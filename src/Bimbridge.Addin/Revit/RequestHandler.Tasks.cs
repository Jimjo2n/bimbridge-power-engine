// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Addin.Revit.Families;
using Bimbridge.Addin.Scripting;
using Bimbridge.Engine;
using Bimbridge.Engine.Tasks;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// 0.6.0 (§4–§6): long resumable tasks. A single command (tarea iniciar) hands over a recipe and its arguments; the engine walks it
    /// in batches on the Revit thread, leaves durable checkpoints, honors pause/Hold/dialogs, saves and exports as verified barriers,
    /// and any client (Codex or Claude) continues it with its id and a compact summary. The recipe code is copied into the task.
    /// </summary>
    public sealed partial class RequestHandler
    {
        private TaskManager _taskMgr;
        private RecipeRegistry _recipes;
        private string _tasksError;
        private readonly object _tasksInit = new object();

        /// <summary>Creates the task manager and hooks the save events (persistence evidence) and Save As (alias).</summary>
        internal void InitTasks()
        {
            lock (_tasksInit)
            {
                if (_taskMgr != null || _tasksError != null) return;
                try
                {
                    var store = new TaskStore();
                    _recipes = new RecipeRegistry(RecipeRegistry.UserFolder, Path.Combine(ScriptHost.AddinDir, "recipes"));
                    _taskMgr = new TaskManager(store, DocRegistry.Instance.InstanceId, t => new RevitTaskBackend(this, t))
                    {
                        // R03: without a grant store, the engine denies every write (DenyWritesAuthority).
                        Authority = Ui.Runtime.Grants != null ? (ITaskAuthority)new GrantAuthority(Ui.Runtime.Grants) : DenyWritesAuthority.Instance,
                        Audit = Ui.Runtime.Audit,
                    };
                    _tracker.Saved += (doc, saveAs) => OnPersisted(doc, saveAs ? "save_as" : "saved");
                    _tracker.Synced += doc => OnPersisted(doc, "sync");
                }
                catch (Exception ex) { _tasksError = ex.GetType().Name + ": " + ex.Message; }
            }
        }

        /// <summary>Revit event (Revit thread): only cheap data is taken; the disk record happens off the thread.</summary>
        private void OnPersisted(Document doc, string kind)
        {
            try
            {
                var id = DocRegistry.Instance.IdentityOf(doc);
                if (id == null) return;
                var ev = new PersistEvidence { Kind = kind, When = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
                try
                {
                    ev.File = doc.PathName;
                    if (!string.IsNullOrEmpty(ev.File) && File.Exists(ev.File))
                    {
                        var fi = new FileInfo(ev.File);
                        ev.Bytes = fi.Length;
                        ev.Mtime = fi.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception) { }
                var key = id.Key;
                var gen = id.Generation;
                var mgr = _taskMgr;
                if (mgr != null) Task.Run(() => { try { mgr.RecordPersist(key, gen, ev); } catch (Exception) { } });
            }
            catch (Exception) { }
        }

        private void OnRekeyed(string oldKey, string newKey)
        {
            try { _ops?.Alias(oldKey, newKey); } catch (Exception) { }
            var mgr = _taskMgr;
            if (mgr != null) Task.Run(() => { try { mgr.Rekey(oldKey, newKey); } catch (Exception) { } });
        }

        private static string ResponseStatusOf(string taskState)
        {
            switch (taskState)
            {
                case TaskState.Applied: return Status.Ok;
                case TaskState.AppliedWithDifferences: return Status.CompletedWithDifferences;
                case TaskState.Partial: return Status.Partial;
                case TaskState.Interrupted: return Status.Interrupted;
                case TaskState.Cancelled: return Status.Cancelled;
                case TaskState.CancelRequested: return Status.CancelRequested;
                case TaskState.WaitingContext: return Status.WaitingContext;
                default: return Status.InProgress;
            }
        }

        /// <summary>The task is held by another Revit = busy (retry there), not a bad argument.</summary>
        private string BusyOrInvalid(string id) => _taskMgr.HeldElsewhere(id) != null ? Status.DocumentBusy : Status.Invalid;

        private Response TaskResponse(Request request, TaskRecord t, bool continuation, Dictionary<string, object> extra = null)
        {
            _taskMgr.Get(t.Id, out var units);
            var d = TaskSummary.Status(t, units, continuation);
            if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
            return new Response { Id = request.Id, State = ResponseStatusOf(t.State), Data = d };
        }

        // ---------------- op tarea ----------------

        private async Task<Response> TaskOp(Session session, Request request, CancellationToken ct)
        {
            InitTasks();
            if (_taskMgr == null)
                return Response.Fail(request.Id, Status.JournalUnavailable, "tasks are not available: their folder could not be prepared (" + _tasksError + ")");
            var a = request.Args;
            var verb = OpSemantics.TaskVerb(a);
            var id = Arg.GetString(a, "id") ?? Arg.GetString(a, Wire.TaskArg);
            var who = session.ClientName ?? session.Id;
            if (verb == "recipes") return Recipes(request);
            if (verb == "list") return TaskList(request);
            if (verb == "start") return await StartTask(session, request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(id)) return Response.Fail(request.Id, Status.Invalid, "missing the task id= (task list shows the ones on this machine)");
            switch (verb)
            {
                case "status":
                case "summary":
                {
                    var t = _taskMgr.Get(id, out _);
                    if (t == null) return Response.Fail(request.Id, Status.Invalid, "task does not exist: " + id);
                    return TaskResponse(request, t, verb == "summary");
                }
                case "wait":
                {
                    var since = Arg.GetLong(a, "from") ?? -1;
                    var max = Math.Max(1, Math.Min(120, Arg.GetLong(a, "max") ?? 30));
                    var t = await _taskMgr.WaitAsync(id, since, TimeSpan.FromSeconds(max), ct).ConfigureAwait(false);
                    if (t == null) return Response.Fail(request.Id, Status.Invalid, "task does not exist: " + id);
                    return TaskResponse(request, t, false);
                }
                case "cancel":
                {
                    var t = _taskMgr.Cancel(id, out var busy);
                    if (t == null) return Response.Fail(request.Id, Status.Invalid, "task does not exist: " + id);
                    if (busy != null) return Response.Fail(request.Id, Status.DocumentBusy, busy);
                    return TaskResponse(request, t, false);
                }
                case "resume":
                {
                    if (!_config.AllowsExec(true)) return Response.Fail(request.Id, Status.Rejected, "this Revit does not allow running recipes (runner/levels in the MCP configuration)");
                    var r = _taskMgr.Resume(id, who, PolicyFrom(a, onlyGiven: true));
                    if (r.Error != null) return Response.Fail(request.Id, r.ErrorStatus ?? Status.Error, r.Error);
                    var t = await WaitBriefly(r.Task, a, ct).ConfigureAwait(false);
                    return TaskResponse(request, t, false, r.AlreadyRunning ? new Dictionary<string, object> { ["already_running"] = true } : null);
                }
                case "reconcile":
                {
                    var dec = (Arg.GetString(a, "decision") ?? "").Trim().ToLowerInvariant();
                    bool applied;
                    if (dec == "applied" || dec == "yes") applied = true;
                    else if (dec == "not_applied" || dec == "no") applied = false;
                    else return Response.Fail(request.Id, Status.Invalid, "reconcile needs decision=applied (already in the model: not repeated) or decision=not_applied (it will run on resume)");
                    var err = _taskMgr.Reconcile(id, Arg.GetStringList(a, "keys"), applied, Arg.GetString(a, "note"), who);
                    if (err != null) return Response.Fail(request.Id, BusyOrInvalid(id), err);
                    return TaskResponse(request, _taskMgr.Get(id, out _), false);
                }
                case "note":
                {
                    var text = Arg.GetString(a, "text");
                    if (string.IsNullOrWhiteSpace(text)) return Response.Fail(request.Id, Status.Invalid, "note needs text=");
                    var err = _taskMgr.Note(id, text, who);
                    if (err != null) return Response.Fail(request.Id, BusyOrInvalid(id), err);
                    return TaskResponse(request, _taskMgr.Get(id, out _), false);
                }
                case "units":
                {
                    var rows = _taskMgr.UnitRows(id, Arg.GetString(a, "status"), (int)(Arg.GetLong(a, "from") ?? 0), (int)(Arg.GetLong(a, "n") ?? 50), out var total);
                    return new Response
                    {
                        Id = request.Id,
                        Data = new Dictionary<string, object>
                        {
                            ["cols"] = new List<object> { "key", "status", "attempts", "ms", "error", "created" },
                            ["rows"] = rows.Cast<object>().ToList(), ["total"] = (long)total,
                        },
                    };
                }
                case "forget":
                {
                    var err = _taskMgr.Archive(id);
                    if (err != null) return Response.Fail(request.Id, BusyOrInvalid(id), err);
                    return new Response { Id = request.Id, Data = new Dictionary<string, object> { ["task"] = id, ["archived"] = true } };
                }
                default:
                    return Response.Fail(request.Id, Status.Invalid, "task: start, status, summary, wait, cancel, resume, reconcile, note, list, units, recipes or forget");
            }
        }

        /// <summary>Short wait after start/resume (espera_s, default 10 s): small tasks come back already finished in ONE call.</summary>
        private async Task<TaskRecord> WaitBriefly(TaskRecord t, IDictionary<string, object> a, CancellationToken ct)
        {
            var wait = Math.Max(0, Math.Min(120, Arg.GetLong(a, "wait_s") ?? 10));
            var until = DateTime.UtcNow.AddSeconds(wait);
            var cur = t;
            while (DateTime.UtcNow < until && _taskMgr.IsRunning(t.Id))
            {
                var left = until - DateTime.UtcNow;
                cur = await _taskMgr.WaitAsync(t.Id, cur.Rev, left, ct).ConfigureAwait(false) ?? cur;
            }
            return _taskMgr.Get(t.Id, out _) ?? cur;
        }

        private static readonly string[] PolicyKeys = { "save_every", "at_end", "on_error", "batch_max", "warnings", "tx" };

        /// <summary>FROZEN hash literals: 0.9.4 capability names in the task content fingerprint. Never change these strings.</summary>
        private static readonly Dictionary<string, string> FrozenCapabilities = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Capability.Modify] = "revit_modificar", [Capability.Save] = "revit_guardar", [Capability.Export] = "revit_exportar",
            [Capability.FileWrite] = "archivo_escritura", [Capability.ReconcileNotApplied] = "reconciliar_no_aplicada",
            [Capability.ExternalRecipe] = "receta_externa",
        };

        private static Dictionary<string, object> PolicyFrom(IDictionary<string, object> a, bool onlyGiven)
        {
            var p = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var k in PolicyKeys)
                if (a.TryGetValue(k, out var v) && v != null) p[k] = v;
            return onlyGiven && p.Count == 0 ? null : p;
        }

        private async Task<Response> StartTask(Session session, Request request, CancellationToken ct)
        {
            var a = request.Args;
            if (!_config.AllowsExec(true))
                return Response.Fail(request.Id, Status.Rejected, "this Revit does not allow running recipes that change the model (runner/levels " + _config.Levels + " in the MCP configuration). Tell the user.");
            var target = DocTarget.Ambient.Value;
            if (target == null) return Response.Fail(request.Id, Status.TargetUnavailable, "there is no open document (or specify doc=)");
            if (WillHold(target.Key))
                return Response.Fail(request.Id, Status.NeedsUser, "Hold is active: a task has no diff to queue. Ask the user to switch Mode to Live for this task.");

            // Recipe: registered by name (receta=), .cs file, saved script (nombre=) or inline code.
            string code, source, recipeName, recipeVersion = null, recipeHash;
            var installedRecipe = false;
            var reg = Arg.GetString(a, "recipe");
            if (reg != null)
            {
                var e = _recipes.Find(reg);
                if (e == null)
                    return Response.Fail(request.Id, Status.Invalid, "there is no recipe '" + reg + "' (task recipes lists the available ones; user recipes go in " + RecipeRegistry.UserFolder + ")");
                code = File.ReadAllText(e.Path);
                source = e.Origin + ":" + e.Path;
                installedRecipe = e.Origin == "installed";
                recipeName = e.Name;
                recipeVersion = e.Version;
            }
            else
            {
                // The agent's own code (codigo=, archivo=, nombre=) is also C# injection: it honors the block. Registered recipes do not.
                var blockedTask = DynamicCodePolicy.Decide(Ui.Runtime.BlockDynamicCode);
                if (blockedTask != null) return Response.Fail(request.Id, Status.Rejected, blockedTask);
                code = ResolveCode(a, out source, out var codeError);
                if (code == null) return Response.Fail(request.Id, Status.Invalid, codeError ?? "missing the recipe: recipe=name, file=path.cs, name= of a saved script or code=");
                recipeName = Arg.GetString(a, "name") ?? (Arg.GetString(a, "file") is string f ? Path.GetFileNameWithoutExtension(f) : "code");
            }
            recipeHash = Home.Sha256Hex(code);

            // R04: a task runs unattended → restricted profile for extra DLLs too (decided before loading them).
            var refs = Arg.GetStringList(a, "refs");
            var refDenied = ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, refs,
                f => { try { return Home.FileSha256(f); } catch (Exception) { return null; } }, ReferencePolicy.LoadTrusted(), ScriptHost.AddinDir);
            if (refDenied.Count > 0)
                return Response.Fail(request.Id, Status.Rejected, "referencia_no_confiable: " + string.Join("; ", refDenied.Take(5)) + ". Add its SHA-256 to " + ReferencePolicy.TrustedListPath + " (a person decides).");
            // Compile now (off the Revit thread) to fail fast; the task keeps the exact copy of the code.
            var compiled = await Task.Run(() =>
            {
                var c = ScriptHost.Compile(code, _revitVersion, _addinVersion, refs, out var err);
                return Tuple.Create(c, err);
            }, ct).ConfigureAwait(false);
            if (compiled.Item1 == null) return Response.Fail(request.Id, Status.Invalid, compiled.Item2);
            // R03: what the agent asks for (capacidades=) is only NOTED as requested. What is never granted is rejected right away.
            var requested = Arg.GetStringList(a, "capabilities").Where(c => Capability.Grantable.Contains(c)).ToList();
            var denied = CapabilityPolicy.Denied(ExecutionProfile.RestrictedAutonomous, compiled.Item1.Capabilities, Capability.Grantable);
            if (denied.Count > 0) return Response.Fail(request.Id, Status.Rejected, CapabilityPolicy.Explain(denied));
            var writes = true;
            if (compiled.Item1.RecipeType != null)
            {
                // rc3: NOT instantiated (the constructor is recipe code and there is no authorization yet). Metadata from the source code;
                // if they are not constants, assume the conservative case: it writes (revit_modificar).
                recipeVersion = recipeVersion ?? ScriptHost.RecipeConstant(code, "Version") ?? "1";
                writes = ScriptHost.RecipeConstant(code, "Transaction") != nameof(TxPolicy.ReadOnly);
                if (compiled.Item1.RecipeType.GetConstructor(Type.EmptyTypes) == null)
                    return Response.Fail(request.Id, Status.Invalid, "the recipe needs a public parameterless constructor");
            }

            var userArgs = a.TryGetValue("args", out var ua) && ua is Dictionary<string, object> ud ? ud : new Dictionary<string, object>(StringComparer.Ordinal);
            if (installedRecipe) LegacyAliases.NormalizeRecipeArgs(userArgs);   // 0.9.4 input names of our own recipes; user recipes keep theirs
            var policy = PolicyFrom(a, onlyGiven: false);
            if (refs.Count > 0) policy["refs"] = refs.Cast<object>().ToList();
            // Capabilities the units need (barriers are checked when reached): validated at every execution point.
            var requires = compiled.Item1.Capabilities.Select(CapabilityPolicy.Kind).Where(k => Capability.Grantable.Contains(k)).ToList();
            if (writes) requires.Add(Capability.Modify);
            // R04: the closed set of what runs unattended is the installed recipes (verified by the installer). Any other
            // autonomous code (read-only too) requires a person to approve THAT task (which pins the recipe's SHA-256).
            if (!installedRecipe) requires.Add(Capability.ExternalRecipe);
            requires = requires.Distinct().ToList();
            policy["requires"] = requires.Cast<object>().ToList();
            if (requested.Count > 0) policy["requested"] = requested.Cast<object>().ToList();
            // FROZEN hash literals (0.9.4 key and capability names): re-sending task start for a task created by 0.9.x is not "other content".
            var content = OpSemantics.Canonical(new Dictionary<string, object>
            {
                ["receta"] = recipeHash, ["args"] = userArgs, ["doc"] = target.Key, ["huella_doc"] = target.Print ?? "", ["refs"] = refs.Cast<object>().ToList(),
                ["requiere"] = requires.Select(r => (object)(FrozenCapabilities.TryGetValue(r, out var legacy) ? legacy : r)).ToList(),
            });
            var draft = new TaskRecord
            {
                Id = TaskStore.SafeId(Arg.GetString(a, "id") ?? Arg.GetString(a, Wire.TaskArg) ?? TaskStore.NewId(DateTime.UtcNow)),
                Objective = Arg.GetString(a, "target"),
                Scope = Arg.GetString(a, "scope"),
                RecipeName = recipeName,
                RecipeVersion = recipeVersion ?? "1",
                RecipeHash = recipeHash,
                RecipeSource = source,
                ContentHash = Home.Sha256Hex(content).Substring(0, 16),
                Args = userArgs,
                Docs = new List<TaskDoc> { new TaskDoc { Key = target.Key, Print = target.Print, Generation = target.Generation, Handle = target.Id, Title = target.Title } },
                Policy = policy,
            };
            var r = _taskMgr.Start(draft, code, session.ClientName ?? session.Id);
            if (r.Error != null) return Response.Fail(request.Id, r.ErrorStatus ?? Status.Error, r.Error);
            var t = await WaitBriefly(r.Task, a, ct).ConfigureAwait(false);
            var extra = new Dictionary<string, object>
            {
                ["recipe"] = recipeName + " v" + draft.RecipeVersion, ["fingerprint"] = draft.ContentHash,
                ["compile_ms"] = compiled.Item1.FromCache ? 0L : compiled.Item1.CompileMs,
            };
            if (!r.Created) extra["already_existed"] = true;
            if (r.AlreadyRunning) extra["already_running"] = true;
            // What still needs authorization is left as a REQUEST for a person («Authorize» button in Revit). The agent cannot approve it.
            var need = requires.Concat(requested).Distinct().ToList();
            if (Ui.Runtime.Grants != null && need.Count > 0)
            {
                var missing = need.Where(c => Ui.Runtime.Grants.Check(draft.Id, target.Key, target.Print, c, DateTime.UtcNow) != null).ToList();
                if (missing.Count > 0)
                {
                    Ui.Runtime.Grants.Request(draft.Id, target.Key, target.Print, target.Title, missing, session.ClientName ?? session.Id, draft.Objective, DateTime.UtcNow);
                    Ui.Runtime.MarkDirty();
                    extra["authorization"] = new Dictionary<string, object>
                    {
                        ["pending"] = missing.Cast<object>().ToList(),
                        ["as"] = "a person must press «Authorize» on the BIMBRIDGE tab in Revit; then: task resume " + draft.Id,
                    };
                }
            }
            return TaskResponse(request, t, false, extra);
        }

        private Response Recipes(Request request)
        {
            var rows = _recipes.List().Select(e => (object)new object[] { e.Name, e.Version, e.Origin, e.Description, e.Hash.Substring(0, 12) }).ToList();
            return new Response
            {
                Id = request.Id,
                Data = new Dictionary<string, object>
                {
                    ["cols"] = new List<object> { "name", "version", "origin", "description", "sha" }, ["rows"] = rows,
                    ["user_folder"] = RecipeRegistry.UserFolder,
                },
            };
        }

        private Response TaskList(Request request)
        {
            var rows = _taskMgr.List().OrderByDescending(t => t.Updated ?? t.Created, StringComparer.Ordinal).Take(50)
                .Select(t => (object)new object[] { t.Id, t.State, t.RecipeName, t.MainDoc?.Title, t.UnitsTotal, t.ConfirmSeq, t.Updated ?? t.Created }).ToList();
            return new Response
            {
                Id = request.Id,
                Data = new Dictionary<string, object>
                {
                    ["cols"] = new List<object> { "id", "status", "recipe", "doc", "units", "committed_items", "updated" }, ["rows"] = rows,
                },
            };
        }

        // ---------------- Revit backend ----------------

        /// <summary>A script with Run(ctx) used as a task: a single unit.</summary>
        private sealed class RunAsRecipe : Recipe
        {
            private readonly System.Reflection.MethodInfo _run;
            public RunAsRecipe(System.Reflection.MethodInfo run) { _run = run; }
            public override IEnumerable<Unit> Units(ScriptContext ctx) { yield return new Unit("unique"); }
            public override object Run(ScriptContext ctx, Unit u)
            {
                try { return _run.Invoke(null, new object[] { ctx }); }
                catch (System.Reflection.TargetInvocationException tie) { throw tie.InnerException ?? tie; }
            }
        }

        /// <summary>
        /// Revit side of the engine (§4): enumerates and runs units on the Revit thread with one transaction per unit, detects conflicts with
        /// foreign changes (author other than the task), verifies doubtful ones by UniqueId or with the recipe's Verify, and runs the
        /// guardar/sync/pdf barriers with file verification. Takes the document (DocLocks) while the task runs and releases it when done.
        /// </summary>
        private sealed class RevitTaskBackend : ITaskBackend, IDisposable
        {
            private readonly RequestHandler _h;
            private readonly TaskRecord _t;
            private readonly string _owner;
            private readonly NamedSets _sets = new NamedSets();
            private readonly ScriptContext.TaskSink _sink;
            private bool _locked;
            private Recipe _recipe;
            private ScriptHost.Compiled _compiled;
            private readonly RecipeGate _gate;
            private UIApplication _gateApp;
            private string _compileError;
            private IEnumerator<Unit> _enum;
            private bool _enumDone;
            private bool _prepared;
            private readonly List<Unit> _found = new List<Unit>();
            private readonly Dictionary<string, Unit> _byKey = new Dictionary<string, Unit>(StringComparer.Ordinal);
            private readonly Dictionary<string, Dictionary<long, long>> _snap = new Dictionary<string, Dictionary<long, long>>(StringComparer.Ordinal);
            private static readonly TimeSpan BusyAfter = TimeSpan.FromSeconds(20);

            public RevitTaskBackend(RequestHandler h, TaskRecord t)
            {
                _h = h;
                _t = t;
                _owner = "tarea:" + t.Id;   // document lease owner identity shared with 0.9.3 processes and stored leases: kept verbatim
                _sink = new ScriptContext.TaskSink { TaskId = t.Id, ArtifactsDir = h._taskMgr.Store.ArtifactsDir(t.Id) };
                // rc3: recipe boundary. Authority = the engine's last safe point (leases + grants); with no engine running, nothing.
                _gate = new RecipeGate(() => _t.Preflight != null ? _t.Preflight() : "the task is not running", WriteCounter);
            }

            /// <summary>Write counter across all open documents: tracker revision (goes up with each committed
            /// transaction) + 1e12 per document with an open transaction. Only read on the Revit thread (inside OnRevit).</summary>
            private long WriteCounter()
            {
                long n = 0;
                if (_gateApp == null) return 0;
                foreach (Document d in _gateApp.Application.Documents)
                {
                    if (d.IsLinked) continue;
                    n += _h._tracker.For(d)?.Rev ?? 0;
                    if (d.IsModifiable) n += 1000000000000L;
                }
                return n;
            }

            /// <summary>Runs recipe code inside the boundary (on the Revit thread). null = fine; otherwise the reason.</summary>
            private string Guard(UIApplication app, string phase, Action body)
            {
                _gateApp = app;
                try { return _gate.Run(phase, body); }
                finally { _gateApp = null; }
            }

            /// <summary>Creates the recipe instance (constructor and initializers) only after authorization, on the Revit thread.</summary>
            private string EnsureInstance(UIApplication app)
            {
                if (_recipe != null) return null;
                if (_compiled == null) return _compileError ?? "recipe not compiled";
                var c = _compiled;
                Recipe r = null;
                var why = Guard(app, RecipeGate.Phase.Constructor, () => r = c.RecipeType != null ? (Recipe)Activator.CreateInstance(c.RecipeType) : new RunAsRecipe(c.Run));
                if (why != null) return why;
                // rc3: the actual policy must match what was authorized at start (metadata read from the code). If the recipe writes
                // but the task was authorized as read-only (no revit_modificar), it does not continue.
                TxPolicy tx = TxPolicy.PerUnit;
                why = Guard(app, RecipeGate.Phase.Constructor, () => tx = r.Transaction);
                if (why != null) return why;
                var req = _t.Policy.TryGetValue("requires", out var rq) && rq is List<object> rl ? rl.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList() : null;
                if (tx != TxPolicy.ReadOnly && req != null && !req.Contains(Capability.Modify))
                    return "the recipe declares Transaction=" + tx + " but the task was authorized as read-only: it is not run (restart the task to request revit_modify)";
                _recipe = r;
                return null;
            }

            public void Dispose()
            {
                if (_locked && _t.MainDoc?.Key != null) _h.Locks.Release(new[] { _t.MainDoc.Key }, _owner);
                _locked = false;
                try { _enum?.Dispose(); } catch (Exception) { }
            }

            private DocRegistry.Info Info => DocRegistry.Instance.Snapshot.FirstOrDefault(i => i.Key == _t.MainDoc?.Key);

            public string CurrentGeneration(TaskRecord task) => Info?.Generation;

            public BackendResult<bool> CheckContext(TaskRecord task)
            {
                if (!_h._config.AllowsExec(true)) return BackendResult<bool>.Fail("the MCP configuration no longer allows running recipes in this Revit");
                if (_h._pause != null && _h._pause.On) return BackendResult<bool>.Waiting("the user paused the AI from Revit", "the user presses Resume AI; the task continues on its own");
                var key = _t.MainDoc?.Key;
                if (_h.WillHold(key)) return BackendResult<bool>.Waiting("Hold is active (or there are plans in this document's queue)", "the user switches Mode to Live");
                var info = Info;
                if (info == null) return BackendResult<bool>.Waiting("document " + (_t.MainDoc?.Title ?? key) + " is not open in this Revit", "open it; the task continues on its own");
                if (_t.MainDoc.Print != null && info.Print != null && info.Print != _t.MainDoc.Print)
                    return BackendResult<bool>.Fail("file " + key + " now contains ANOTHER model (fingerprint " + info.Print + " ≠ " + _t.MainDoc.Print + "): the task does not continue on a different document");
                if (!_locked)
                {
                    if (!_h.Locks.TryAcquire(new[] { key }, _owner, "task", _t.Id, out var blocking))
                        return BackendResult<bool>.Waiting("the document is held by " + blocking.Describe(), "wait for it to finish");
                    _locked = true;
                }
                return BackendResult<bool>.Ok(true);
            }

            private Task<WorkResult> OnRevit(Func<UIApplication, Stopwatch, int, bool> step, CancellationToken ct) =>
                _h._main.RunAsync((app, sw, budget) =>
                {
                    using (DocTarget.Enter(null)) return step(app, sw, budget);
                }, false, BusyAfter, ct);

            private static BackendResult<T> FromWork<T>(WorkResult r)
            {
                if (r.Kind == WorkResult.KindBusy) return BackendResult<T>.Waiting("Revit is busy (a command, a dialog or an edit mode is open)", "finish or close whatever is open in Revit");
                if (r.Kind == WorkResult.KindCancelled) return BackendResult<T>.Waiting("the Revit visit was cancelled", null);
                return BackendResult<T>.Fail("internal error on the Revit thread: " + (r.Error?.Message ?? r.Kind));
            }

            private Document FindDoc(UIApplication app)
            {
                foreach (Document d in app.Application.Documents)
                {
                    if (d.IsLinked) continue;
                    var id = DocRegistry.Instance.IdentityOf(d);
                    if (id != null && id.Key == _t.MainDoc.Key) return d;
                }
                return null;
            }

            private ScriptContext Ctx(UIApplication app, Document doc, CancellationToken ct)
            {
                var ctx = new ScriptContext(app, doc, _t.Args, OpSemantics.ModeRun, "task", ct, _sets, DocRegistry.Instance.IdOf(doc)) { Sink = _sink };
                return ctx;
            }

            /// <summary>Compiles the recipe copy saved in the task (not the current file: if it changed, the task keeps its own).</summary>
            private bool EnsureRecipe(out string error)
            {
                error = null;
                if (_compiled != null) return true;
                if (_compileError != null) { error = _compileError; return false; }
                var code = _h._taskMgr.Store.RecipeCode(_t.Id);
                if (code == null) { _compileError = "the recipe copy is missing from the task folder"; error = _compileError; return false; }
                if (Home.Sha256Hex(code) != _t.RecipeHash) { _compileError = "the task's recipe copy does not match its fingerprint (file altered)"; error = _compileError; return false; }
                var refs = _t.Policy.TryGetValue("refs", out var rv) && rv is List<object> rl ? rl.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList() : new List<string>();
                var c = ScriptHost.Compile(code, _h._revitVersion, _h._addinVersion, refs, out var err);
                if (c == null) { _compileError = "the recipe no longer compiles in this Revit: " + err; error = _compileError; return false; }
                // The authority decides what is grantable at each execution point; here only what is never granted is cut.
                var denied = CapabilityPolicy.Denied(ExecutionProfile.RestrictedAutonomous, c.Capabilities, Capability.Grantable);
                if (denied.Count > 0) { _compileError = CapabilityPolicy.Explain(denied); error = _compileError; return false; }
                // rc3: NOT instantiated here: the constructor is recipe code and only runs after authorization (EnsureInstance).
                _compiled = c;
                return true;
            }

            private TxPolicy TxPolicy
            {
                get
                {
                    var p = (_t.PolicyString("tx") ?? "").ToLowerInvariant();
                    if (p == "none") return TxPolicy.None;
                    if (p == "read") return TxPolicy.ReadOnly;
                    return _recipe?.Transaction ?? TxPolicy.PerUnit;
                }
            }

            /// <summary>Walks the recipe's lazy enumeration in slices (several visits if needed).</summary>
            private async Task<BackendResult<bool>> EnsureEnumerated(CancellationToken ct)
            {
                if (!EnsureRecipe(out var err)) return BackendResult<bool>.Fail(err);
                while (!_enumDone)
                {
                    string fail = null;
                    var r = await OnRevit((app, watch, budget) =>
                    {
                        var doc = FindDoc(app);
                        if (doc == null) { fail = "closed"; return true; }
                        var ctx = Ctx(app, doc, ct);
                        var reg = _h._tracker.For(doc);
                        try
                        {
                            var g = EnsureInstance(app);
                            if (g != null) { fail = g; return true; }
                            if (!_prepared)
                            {
                                g = Guard(app, RecipeGate.Phase.Prepare, () => _recipe.Prepare(ctx));
                                if (g != null) { fail = g; return true; }
                                _prepared = true;
                            }
                            if (_enum == null)
                            {
                                g = Guard(app, RecipeGate.Phase.Enumerate, () => _enum = _recipe.Units(ctx).GetEnumerator());
                                if (g != null) { fail = g; return true; }
                            }
                            var limit = Math.Max(20, budget);
                            while (watch.ElapsedMilliseconds < limit)
                            {
                                var more = false;
                                g = Guard(app, RecipeGate.Phase.Enumerate, () => more = _enum.MoveNext());
                                if (g != null) { fail = g; return true; }
                                if (!more) { _enumDone = true; break; }
                                var u = _enum.Current;
                                if (u == null) continue;
                                _found.Add(u);
                                if (!_byKey.ContainsKey(u.Key)) _byKey[u.Key] = u;
                                if (u.Ids.Count > 0 && reg != null) _snap[u.Key] = reg.Snapshot(u.Ids);
                            }
                        }
                        catch (Exception ex) { fail = "the recipe failed while enumerating units: " + Describe(ex); }
                        return true;
                    }, ct).ConfigureAwait(false);
                    if (!r.Ok) return FromWork<bool>(r);
                    if (fail == "closed") return BackendResult<bool>.Waiting("the document is not open", "open it; the task continues on its own");
                    if (fail != null) return BackendResult<bool>.Fail(fail);
                }
                return BackendResult<bool>.Ok(true);
            }

            public async Task<BackendResult<List<UnitRef>>> EnumerateAsync(TaskRecord task, CancellationToken ct)
            {
                var e = await EnsureEnumerated(ct).ConfigureAwait(false);
                if (e.Value != true) return new BackendResult<List<UnitRef>> { Wait = e.Wait, Action = e.Action, Fatal = e.Fatal };
                return BackendResult<List<UnitRef>>.Ok(_found.Select(u => new UnitRef
                {
                    Key = u.Key, Barrier = u.BarrierAction,
                    BarrierArgs = u.BarrierArgs != null ? new Dictionary<string, object>(u.BarrierArgs) : null,
                }).ToList());
            }

            public async Task<BackendResult<List<UnitOutcome>>> RunBatchAsync(TaskRecord task, IReadOnlyList<UnitRef> units, int budgetMs, CancellationToken ct)
            {
                if (!EnsureRecipe(out var err)) return BackendResult<List<UnitOutcome>>.Fail(err);
                var outcomes = new List<UnitOutcome>();
                string wait = null, waitAction = null;
                var r = await OnRevit((app, watch, budget) =>
                {
                    var doc = FindDoc(app);
                    if (doc == null) { wait = "the document is not open"; waitAction = "open it; the task continues on its own"; return true; }
                    var reg = _h._tracker.For(doc);
                    var ctx = Ctx(app, doc, ct);
                    var limit = Math.Max(10, Math.Min(budget, budgetMs));
                    _h._tracker.CurrentWriter = _owner;
                    try
                    {
                        foreach (var uref in units)
                        {
                            if (outcomes.Count > 0 && (watch.ElapsedMilliseconds >= limit || ct.IsCancellationRequested)) break;
                            // R06/R03: last safe point, on the Revit thread, right before opening this unit's transaction.
                            if (task.Preflight?.Invoke() is string refusal) { Ui.Runtime.Log("Task " + task.Id + ": unit " + uref.Key + " not run (" + refusal + ")"); break; }
                            if (!_byKey.TryGetValue(uref.Key, out var u))
                            {
                                outcomes.Add(UnitOutcome.Of(uref.Key, UnitState.RolledBack, "the recipe no longer enumerates this unit"));
                                continue;
                            }
                            var o = RunUnit(doc, reg, ctx, u, out var stopWait);
                            if (o == null) { wait = stopWait; waitAction = "close the edit mode or command open in Revit"; break; }
                            outcomes.Add(o);
                            if (o.State == UnitState.Partial || o.State == UnitState.Unknown) break;
                        }
                    }
                    finally { _h._tracker.CurrentWriter = null; }
                    return true;
                }, ct).ConfigureAwait(false);
                if (!r.Ok && outcomes.Count == 0) return FromWork<List<UnitOutcome>>(r);
                return new BackendResult<List<UnitOutcome>> { Value = outcomes, Wait = wait, Action = waitAction };
            }

            /// <summary>One unit: conflict → transaction → Run → materialized result → checked commit. null = could not even start.</summary>
            private UnitOutcome RunUnit(Document doc, VersionRegistry reg, ScriptContext ctx, Unit u, out string wait)
            {
                wait = null;
                var sw = Stopwatch.StartNew();
                var o = new UnitOutcome { Key = u.Key };
                if (reg != null && _snap.TryGetValue(u.Key, out var snap) && snap.Count > 0)
                {
                    var conflicts = reg.FindConflicts(snap).Where(c => c.Author != _owner).ToList();
                    if (conflicts.Count > 0)
                    {
                        o.State = UnitState.Conflict;
                        o.Error = conflicts.Count + " elements changed since enumeration: " + string.Join("; ", conflicts.Take(3).Select(c => c.Describe(_owner))) +
                                  ". Not overwritten; resuming re-enumerates it against the current model.";
                        return o;
                    }
                }
                var rev0 = reg?.Rev ?? 0;
                bool Changed() => (reg?.Rev ?? 0) != rev0;
                var policy = TxPolicy;
                var failures = new FailureLog { DeleteWarnings = !string.Equals(_t.PolicyString("warnings"), "leave", StringComparison.OrdinalIgnoreCase) };
                Transaction t = null;
                _sink.Clear();
                try
                {
                    if (policy == TxPolicy.PerUnit)
                    {
                        t = new Transaction(doc, DocumentTracker.TransactionPrefix + " task " + _t.Id + " " + Clip(u.Key, 60));
                        FailureLog.Attach(t, failures);
                        var ts = t.Start();
                        if (ts != TransactionStatus.Started) { wait = "Revit does not allow opening a transaction now (" + ts + ")"; return null; }
                    }
                    var value = _recipe.Run(ctx, u);
                    object json;
                    try { json = ResultSerializer.ToJson(value, doc, out _, out _); }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { throw new InvalidOperationException("the unit result could not be converted: " + ex.Message, ex); }
                    o.Result = json;
                    if (t != null)
                    {
                        var st = t.Commit();
                        if (st == TransactionStatus.RolledBack)
                        {
                            o.State = UnitState.RolledBack;
                            o.Error = "Revit rejected the transaction and rolled it back: " + string.Join(" | ", failures.Errors.Take(3));
                        }
                        else if (st != TransactionStatus.Committed)
                        {
                            o.State = UnitState.Unknown;
                            o.Error = "the transaction was left in status " + st + " (there may be a warning pending on screen)";
                        }
                        else o.State = UnitState.Committed;
                    }
                    else if (policy == TxPolicy.ReadOnly && Changed())
                    {
                        o.State = UnitState.Partial;
                        o.Error = "the recipe is read-only but the model changed during the unit";
                    }
                    else o.State = UnitState.Committed;
                    if (failures.Warnings.Count > 0) o.Decisions.Add("Revit warnings in " + u.Key + ": " + string.Join(" | ", failures.Warnings.Take(3)));
                }
                catch (SkipUnit om)
                {
                    var rolled = RollBack(t);
                    if (Changed() && (policy != TxPolicy.PerUnit || !rolled))
                    {
                        o.State = UnitState.Partial;
                        o.Error = "skip was requested, but committed changes remained: " + om.Message;
                    }
                    else { o.State = UnitState.Skipped; o.Error = om.Message; }
                }
                catch (Exception ex)
                {
                    var rolled = RollBack(t);
                    var what = (ex is OperationCanceledException ? "cancelled: " : "") + Describe(ex);
                    if (!Changed() && rolled) { o.State = UnitState.RolledBack; o.Error = what + " (nothing was left applied)"; }
                    else { o.State = UnitState.Partial; o.Error = what + ". WARNING: committed changes remained; review and reconcile"; }
                }
                finally
                {
                    try { t?.Dispose(); } catch (Exception) { }
                }
                o.Ms = sw.ElapsedMilliseconds;
                if (reg != null && Changed() && (o.State == UnitState.Committed || o.State == UnitState.Partial))
                {
                    foreach (var id in reg.AddedSince(rev0))
                    {
                        string uid = null;
                        try { uid = doc.GetElement(Ids.ToId(id))?.UniqueId; } catch (Exception) { }
                        if (uid != null) o.Created.Add(new object[] { id, uid });
                    }
                    var cnt = reg.CountSince(rev0);
                    o.Changed = cnt.Key;
                    o.Deleted = cnt.Value;
                }
                o.Decisions.AddRange(_sink.Decisions);
                o.Exceptions.AddRange(_sink.Exceptions.Select(x => new Dictionary<string, object>(x)));
                o.Artifacts.AddRange(_sink.Artifacts.Select(x => new Dictionary<string, object>(x)));
                foreach (var line in ctx.LogLines) if (o.Decisions.Count < 20) o.Decisions.Add("log: " + line);
                ctx.LogLines.Clear();
                _sink.Clear();
                return o;
            }

            private static bool RollBack(Transaction t)
            {
                if (t == null) return true;
                try { return t.GetStatus() != TransactionStatus.Started || t.RollBack() == TransactionStatus.RolledBack; }
                catch (Exception) { return false; }
            }

            public async Task<BackendResult<UnitOutcome>> RunBarrierAsync(TaskRecord task, UnitRef barrier, CancellationToken ct)
            {
                var verb = (barrier.Barrier ?? "").ToLowerInvariant();
                if (verb != "save" && verb != "sync" && verb != "sync" && verb != "pdf")
                    return BackendResult<UnitOutcome>.Ok(UnitOutcome.Of(barrier.Key, UnitState.RolledBack, "unknown barrier '" + barrier.Barrier + "' (save, sync or pdf)"));
                if (!_h._config.AllowsDocOps)
                    return BackendResult<UnitOutcome>.Ok(UnitOutcome.Of(barrier.Key, UnitState.RolledBack, "the MCP configuration does not allow save/sync/export in this Revit"));
                var sw = Stopwatch.StartNew();
                var diffs = new List<string>();
                var pend = new DocOps.Pending();
                Dictionary<string, object> data = null;
                string fail = null, wait = null, path = null;
                var bargs = barrier.BarrierArgs ?? new Dictionary<string, object>();
                var r = await OnRevit((app, watch, budget) =>
                {
                    var doc = FindDoc(app);
                    if (doc == null) { wait = "the document is not open"; return true; }
                    _h._tracker.CurrentWriter = _owner;
                    try
                    {
                        switch (verb)
                        {
                            case "save": data = DocOps.Save(app, doc, bargs, pend, diffs); break;
                            case "sync":  data = DocOps.Sync(doc, bargs, diffs, _h._tracker); break;
                            case "pdf": data = DocOps.Pdf(doc, bargs, _sets, DocRegistry.Instance.IdOf(doc), pend, diffs); break;
                        }
                        try { path = doc.IsModelInCloud ? ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetCloudModelPath()) : doc.PathName; } catch (Exception) { path = doc.PathName; }
                    }
                    catch (OpFail ex) { fail = ex.Message; }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { fail = ex.GetType().Name + ": " + ex.Message; }
                    finally { _h._tracker.CurrentWriter = null; }
                    return true;
                }, ct).ConfigureAwait(false);
                if (!r.Ok) return FromWork<UnitOutcome>(r);
                if (wait != null) return BackendResult<UnitOutcome>.Waiting(wait, "open it; the task continues on its own");
                var o = new UnitOutcome { Key = barrier.Key };
                if (fail != null) { o.State = UnitState.RolledBack; o.Error = fail; o.Ms = sw.ElapsedMilliseconds; return BackendResult<UnitOutcome>.Ok(o); }
                List<object> files = null;
                if (pend.Files.Count > 0)
                {
                    var expected = data != null && data.TryGetValue("n_sheets", out var nh) && nh is long l ? (int)l : 0;
                    files = await Task.Run(() => DocOps.VerifyFiles(pend, expected, diffs), ct).ConfigureAwait(false);
                    data["files"] = files;
                }
                o.State = UnitState.Committed;
                o.Result = data;
                o.Ms = sw.ElapsedMilliseconds;
                foreach (var d in diffs) o.Exceptions.Add(new Dictionary<string, object> { ["reason"] = "barrier " + verb + ": " + d });
                var first = files?.OfType<Dictionary<string, object>>().FirstOrDefault();
                if (verb == "pdf")
                {
                    foreach (var f in files?.OfType<Dictionary<string, object>>() ?? Enumerable.Empty<Dictionary<string, object>>())
                        o.Artifacts.Add(new Dictionary<string, object>
                        {
                            ["path"] = f.TryGetValue("file", out var p) ? p : null, ["sha256"] = f.TryGetValue("sha256", out var s) ? s : null,
                            ["bytes"] = f.TryGetValue("bytes", out var b) ? b : null, ["pages"] = f.TryGetValue("pages", out var pg) ? pg : null, ["type"] = "pdf",
                        });
                }
                else
                {
                    o.Persist = new PersistEvidence
                    {
                        Kind = verb == "save" ? "save" : "sync", When = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        File = (first != null && first.TryGetValue("file", out var fp) ? fp as string : null) ?? path,
                        Bytes = first != null && first.TryGetValue("bytes", out var fb) && fb is long lb ? lb : 0,
                        Mtime = first != null && first.TryGetValue("written_utc", out var fm) ? fm as string : null,
                        Sha = first != null && first.TryGetValue("sha256", out var fs) ? fs as string : null,
                    };
                }
                return BackendResult<UnitOutcome>.Ok(o);
            }

            public async Task<BackendResult<List<UnitOutcome>>> VerifyAsync(TaskRecord task, IReadOnlyList<UnitRecord> units, bool reopened, CancellationToken ct)
            {
                // The recipe's Verify needs the enumerated units (their data); only if some cannot be verified by UniqueId.
                if (units.Any(u => u.Created.Count == 0))
                {
                    var e = await EnsureEnumerated(ct).ConfigureAwait(false);
                    if (e.Value != true) return new BackendResult<List<UnitOutcome>> { Wait = e.Wait, Action = e.Action, Fatal = e.Fatal };
                }
                var outcomes = new List<UnitOutcome>();
                string wait = null;
                var curGen = CurrentGeneration(task);
                var r = await OnRevit((app, watch, budget) =>
                {
                    var doc = FindDoc(app);
                    if (doc == null) { wait = "the document is not open"; return true; }
                    var ctx = Ctx(app, doc, ct);
                    foreach (var u in units) outcomes.Add(VerifyOne(app, doc, ctx, u, reopened, curGen));
                    return true;
                }, ct).ConfigureAwait(false);
                if (!r.Ok) return FromWork<List<UnitOutcome>>(r);
                if (wait != null) return BackendResult<List<UnitOutcome>>.Waiting(wait, "open it; the task continues on its own");
                return BackendResult<List<UnitOutcome>>.Ok(outcomes);
            }

            private UnitOutcome VerifyOne(UIApplication _verifyApp, Document doc, ScriptContext ctx, UnitRecord u, bool reopened, string curGen)
            {
                var o = new UnitOutcome { Key = u.Key };
                // 1) Evidence in the model: the elements it created (UniqueId).
                if (u.Created.Count > 0)
                {
                    var exist = u.Created.Count(c => c.Length > 1 && c[1] is string uid && SafeGet(doc, uid) != null);
                    if (exist == u.Created.Count) { o.State = UnitState.Committed; o.Error = "verified: its " + exist + " created elements exist"; o.Created = u.Created.ToList(); return o; }
                    if (exist == 0 && reopened) { o.State = UnitState.Pending; o.Error = "verified: none of its created elements is in the reopened model (lost when closing without saving)"; return o; }
                    o.State = UnitState.Unknown;
                    o.Error = exist + " of the " + u.Created.Count + " elements it created exist: review and reconcile";
                    return o;
                }
                // 2) The recipe's Verify.
                var v = Verification.Unknown;
                if (_byKey.TryGetValue(u.Key, out var unit))
                {
                    try
                    {
                        var g = _recipe == null ? "recipe not instantiated" : Guard(_verifyApp, RecipeGate.Phase.Verify, () => v = _recipe.Verify(ctx, unit));
                        if (g != null) { v = Verification.Unknown; o.Error = g; }
                    }
                    catch (Exception ex) { o.Error = "Verify failed: " + Describe(ex); }
                }
                if (v == Verification.Applied) { o.State = UnitState.Committed; o.Error = "verified by the recipe"; return o; }
                if (v == Verification.NotApplied) { o.State = UnitState.Pending; o.Error = "the recipe verified that it is not applied"; return o; }
                // 3) Safe inference only after reopening: it started after the last save of ITS opening → it is not in the file.
                if (reopened && u.Generation != null && u.Generation != curGen)
                {
                    var saved = _t.PersistedSince(u.Generation, u.Started);
                    if (saved == false)
                    {
                        o.State = UnitState.Pending;
                        o.Error = "inferred: it started (" + u.Started + ") after the last save of opening " + u.Generation + " and the document was reopened without saving it";
                        return o;
                    }
                }
                o.State = UnitState.Unknown;
                o.Error = (o.Error != null ? o.Error + "; " : "") + "cannot be checked in the model (the recipe does not implement Verify and created no elements)";
                return o;
            }

            private static Element SafeGet(Document doc, string uid)
            {
                try { return doc.GetElement(uid); } catch (Exception) { return null; }
            }

            public async Task<BackendResult<Dictionary<string, object>>> PostconditionAsync(TaskRecord task, CancellationToken ct)
            {
                if (!EnsureRecipe(out var err)) return BackendResult<Dictionary<string, object>>.Fail(err);
                Dictionary<string, object> result = null;
                string wait = null;
                var r = await OnRevit((app, watch, budget) =>
                {
                    var doc = FindDoc(app);
                    if (doc == null) { wait = "the document is not open"; return true; }
                    var ctx = Ctx(app, doc, ct);
                    // Read-only QC: inside a group that is always rolled back (whatever it tries to change does not stick).
                    TransactionGroup g = null;
                    using (_h._tracker.Suspend())
                    {
                        try
                        {
                            // P0: with unsaved changes the temporary group is not opened (Revit will reject any attempt to modify).
                            if (Engine.TrialGuard.MayOpenTemporaryGroup(doc.IsModified))
                            {
                                g = new TransactionGroup(doc, DocumentTracker.TemporaryPrefix + " qc task");
                                g.Start();
                            }
                            object value = null;
                            var pg = EnsureInstance(app) ?? Guard(app, RecipeGate.Phase.Postcondition, () => value = _recipe.Postcondition(ctx));
                            if (pg != null) throw new InvalidOperationException(pg);
                            var json = value == null ? null : ResultSerializer.ToJson(value, doc, out _, out _);
                            result = json as Dictionary<string, object> ?? (json == null ? new Dictionary<string, object>() : new Dictionary<string, object> { ["result"] = json });
                        }
                        catch (Exception ex)
                        {
                            result = new Dictionary<string, object> { ["deviations"] = new List<object> { "the postcondition failed: " + Describe(ex) } };
                        }
                        finally
                        {
                            try { if (g != null && g.HasStarted()) g.RollBack(); } catch (Exception) { }
                            try { g?.Dispose(); } catch (Exception) { }
                        }
                    }
                    return true;
                }, ct).ConfigureAwait(false);
                if (!r.Ok) return FromWork<Dictionary<string, object>>(r);
                if (wait != null) return BackendResult<Dictionary<string, object>>.Waiting(wait, "open it; the task continues on its own");
                return BackendResult<Dictionary<string, object>>.Ok(result);
            }

            private static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
        }
    }
}
