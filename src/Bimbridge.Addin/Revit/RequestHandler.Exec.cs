// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Addin.Revit.Families;
using Bimbridge.Addin.Scripting;
using Bimbridge.Engine;
using Bimbridge.Engine.Families;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// 0.5.0: identified documents (docs), general C# runner (exec), families and connectors (fam) and document operations (doc).
    /// Same protections as the rest: Revit thread via ExternalEvent, user pause, Hold, levels, transactions with rollback,
    /// fixed target and idempotency journal.
    /// </summary>
    public sealed partial class RequestHandler
    {
        private readonly OperationJournal _ops = SafeJournal(out _opsError);
        private static string _opsError;

        /// <summary>Per-document coordination: long tasks and barriers take the document; other writers wait or are rejected.</summary>
        internal readonly DocLocks Locks = new DocLocks();

        private static OperationJournal SafeJournal(out string error)
        {
            error = null;
            try { return new OperationJournal(); }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private static string TargetId => DocTarget.Ambient.Value?.Id;
        private static string TargetTitle => DocTarget.Ambient.Value?.Title;

        /// <summary>Runs a step on the Revit thread with the request's target pinned (F04).</summary>
        private Task<WorkResult> RunMain(Func<UIApplication, Stopwatch, int, bool> step, bool highPriority, TimeSpan busyAfter, CancellationToken ct)
        {
            var target = DocTarget.Ambient.Value;
            return _main.RunAsync((app, sw, budget) =>
            {
                using (DocTarget.Enter(target)) return step(app, sw, budget);
            }, highPriority, busyAfter, ct);
        }

        /// <summary>Request target: doc= (handle, path, key or title) or the currently active one. Resolved off the Revit thread using the registry copy.</summary>
        private Response ResolveTarget(Session session, Request request)
        {
            var requested = Arg.GetString(request.Args, Wire.DocArg);
            var planId = request.Op == Wire.OpCommit ? Arg.GetString(request.Args, "plan_id") : null;
            var plan = string.IsNullOrEmpty(planId) ? null : _plans.Get(planId);
            var task = Arg.GetString(request.Args, Wire.TaskArg);
            if (plan?.DocId != null && (plan.SessionId == session.Id || (!string.IsNullOrEmpty(task) && plan.Task == task)))
            {
                var planned = DocRegistry.Instance.Find(plan.DocId, out _, out _);
                if (planned == null || planned.Key != plan.DocKey)
                    return Response.Fail(request.Id, Status.TargetUnavailable, "The plan's document is no longer available. Query the plan before repeating.");
                if (!string.IsNullOrEmpty(requested))
                {
                    var explicitDoc = DocRegistry.Instance.Find(requested, out _, out _);
                    if (explicitDoc?.Id != planned.Id)
                        return Response.Fail(request.Id, Status.Conflict, "The given document does not match the plan's document.");
                }
                DocTarget.Ambient.Value = DocTarget.From(planned, true);
                return null;
            }
            if (DocRegistry.Instance.Count == 0 && string.IsNullOrEmpty(requested)) { DocTarget.Ambient.Value = null; return null; }
            var info = DocRegistry.Instance.Find(requested, out var error, out var ambiguous);
            if (info == null)
            {
                if (string.IsNullOrEmpty(requested)) { DocTarget.Ambient.Value = null; return null; } // no known documents yet: previous behavior
                return Response.Fail(request.Id, ambiguous ? Status.Ambiguous : Status.TargetUnavailable, error);
            }
            DocTarget.Ambient.Value = DocTarget.From(info, !string.IsNullOrEmpty(requested));
            return null;
        }

        /// <summary>
        /// Code actually resolved for a request (codigo=, nombre= of a saved script or archivo=path.cs). The journal fingerprint
        /// includes it (H03): changing the file changes the fingerprint even if the name is the same.
        /// </summary>
        internal static string ResolveCode(IDictionary<string, object> a, out string source, out string error)
        {
            source = null;
            error = null;
            var code = Arg.GetString(a, "code") ?? Arg.GetString(a, "code");
            var name = Arg.GetString(a, "name");
            var file = Arg.GetString(a, "file");
            try
            {
                if (code != null) { source = "code"; return code; }
                if (name != null)
                {
                    var p = ScriptSource.PathOf(name);
                    if (!File.Exists(p)) { error = "there is no saved script '" + name + "' (exec action=list)"; return null; }
                    source = "nombre:" + name;   // recipe source identity stored in task records (task fingerprint): kept verbatim
                    return File.ReadAllText(p);
                }
                if (file != null)
                {
                    file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
                    if (!Path.IsPathRooted(file)) { error = "file= needs a full path (e.g. %USERPROFILE%\\Documents\\x.cs), not relative to the Revit folder"; return null; }
                    if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { error = "file= must be a .cs file"; return null; }
                    if (!File.Exists(file)) { error = "file= does not exist: " + file; return null; }
                    source = "archivo:" + Path.GetFullPath(file);
                    return File.ReadAllText(file);
                }
            }
            catch (ArgumentException ex) { error = ex.Message; return null; }
            catch (IOException ex) { error = "could not read the code: " + ex.Message; return null; }
            catch (UnauthorizedAccessException ex) { error = "could not read the code: " + ex.Message; return null; }
            return null;
        }

        private const string ResolvedCodeArg = "_resolved_code";

        /// <summary>
        /// Writes with clave=…: the intent is recorded (durably) first and the result when the job really finishes (H02):
        /// - same key already applied → the saved result is returned without repeating;
        /// - same key running → the SAME job_id (wait briefly and report), never a second job;
        /// - error, partial, unknown or no final → interrupted: reconciliar=no_aplicado (repeat) or reconciliar=aplicado (mark done);
        /// - same key with different content → always invalid, also when reconciling;
        /// - if the journal cannot be written → diario_no_disponible and NOT applied.
        /// </summary>
        private async Task<Response> WithJournal(Session session, Request request, Func<Task<Response>> run)
        {
            var kind = OpSemantics.Classify(request.Op, request.Args, out var invalid);
            if (kind == OpSemantics.Kind.Invalid) return Response.Fail(request.Id, Status.Invalid, invalid);
            var key = Arg.GetString(request.Args, Wire.KeyArg);
            // alpha3: a trial is always rolled back; recording it with clave= would leave it as "already applied" in the journal.
            if (!string.IsNullOrWhiteSpace(key) && ((request.Op == Wire.OpCommit && string.Equals((Arg.GetString(request.Args, "mode") ?? "").Trim(), "trial", StringComparison.OrdinalIgnoreCase))
                                                   || (request.Op == Wire.OpExec && OpSemantics.ExecMode(request.Args, out _) == OpSemantics.ModeTrial)))
                return Response.Fail(request.Id, Status.Invalid, "key= is for persistent changes; a trial is rolled back and is not recorded as applied. Remove key= (correlation goes in summary or task).");
            if (kind != OpSemantics.Kind.Write || request.Op == Wire.OpTask) return await GuardDocLock(session, request, kind, run).ConfigureAwait(false);

            // AUD-19: a plan that comes from a source (operations with origen=) ALWAYS enters the journal with the key and fingerprint
            // derived from its content, whether or not the agent passed clave=. Forgetting the key does not allow repeating the batch.
            string boundHash = null;
            if (request.Op == Wire.OpCommit)
            {
                var pid = Arg.GetString(request.Args, "plan_id");
                if (string.IsNullOrEmpty(pid) && request.Args.TryGetValue("ops", out var rawOps))
                {
                    try
                    {
                        if (SourceBinding.Resolve(rawOps as List<object>) != null)
                            return Response.Fail(request.Id, Status.Invalid, "commit ops= with operations from a source (origin=): use plan and then commit plan_id=…; that way the batch is protected against repeats. Nothing was applied.");
                    }
                    catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }
                }
                var boundPlan = string.IsNullOrEmpty(pid) ? null : _plans.Get(pid);
                var isTrial = string.Equals((Arg.GetString(request.Args, "mode") ?? "").Trim(), "trial", StringComparison.OrdinalIgnoreCase);
                if (boundPlan?.BoundKey != null && !isTrial)
                {
                    var bound = new SourceBinding.Bound { Token = boundPlan.BoundToken, Key = boundPlan.BoundKey, Hash = boundPlan.BoundHash };
                    if (!SourceBinding.JournalIdentity(bound, key, out key, out boundHash, out var bindError))
                        return Response.Fail(request.Id, Status.Invalid, bindError);
                    request.Args[Wire.KeyArg] = key;
                }
            }

            string resolved = null;
            if (request.Op == Wire.OpExec)
            {
                resolved = ResolveCode(request.Args, out _, out var codeError);
                if (resolved == null && codeError != null) return Response.Fail(request.Id, Status.Invalid, codeError);
                if (resolved != null) request.Args[ResolvedCodeArg] = resolved;
            }
            if (string.IsNullOrWhiteSpace(key)) return await GuardDocLock(session, request, kind, run).ConfigureAwait(false);
            if (_ops == null)
                return Response.Fail(request.Id, Status.JournalUnavailable,
                    "Nothing was applied: the operation journal is not available (" + _opsError + "), so key=" + key + " cannot guarantee it is not repeated. " +
                    "Fix the MCP data folder or, if the user accepts it, repeat without key= (no idempotency).");
            var target = DocTarget.Ambient.Value;
            // Fixed journal identity of writes without a target document (stored in 0.9.3 journals): kept verbatim, like the "archivo:" key prefix.
            var docKey = target?.Key ?? "(sin documento)";
            OperationJournal.ReconcileMode mode;
            try { mode = OperationJournal.ParseReconcile(request.Args.TryGetValue("reconcile", out var rv) ? rv : null); }
            catch (ArgumentException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }
            var argsForHash = new Dictionary<string, object>(request.Args);
            argsForHash.Remove(ResolvedCodeArg);
            // Plan bound to a source: the fingerprint is the batch content's, not the plan_id's (a new plan with the same batch is the same operation).
            var hash = boundHash ?? OperationJournal.HashArgs(request.Op, argsForHash, resolved);
            var legacyHash = boundHash != null ? null : OperationJournal.LegacyHashArgs(request.Op, argsForHash, resolved);
            var task = Arg.GetString(request.Args, Wire.TaskArg);
            OperationJournal.BeginResult b;
            var attempt = Guid.TryParseExact(request.AttemptId, "N", out _) ? request.AttemptId : null;
            try
            {
                b = _ops.Begin(key, docKey, request.Op, hash, task, session.Id, mode, target?.Print, target?.Generation,
                    attempt, Correlation.Current?.AgentId, Arg.GetString(request.Args, "plan_id"), legacyHash);
            }
            catch (JournalUnavailableException ex)
            {
                return Response.Fail(request.Id, Status.JournalUnavailable, "Nothing was applied: " + ex.Message + ". Without a journal, key= cannot prevent repeats.");
            }
            switch (b.Kind)
            {
                case OperationJournal.BeginKind.Completed:
                    return new Response
                    {
                        Id = request.Id, State = b.Previous.State ?? Status.Ok,
                        Data = new Dictionary<string, object>
                        {
                            ["already_applied"] = true, ["key"] = key, ["when"] = b.Previous.Finished, ["attempts"] = (long)b.Previous.Attempts.Count,
                            ["original_result"] = b.Previous.Result,
                        },
                        Warning = "This operation (key " + key + ") was already applied in this document; it was not repeated.",
                    };
                case OperationJournal.BeginKind.Running:
                {
                    var jid = _ops.RunningJob(key, docKey);
                    var job = string.IsNullOrEmpty(jid) ? null : _jobs.Get(jid);
                    if (job != null) return await AwaitJob(request, job, CancellationToken.None).ConfigureAwait(false);
                    return Response.Fail(request.Id, Status.InProgress, "the operation with key " + key + " is running now in this Revit; wait and query its status instead of repeating it");
                }
                case OperationJournal.BeginKind.Mismatch:
                    return Response.Fail(request.Id, Status.Invalid,
                        "key " + key + " was already used for a different operation in this document (fingerprint " + b.Previous.Hash + " ≠ " + hash + "); " +
                        "a key identifies a single operation, also when reconciling. Use another key.");
                case OperationJournal.BeginKind.DocumentReplaced:
                    return Response.Fail(request.Id, Status.Conflict,
                        "there is now ANOTHER model at " + docKey + " (fingerprint " + target?.Print + "; operation " + key + " was recorded with " + b.Previous.DocPrint + "). " +
                        "The file was replaced: nothing is applied. Check which file is the correct one.");
                case OperationJournal.BeginKind.PossibleCopy:
                    return new Response
                    {
                        Id = request.Id, State = Status.Interrupted,
                        Data = new Dictionary<string, object> { ["key"] = key, ["recorded_in"] = b.OtherDoc, ["status_there"] = b.Previous.State ?? "no final" },
                        Warning = "This model looks like a copy of " + b.OtherDoc + ", where operation " + key + " is already recorded. Read the model: if it already contains the change, " +
                                "repeat with reconcile=applied; otherwise with reconcile=not_applied.",
                    };
                case OperationJournal.BeginKind.Interrupted:
                    return new Response
                    {
                        Id = request.Id, State = Status.Interrupted,
                        Data = new Dictionary<string, object>
                        {
                            ["key"] = key, ["started"] = b.Previous.Started, ["op"] = b.Previous.Op, ["previous_status"] = b.Previous.State ?? "no final",
                            ["attempts"] = (long)b.Previous.Attempts.Count, ["previous_result"] = b.Previous.Result,
                        },
                        Warning = "This operation started (" + b.Previous.Started + ") and its outcome was " + (b.Previous.State ?? "unknown (Revit may have closed or the connection dropped)") +
                                ": it may have been partly applied. Read the model and repeat the SAME command with reconcile=not_applied (nothing remained: it is applied again) " +
                                "or reconcile=applied (already there: it is marked done without repeating).",
                    };
            }

            Response resp;
            try { resp = await GuardDocLock(session, request, kind, run).ConfigureAwait(false); }
            catch (Exception ex)
            {
                resp = Response.Fail(request.Id, Status.Unknown, "the operation ended with an internal exception (" + ex.GetType().Name + ": " + ex.Message +
                                                                     "); it is unknown what was applied: reconcile before repeating");
            }
            // A job still running in Revit (en_proceso) is NOT finished: its job_id is recorded and the job itself writes the outcome.
            if (resp.State == Status.InProgress && resp.Data is Dictionary<string, object> pd && Arg.GetString(pd, "job_id") is string jobId && _jobs.Get(jobId) is Job running)
            {
                try { _ops.LinkJob(key, docKey, jobId); } catch (JournalUnavailableException) { }
                _jobs.WhenFinal(running, j => _ops.Complete(key, docKey, j.State, JobJournalResult(j)));
                AddWriteReceipt(session, attempt, resp);
                return resp;
            }
            try { _ops.Complete(key, docKey, resp.State, resp.Data as Dictionary<string, object> ?? (resp.Warning != null ? new Dictionary<string, object> { ["warning"] = resp.Warning } : null)); }
            catch (JournalUnavailableException ex)
            {
                resp.Warning = (resp.Warning == null ? "" : resp.Warning + " ") + "WARNING: the journal did not record the outcome (" + ex.Message + "); if you repeat key=" + key + " you will be asked to reconcile.";
            }
            AddWriteReceipt(session, attempt, resp);
            return resp;
        }

        private Response ReadWriteReceipt(Session session, long requestId, string attempt)
        {
            var target = DocTarget.Ambient.Value;
            Dictionary<string, object> receipt;
            try
            {
                var agent = Correlation.Current?.AgentId;
                receipt = _ops?.FindReceipt(attempt, session.Id, agent, target?.Key, target?.Print);
                if (receipt == null || Arg.GetString(receipt, "status") == Status.Held)
                    receipt = AuditLog.FindCommitReceipt(Ui.Runtime.Audit?.Path_, attempt, session.Id, agent, target?.Key, target?.Print) ?? receipt;
                if (receipt == null && target?.Print != null)
                {
                    _plans.LoadPersisted(target.Key);
                    var held = _plans.Held(target.Key).FirstOrDefault(p => p.ReceiptAttempt == attempt && p.ReceiptDocPrint == target.Print &&
                        (p.ReceiptSession == session.Id || (!string.IsNullOrEmpty(agent) && p.ReceiptAgent == agent)));
                    if (held != null) receipt = HeldReceipt(held);
                }
            }
            catch (Exception ex) when (ex is JournalUnavailableException || ex is PlanStoreException || ex is IOException || ex is UnauthorizedAccessException)
            {
                receipt = null;
            }
            if (receipt == null)
                return Response.Fail(requestId, Status.Unknown,
                    "There is no queryable evidence for this attempt in the given document and session. Pin the document and verify the model before repeating.");
            return new Response
            {
                Id = requestId, State = Arg.GetString(receipt, "status"),
                Data = new Dictionary<string, object> { ["receipt"] = receipt },
            };
        }

        private void AddWriteReceipt(Session session, string attempt, Response response)
        {
            if (attempt == null) return;
            if (ReadWriteReceipt(session, response.Id, attempt).Data is Dictionary<string, object> receipt)
            {
                if (response.Data == null) response.Data = receipt;
                else if (response.Data is Dictionary<string, object> data) data["receipt"] = receipt["receipt"];
            }
        }

        private static Dictionary<string, object> JobJournalResult(Job j)
        {
            var d = j.Result != null ? new Dictionary<string, object>(j.Result) : new Dictionary<string, object>();
            d["job_id"] = j.Id;
            if (j.Error != null) d["error"] = j.Error;
            return d;
        }

        /// <summary>
        /// Writes to a document held by another owner's long task or barrier: they are not interleaved (documento_ocupado).
        /// Reads always pass.
        /// </summary>
        private async Task<Response> GuardDocLock(Session session, Request request, OpSemantics.Kind kind, Func<Task<Response>> run)
        {
            if (kind == OpSemantics.Kind.Write && request.Op == Wire.OpTask)
            {
                // Start or resume writes; cancel, note and reconcile on a task must remain available while the document is locked.
                var verb = OpSemantics.TaskVerb(request.Args);
                if (verb == "start" || verb == "resume")
                {
                    var taskBlock = WriteBarrier(request.Id, DeclaredDocs(request));
                    if (taskBlock != null) return taskBlock;
                }
            }
            if (kind != OpSemantics.Kind.Write || request.Op == Wire.OpTask) return await run().ConfigureAwait(false);
            var docs = DeclaredDocs(request);
            // commit checks its plan's document on the Revit thread; everything else stops here.
            if (request.Op != Wire.OpCommit)
            {
                var barrier = WriteBarrier(request.Id, docs);
                if (barrier != null) return barrier;
            }
            foreach (var d in docs)
            {
                var h = Locks.HeldByOther(d, session.Id);
                if (h != null && !IsOwnTask(request, h))
                    return Response.Fail(request.Id, Status.DocumentBusy,
                        "Not applied: the document is held by " + h.Describe() + ". Wait for it to finish (task status) or ask the user to cancel it.");
            }
            // Save, sync, export and close are barriers: while they run nobody else writes to that document.
            if (request.Op == Wire.OpDoc || (request.Op == Wire.OpFam && (OpSemantics.FamVerb(request.Args) == "load" || OpSemantics.FamVerb(request.Args) == "load_file")))
            {
                var owner = "barrera:" + session.Id + ":" + request.Id;   // lock owner identity shared with 0.9.3 processes: kept verbatim
                var blocking = await Locks.AcquireAsync(docs, owner, "barrier", request.Op + " " + Arg.GetString(request.Args, "action"), TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                if (blocking != null) return Response.Fail(request.Id, Status.DocumentBusy, "Not applied: the document is held by " + blocking.Describe() + ".");
                var resp = await run().ConfigureAwait(false);
                if (resp.State == Status.InProgress && resp.Data is Dictionary<string, object> pd && Arg.GetString(pd, "job_id") is string jid && _jobs.Get(jid) is Job j)
                    _jobs.WhenFinal(j, _ => Locks.Release(docs, owner));
                else Locks.Release(docs, owner);
                return resp;
            }
            return await run().ConfigureAwait(false);
        }

        /// <summary>The task itself (tarea=id) may write to a document it holds.</summary>
        private static bool IsOwnTask(Request request, DocLocks.Holder h)
        {
            var task = Arg.GetString(request.Args, Wire.TaskArg);
            return task != null && h.Kind == "task" && h.Detail == task;
        }

        /// <summary>Documents a write touches: the target and, for multi-document scripts, those in docs=d1,d2 (durable keys).</summary>
        private static List<string> DeclaredDocs(Request request)
        {
            var list = new List<string>();
            var t = DocTarget.Ambient.Value;
            if (t?.Key != null) list.Add(t.Key);
            foreach (var h in Arg.GetStringList(request.Args, "docs"))
            {
                var info = DocRegistry.Instance.Find(h, out _);
                if (info?.Key != null && !list.Contains(info.Key)) list.Add(info.Key);
            }
            return list;
        }

        // ---------------- docs ----------------

        private async Task<Response> Docs(Request request, CancellationToken ct)
        {
            List<DocRegistry.Info> list = null;
            var rows = new List<object>();
            var r = await RunMain((app, sw, budget) =>
            {
                list = DocRegistry.Instance.Refresh(app);
                foreach (var i in list)
                {
                    var d = DocRegistry.Instance.Resolve(app, i.Id);
                    if (d == null) continue;
                    string fam = null;
                    try { if (d.IsFamilyDocument) fam = d.OwnerFamily?.FamilyCategory?.Name; } catch (Exception) { }
                    rows.Add(new object[]
                    {
                        i.Id, d.Title, i.Kind, i.Active, d.IsModified, d.IsReadOnly, d.IsWorkshared, string.IsNullOrEmpty(d.PathName) ? "(unsaved)" : d.PathName,
                        fam ?? "", _tracker.For(d)?.Rev ?? 0L,
                    });
                }
                return true;
            }, highPriority: true, busyAfter: Busy, ct: ct).ConfigureAwait(false);
            if (!r.Ok) return FromWork(request.Id, r);
            return new Response
            {
                Id = request.Id,
                Data = new Dictionary<string, object>
                {
                    ["instance"] = DocRegistry.Instance.InstanceId,
                    ["cols"] = new List<object> { "doc", "title", "type", "active", "modified", "read_only", "worksharing", "path", "family_category", "rev" },
                    ["rows"] = rows,
                    ["usage"] = "doc=dN on any command pins that document even if the user switches tabs",
                },
            };
        }

        // ---------------- exec ----------------

        /// <summary>
        /// General runner (0.6.0, H05). Guarantees:
        /// - the result is converted to JSON BEFORE committing the transaction: if that fails, it is rolled back and reported (never "rolled back" after a commit);
        /// - Start/Commit/Assimilate/RollBack are checked (also Pending); if something is left half done the status is partial or unknown;
        /// - with tx=ninguna, real changes are measured with Revit events: no changes = nothing applied; changes plus error = partial;
        /// - consulta rolls back ITS transaction in the target document; whatever the script commits in other documents or files is not rolled back
        ///   and is reported (change tracking is no longer suspended, which hid it).
        /// </summary>
        private async Task<Response> Exec(Session session, Request request, CancellationToken ct)
        {
            var a = request.Args;
            var mode = OpSemantics.ExecMode(a, out var modeError);
            if (mode == null) return Response.Fail(request.Id, Status.Invalid, modeError);
            var tx = (Arg.GetString(a, "tx") ?? "auto").Trim().ToLowerInvariant();
            if (tx != "auto" && tx != "group" && tx != "none") return Response.Fail(request.Id, Status.Invalid, "tx must be auto, group or none");
            if (mode != OpSemantics.ModeRun && tx != "auto") return Response.Fail(request.Id, Status.Invalid, "tx=group/none only with mode=run");
            var run = mode == OpSemantics.ModeRun;
            var trial = mode == OpSemantics.ModeTrial;
            // alpha3: a trial modifies the model while it runs (even though it is rolled back): it requires the same W level as writing.
            if (trial && !_config.AllowsExec(true) && !(_config.Runner == "query" && _config.Allows('W')))
                return Response.Fail(request.Id, Status.Rejected, "trial changes the model temporarily and requires level W (levels " + _config.Levels +
                    "). The user decides this in the MCP configuration. To only read use mode=query; to test a typed write, commit mode=trial.");
            if (!trial && !_config.AllowsExec(run))
            {
                var why = _config.Runner == "off" ? "the C# runner is turned off in this Revit's configuration (runner=off)"
                    : run && _config.Runner == "query" ? "in this Revit the runner only accepts queries (runner=query)"
                    : "this Revit does not have changes enabled through this connector (levels " + _config.Levels + ")";
                return Response.Fail(request.Id, Status.Rejected, why + ". The user decides this in the MCP configuration; tell them. If the user authorized another tool for this, you may use it.");
            }
            if (run && WillHold(DocTarget.Ambient.Value?.Key))
                return Response.Fail(request.Id, Status.NeedsUser,
                    "Hold is active (or there are plans in the queue): the user reviews every change from the queue, and a script has no diff to queue. " +
                    "Ask them to switch Mode to Live for this task, or use plan/commit (which is queued).");

            // Code: inline, saved by name or a .cs file (already resolved by the journal if clave= was given)
            string codeError = null;
            var code = Arg.GetString(a, ResolvedCodeArg) ?? ResolveCode(a, out _, out codeError);
            if (code == null) return Response.Fail(request.Id, Status.Invalid, codeError ?? "missing the code (code=…), name= of a saved script or file=path.cs");
            if (string.IsNullOrWhiteSpace(code)) return Response.Fail(request.Id, Status.Invalid, "the code is empty");
            // Consolidated review: a single control. Without «Block C# code injection», exec runs for the authorized local agent
            // (user pipe + config.json level), with no per-script authorization. With the block, every exec is rejected before compiling.
            var blocked = DynamicCodePolicy.Decide(Ui.Runtime.BlockDynamicCode);
            if (blocked != null)
            {
                var bf = Response.Fail(request.Id, Status.Rejected, blocked);
                bf.Data = new Dictionary<string, object> { ["code"] = DynamicCodePolicy.BlockedCode };
                return bf;
            }
            var name = Arg.GetString(a, "name");
            try
            {
                var saveAs = Arg.GetString(a, "save");
                if (saveAs != null)
                {
                    Directory.CreateDirectory(ScriptSource.ScriptsFolder);
                    File.WriteAllText(ScriptSource.PathOf(saveAs), code);
                }
            }
            catch (ArgumentException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }

            // R04: extra DLLs are decided BEFORE loading them into Revit.
            var trusted = Ui.Runtime.TrustedExecActive;
            var refs = Arg.GetStringList(a, "refs");
            var refDenied = ReferencePolicy.Denied(trusted ? ExecutionProfile.TrustedInteractive : ExecutionProfile.RestrictedAutonomous, refs,
                f => { try { return Home.FileSha256(f); } catch (Exception) { return null; } }, ReferencePolicy.LoadTrusted(), ScriptHost.AddinDir);
            if (refDenied.Count > 0)
                return Response.Fail(request.Id, Status.Rejected, "referencia_no_confiable: " + string.Join("; ", refDenied.Take(5)) +
                    ". Without «Trusted code» active, only DLLs from the add-in folder or listed by SHA-256 in " + ReferencePolicy.TrustedListPath);
            // Compile off the Revit thread (with explicit refs= references if any)
            var compiled = await Task.Run(() =>
            {
                var c = ScriptHost.Compile(code, _revitVersion, _addinVersion, refs, out var err);
                return Tuple.Create(c, err);
            }, ct).ConfigureAwait(false);
            if (compiled.Item1 == null)
                return Response.Fail(request.Id, ScriptHost.LoadError != null && compiled.Item2 == ScriptHost.LoadError ? Status.NotSupported : Status.Invalid, compiled.Item2);
            var script = compiled.Item1;
            if (script.Run == null)
                return Response.Fail(request.Id, Status.Invalid, "that code is a recipe (a class that inherits from Recipe): start it with task start, not with exec");
            var gate = CapabilityPolicy.ExecDecision(run, trusted, script.Capabilities, out var profile);
            if (gate != null) return Response.Fail(request.Id, Status.Rejected, gate);
            var auditErr = AuditScript(session, code, script.Key, mode, tx, profile, script, refs, out var execIntent);
            if (auditErr != null && mode != OpSemantics.ModeQuery)
                return Response.Fail(request.Id, Status.Error, "audit_unavailable (" + auditErr + "): for safety, code that writes is not run without a durable record");

            var job = _jobs.Create(session.Id, "exec " + mode);
            job.Task = Arg.GetString(a, Wire.TaskArg);
            var userArgs = a.TryGetValue("args", out var ua) && ua is Dictionary<string, object> ud ? ud : new Dictionary<string, object>();
            var target = DocTarget.Ambient.Value;
            _jobs.Run(job, async () =>
            {
                var sw = Stopwatch.StartNew();
                Dictionary<string, object> data = null;
                string fail = null, failStatus = Status.Error;
                var failures = new FailureLog { DeleteWarnings = !(Arg.GetString(a, "warnings") == "leave") };
                var revBefore = new Dictionary<string, long>();
                var txStates = new Dictionary<string, object>();
                List<string> serErrors = null;
                ScriptContext ctxObj = null;
                job.Phase = "waiting for Revit";
                var r = await RunMain((app, watch, budget) =>
                {
                    job.Phase = "running in Revit";
                    var doc = DocTarget.Resolve(app, out var docErr);
                    if (doc == null && target != null) { fail = docErr; failStatus = Status.TargetUnavailable; return true; }
                    foreach (Document d in app.Application.Documents) if (!d.IsLinked) revBefore[DocRegistry.Instance.IdOf(d)] = _tracker.For(d)?.Rev ?? 0;
                    bool AnyChange()
                    {
                        foreach (Document d in app.Application.Documents)
                        {
                            if (d.IsLinked) continue;
                            var id = DocRegistry.Instance.IdOf(d);
                            if ((_tracker.For(d)?.Rev ?? 0) != (revBefore.TryGetValue(id, out var rb) ? rb : 0)) return true;
                        }
                        return false;
                    }
                    ctxObj = new ScriptContext(app, doc, userArgs, mode, tx, job.Cts.Token, session.Sets, doc != null ? DocRegistry.Instance.IdOf(doc) : null);
                    var txName = DocumentTracker.TransactionPrefix + (run ? " exec " + (name ?? script.Key.Substring(0, 8)) : " temporary exec");
                    TransactionGroup group = null;
                    Transaction t = null;
                    var committed = false;
                    _tracker.CurrentWriter = session.Id;
                    try
                    {
                        // alpha3: consulta runs WITHOUT a transaction: Revit rejects any modification (previously one was opened and rolled back,
                        // which allowed temporary changes with level R). ensayo = group + transaction, always rolled back.
                        // P0 (Codex): exec ensayo always rolls back its group → refused up front if the document has unsaved changes.
                        if (trial && doc != null)
                        {
                            var refuse = Engine.TrialGuard.Refuse(doc.IsModified, "exec mode=trial");
                            if (refuse != null) { fail = refuse; failStatus = Status.Rejected; return true; }
                        }
                        if (doc != null && (trial || (run && tx == "group")))
                        {
                            group = new TransactionGroup(doc, txName);
                            var gs = group.Start();
                            if (gs != TransactionStatus.Started)
                            {
                                fail = "Revit did not allow opening the transaction group (" + gs + "); the script was not run. Is a command or an edit mode open?";
                                failStatus = Status.RolledBack;
                                return true;
                            }
                        }
                        if (doc != null && (trial || (run && tx == "auto")))
                        {
                            t = new Transaction(doc, txName);
                            FailureLog.Attach(t, failures);
                            var ts = t.Start();
                            if (ts != TransactionStatus.Started)
                            {
                                fail = "Revit did not allow opening the transaction (" + ts + "); the script was not run.";
                                failStatus = Status.RolledBack;
                                if (group != null && group.HasStarted()) group.RollBack();
                                return true;
                            }
                        }

                        object value;
                        try { value = script.Run.Invoke(null, new object[] { ctxObj }); }
                        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }

                        // 1) Materialize the result BEFORE the point of no return.
                        object json;
                        bool trunc;
                        try { json = ResultSerializer.ToJson(value, doc, out trunc, out serErrors); }
                        catch (Exception ex) when (!(ex is OutOfMemoryException))
                        {
                            throw new ResultException("the script finished, but its result could not be converted: " + ex.GetType().Name + ": " + ex.Message, ex);
                        }
                        data = new Dictionary<string, object> { ["result"] = json };
                        if (trunc) data["truncated_result"] = "cut at " + ResultSerializer.MaxItems + " items or depth " + ResultSerializer.MaxDepth + "; return a summary or paginate";
                        if (serErrors != null && serErrors.Count > 0) data["result_errors"] = serErrors.Cast<object>().ToList();

                        // 2) Commit (or roll back in consulta) checking each status.
                        if (t != null)
                        {
                            if (!run)
                            {
                                var rs = t.RollBack();
                                txStates["transaction"] = rs.ToString();
                                if (rs != TransactionStatus.RolledBack) { fail = "the trial could not be rolled back (" + rs + ")"; failStatus = Status.Unknown; }
                            }
                            else
                            {
                                var st = t.Commit();
                                if (st == TransactionStatus.Committed) committed = true;
                                else if (st == TransactionStatus.RolledBack)
                                {
                                    fail = "Revit rejected the transaction and rolled it back: " + string.Join(" | ", failures.Errors.Take(5));
                                    failStatus = Status.RolledBack;
                                }
                                else
                                {
                                    fail = "the transaction was left in status " + st + " (Revit neither committed nor rolled back; there may be a warning pending on screen)";
                                    failStatus = Status.Unknown;
                                }
                            }
                        }
                        if (group != null)
                        {
                            if (!run || fail != null)
                            {
                                var gr = group.RollBack();
                                txStates["group"] = gr.ToString();
                                if (gr != TransactionStatus.RolledBack && fail == null) { fail = "the group could not be rolled back (" + gr + ")"; failStatus = Status.Unknown; }
                            }
                            else
                            {
                                var ga = group.Assimilate();
                                if (ga == TransactionStatus.Committed) committed = true;
                                else if (ga == TransactionStatus.RolledBack) { fail = "Revit rolled back the group while assimilating it (" + ga + ")"; failStatus = Status.RolledBack; }
                                else { fail = "the group was left in status " + ga; failStatus = AnyChange() ? Status.Unknown : Status.RolledBack; }
                            }
                        }
                        if (run && tx == "none") committed = AnyChange();
                    }
                    catch (Exception ex)
                    {
                        // Roll back our own work and check that it really was rolled back.
                        var rolled = true;
                        try { if (t != null && t.GetStatus() == TransactionStatus.Started) rolled &= t.RollBack() == TransactionStatus.RolledBack; } catch (Exception) { rolled = false; }
                        try { if (group != null && group.HasStarted()) rolled &= group.RollBack() == TransactionStatus.RolledBack; } catch (Exception) { rolled = false; }
                        var changed = AnyChange();
                        var cancelled = ex is OperationCanceledException;
                        var what = ex is ResultException ? ex.Message : (cancelled ? "cancelled: " : "") + Describe(ex);
                        if (!changed && rolled)
                        {
                            fail = what + ". Nothing was left applied (it was rolled back and Revit recorded no changes): it can be fixed and repeated.";
                            failStatus = Status.RolledBack;
                        }
                        else if (tx == "none" || !rolled)
                        {
                            fail = what + ". WARNING: there are committed changes in the model (" + (tx == "none" ? "the script's own transactions" : "the rollback was not confirmed") +
                                   "): review the model and reconcile before repeating.";
                            failStatus = Status.Partial;
                        }
                        else
                        {
                            fail = what + ". The script's work was rolled back, but Revit recorded changes in another document: review them.";
                            failStatus = Status.Partial;
                        }
                    }
                    finally
                    {
                        _tracker.CurrentWriter = null;
                        try { t?.Dispose(); } catch (Exception) { }
                        try { group?.Dispose(); } catch (Exception) { }
                    }
                    return true;
                }, highPriority: mode == OpSemantics.ModeQuery, busyAfter: Busy, ct: job.Cts.Token).ConfigureAwait(false);

                if (!r.Ok) { AuditExecOutcome(session, execIntent, "not_run"); FailJob(job, r, write: run); return; }
                var auditGap = AuditExecOutcome(session, execIntent, fail == null ? (run ? "committed" : trial ? "trial_reverted" : "query") : failStatus);
                var extra = new Dictionary<string, object>
                {
                    ["mode"] = mode, ["tx"] = tx, ["shape"] = script.Kind,
                    ["compile_ms"] = script.FromCache ? 0L : script.CompileMs, ["cache"] = script.FromCache,
                    ["total_ms"] = sw.ElapsedMilliseconds,
                };
                if (txStates.Count > 0) extra["tx_status"] = txStates;   // LITERAL Revit status (RolledBack…)
                if (target != null) extra["doc"] = target.Id;
                if (script.ExtraRefs != null) extra["refs"] = script.ExtraRefs;
                if (ctxObj != null && ctxObj.LogLines.Count > 0) extra["log"] = ctxObj.LogLines.Cast<object>().ToList();
                if (failures.Items.Count > 0) extra["revit_warnings"] = failures.Items.Select(i => (object)i.ToJson()).ToList();
                if (script.Diagnostics != null && script.Diagnostics.Length > 0) extra["compiler"] = script.Diagnostics.Cast<object>().ToList();
                // What really changed (according to Revit events), per document — also in consulta (what was NOT rolled back).
                var changed = new List<object>();
                try
                {
                    await RunMain((app, w, b) =>
                    {
                        foreach (Document d in app.Application.Documents)
                        {
                            if (d.IsLinked) continue;
                            var id = DocRegistry.Instance.IdOf(d);
                            var reg = _tracker.For(d);
                            var before = revBefore.TryGetValue(id, out var rb) ? rb : 0;
                            if (reg == null || reg.Rev == before) continue;
                            var ids = reg.ChangedSince(before, out var deleted);
                            changed.Add(new Dictionary<string, object> { ["doc"] = id, ["title"] = d.Title, ["elements"] = (long)ids.Count, ["deleted_items"] = (long)(deleted?.Count ?? 0) });
                            if (ids.Count > 0 && id == target?.Id) { try { session.Sets.Save("$exec_cambiados", ids, id, d.Title); } catch (Exception) { } }
                        }
                        return true;
                    }, highPriority: true, busyAfter: Busy, ct: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { extra["error_changes"] = ex.Message; }
                if (run) extra["changes"] = changed.Count == 0 ? (object)"none" : changed;
                else if (changed.Count > 0)
                {
                    extra["unreverted_effects"] = changed;
                    extra["query_warning"] = "query rolls back only its transaction in the target document; the script committed changes in other documents and those remain applied";
                }
                if (auditGap != null)
                {
                    // rc3: real result but incomplete audit: never "ok" (nor rolled back if it was committed).
                    extra["audit"] = AuditGate.Incomplete;
                    extra["reconcile_intent"] = execIntent;
                    if (fail == null && run)
                    {
                        extra["applied"] = true;
                        if (data != null) foreach (var kv in extra) data[kv.Key] = kv.Value;
                        job.Result = data ?? extra;
                        _jobs.Fail(job, Status.AppliedResultError, auditGap);
                        return;
                    }
                }
                if (fail != null)
                {
                    job.Result = extra;
                    _jobs.Fail(job, failStatus, fail);
                    return;
                }
                if (data == null)
                {
                    // Committed but without a result (should not happen: the result is materialized first). Never reported as rolled back.
                    extra["applied"] = true;
                    job.Result = extra;
                    _jobs.Fail(job, Status.AppliedResultError, "the changes were committed, but there is no result to return");
                    return;
                }
                foreach (var kv in extra) data[kv.Key] = kv.Value;
                var status = !run && changed.Count > 0 ? Status.CompletedWithDifferences : Status.Ok;
                try { Json.Write(data); }
                catch (Exception ex)
                {
                    // Failure producing the response AFTER committing: applied, with a result error (never a false rollback).
                    _jobs.Fail(job, run ? Status.AppliedResultError : Status.Error, "the result could not be written: " + ex.Message + (run ? "; the changes WERE applied" : ""));
                    job.Result = extra;
                    return;
                }
                _jobs.Complete(job, data, status);
            });
            var resp = await AwaitJob(request, job, ct).ConfigureAwait(false);
            if (resp.State != Status.Ok && resp.State != Status.InProgress && job.Result != null && resp.Data == null) resp.Data = job.Result;
            return resp;
        }

        /// <summary>The script finished but its result could not be materialized (before committing: it is rolled back).</summary>
        private sealed class ResultException : Exception
        {
            public ResultException(string message, Exception inner) : base(message, inner) { }
        }

        private static string Describe(Exception ex)
        {
            var msg = ex.GetType().Name + ": " + ex.Message;
            // Script line (thanks to #line and the PDB): "script:line N"
            var st = new StackTrace(ex, true);
            foreach (var f in st.GetFrames() ?? new StackFrame[0])
            {
                var file = f.GetFileName();
                if (file != null && file.EndsWith("script", StringComparison.Ordinal) && f.GetFileLineNumber() > 0)
                    return msg + " (script line " + f.GetFileLineNumber() + ")";
            }
            if (ex is MissingMethodException || ex is MissingMemberException || ex is TypeLoadException)
                msg += " — that member does not exist in this Revit build (" + Ui.Runtime.RevitVersion + ")";
            return msg;
        }

        /// <summary>Phase 1 (R01): every script is recorded in the durable audit BEFORE it runs (intent, with the code's SHA-256, profile,
        /// detected capabilities and references with their SHA-256); its full code in scripts\historial.
        /// Returns null if it was recorded; the reason otherwise (the caller rejects the write: fail closed).</summary>
        private static string AuditScript(Session session, string code, string key, string mode, string tx, ExecutionProfile profile,
            ScriptHost.Compiled script, IList<string> refs, out string intent)
        {
            intent = null;
            string path = null;
            try
            {
                var dir = LegacyAliases.ResolveExisting(ScriptSource.ScriptsFolder, Path.Combine(ScriptSource.ScriptsFolder, "history"));
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, key.Substring(0, 16) + ".cs");
                if (!File.Exists(path)) File.WriteAllText(path, code);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { path = null; }
            var extra = new Dictionary<string, object>
            {
                ["mode"] = mode, ["tx"] = tx, ["profile"] = profile.ToString(), ["fingerprint"] = key.Substring(0, 16), ["sha256"] = Home.Sha256Hex(code), ["file"] = path,
                ["capabilities_used"] = (script.Capabilities ?? new string[0]).Cast<object>().ToList(),
                ["refs"] = (refs ?? new List<string>()).Select(r => (object)(r + " sha256=" + SafeSha(r))).ToList(),
                ["start"] = code.Length > 200 ? code.Substring(0, 200) + "…" : code,
            };
            Ui.Runtime.Journal?.Log(session.Id, "exec_code", mode, 0, extra);
            try
            {
                if (Ui.Runtime.Audit == null) return "audit not initialized";
                intent = Ui.Runtime.Audit.Intent(session.Id, "exec_code", DocTarget.Ambient.Value?.Key ?? "", extra);
                return null;
            }
            catch (JournalUnavailableException ex) { return ex.Message; }
        }

        private static string SafeSha(string p)
        {
            try { return Home.FileSha256(Environment.ExpandEnvironmentVariables(p)); } catch (Exception) { return "?"; }
        }

        /// <summary>Phase 2: outcome of an exec. If it fails, the intent stays pending reconciliation (AuditLog.PendingIntents).</summary>
        private static string AuditExecOutcome(Session session, string intent, string status)
        {
            if (intent == null) return null;
            try { Ui.Runtime.Audit?.Outcome(session.Id, "exec_code", intent, status); return null; }
            catch (JournalUnavailableException ex)
            {
                var msg = AuditGate.Incomplete + ": the script finished (" + status + "; if it committed, the changes WERE applied), but its outcome was not recorded in the audit (" +
                          ex.Message + "). Intent " + intent + " pending reconciliation.";
                Ui.Runtime.Log("AUDIT: " + msg);
                return msg;
            }
        }

        private Response ExecList(Request request)
        {
            var rows = new List<object>();
            try
            {
                if (Directory.Exists(ScriptSource.ScriptsFolder))
                    foreach (var f in Directory.GetFiles(ScriptSource.ScriptsFolder, "*.cs").OrderBy(x => x))
                        rows.Add(new object[] { Path.GetFileNameWithoutExtension(f), new FileInfo(f).Length, File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) });
            }
            catch (Exception ex) { return Response.Fail(request.Id, Status.Error, ex.Message); }
            return new Response
            {
                Id = request.Id,
                Data = new Dictionary<string, object>
                {
                    ["cols"] = new List<object> { "name", "bytes", "modified" }, ["rows"] = rows, ["folder"] = ScriptSource.ScriptsFolder,
                    ["compiler"] = ScriptHost.Present ? "present" : "roslyn folder missing",
                    ["cached"] = (long)ScriptHost.CachedCount,
                },
            };
        }

        // ---------------- fam ----------------

        private FamilyLibrary _library;
        private FamilyLibrary Library => _library ?? (_library = new FamilyLibrary(_config.FamilyFolders, Home.Resolve(System.IO.Path.Combine(Home.Root, "families", "index.json"))));

        /// <summary>fam buscar q=… [carpeta=…] [top=10]: candidates from the authorized library, compatibility and whether already loaded.</summary>
        private Dictionary<string, object> FamilySearch(UIApplication app, Document doc, IDictionary<string, object> a)
        {
            var q = Arg.GetString(a, "q") ?? throw new OpFail(Status.Invalid, "fam search: missing q (words of the name, e.g. q=\"downlight round\")");
            var loaded = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Select(f => f.Name).ToList();
            var ver = int.TryParse(app.Application.VersionNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 9999;
            var hits = Library.Search(q, ver, loaded, Arg.GetString(a, "folder"), (int)(Arg.GetLong(a, "top") ?? 10));
            return new Dictionary<string, object>
            {
                ["indexed"] = (long)Library.Count, ["target_revit"] = (long)ver,
                ["candidates"] = hits.Select(h =>
                {
                    var d = h.Entry.ToJson();
                    d["path"] = h.Entry.Path; d["compatible"] = h.Compatible; d["already_loaded"] = h.Loaded; d["points"] = Math.Round(h.Score, 2);
                    if (h.Why != null) d["reason"] = h.Why;
                    return (object)d;
                }).ToList(),
            };
        }

        /// <summary>fam cargar_archivo ruta=… [sobrescribir=no|valores]: loads into the project an RFA from the authorized library (unchanged since indexing).</summary>
        private Dictionary<string, object> FamilyLoadFile(Document doc, IDictionary<string, object> a)
        {
            FamilyOps.RequireProject(doc, "fam load_file");
            var e = Library.ResolveForLoad(Arg.GetString(a, "path"), out var err) ?? throw new OpFail(Status.Rejected, "fam load_file: " + err);
            var ver = int.TryParse(doc.Application.VersionNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 9999;
            if (e.RevitVersion.HasValue && e.RevitVersion.Value > ver) throw new OpFail(Status.Rejected, "the family is from Revit " + e.RevitVersion + " and this one is " + ver);
            var mode = (Arg.GetString(a, "overwrite") ?? "no").Trim().ToLowerInvariant();
            if (mode != "no" && mode != "values") throw new OpFail(Status.Invalid, "overwrite=no or values");
            var existed = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => string.Equals(f.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            Family fam = null;
            using (var t = new Transaction(doc, DocumentTracker.TransactionPrefix + " fam load_file " + e.Name))
            {
                t.Start();
                var ok = doc.LoadFamily(e.Path, new FamilyOps.LoadOptions(mode == "values"), out fam);
                if (!ok && existed == null) { t.RollBack(); throw new OpFail(Status.Error, "Revit did not load the family"); }
                if (t.Commit() != TransactionStatus.Committed) throw new OpFail(Status.Error, "the load transaction was not committed");
            }
            fam = fam ?? existed;
            var types = fam.GetFamilySymbolIds().Select(id => doc.GetElement(id)?.Name).Where(n => n != null).OrderBy(n => n).ToList();
            return new Dictionary<string, object>
            {
                ["family"] = fam.Name, ["id"] = Ids.ToLong(fam.Id), ["already_existed"] = existed != null, ["types"] = types.Cast<object>().ToList(),
                ["sha256"] = e.Sha256, ["path"] = e.Path, ["revit_file"] = e.RevitVersion.HasValue ? (object)(long)e.RevitVersion.Value : "unknown",
            };
        }

        private static string FamVerb(IDictionary<string, object> a) => (Arg.GetString(a, "action") ?? "info").Trim().ToLowerInvariant();

        private async Task<Response> Fam(Session session, Request request, CancellationToken ct)
        {
            var a = request.Args;
            var verb = FamVerb(a);
            var write = OpSemantics.FamWrites.Contains(verb);
            if (!write && verb != "info" && verb != "faces" && verb != "search")
                return Response.Fail(request.Id, Status.Invalid, "fam: info, faces, search, open, connector, param, load or load_file");
            // 0.8.0: the library index is refreshed OFF the Revit thread (disk only), before asking for a turn.
            if (verb == "search" || verb == "load_file")
            {
                if (Library.Roots.Count == 0)
                    return Response.Fail(request.Id, Status.NeedsUser, "there are no authorized family folders: the user must add them in the configuration (family_folders)");
                if (Library.Count == 0 || Arg.GetBool(a, "refresh") == true) await Task.Run(() => Library.Refresh(), ct).ConfigureAwait(false);
            }
            if (write)
            {
                if (!_config.Allows('W'))
                    return Response.Fail(request.Id, Status.Rejected, "this Revit does not have changes enabled through this connector (levels " + _config.Levels + "). Tell the user; they change it in the MCP configuration.");
                if (WillHold(DocTarget.Ambient.Value?.Key))
                    return Response.Fail(request.Id, Status.NeedsUser, "Hold is active: family changes are not queued. Ask the user to switch Mode to Live for this task.");
            }
            Dictionary<string, object> data = null;
            string fail = null, failStatus = Status.Error;
            var changes = new List<string>();
            var diffs = new List<string>();
            var pending = new List<string>();
            var failures = new FailureLog();
            var sw = Stopwatch.StartNew();
            var r = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out var docErr);
                if (doc == null) { fail = docErr; failStatus = Status.TargetUnavailable; return true; }
                try
                {
                    switch (verb)
                    {
                        case "info": data = FamilyOps.Info(app, doc, a); break;
                        case "faces": data = FamilyOps.FacesInfo(doc, a); break;
                        case "search": data = FamilySearch(app, doc, a); break;
                        case "load_file":
                            data = FamilyLoadFile(doc, a);
                            changes.Add("family " + data["family"] + " loaded from the library");
                            break;
                        case "open": data = FamilyOps.Open(app, doc, a); break;
                        case "load":
                            data = FamilyOps.Load(app, doc, a, diffs);
                            changes.Add("family loaded into " + data["loaded_in"]);
                            break;
                        case "connector":
                        case "param":
                            var q = verb == "connector" ? FamilyOps.ConnectorRequest.Parse(a) : null;
                            FamilyOps.RequireFamily(doc, "fam " + verb);
                            _tracker.CurrentWriter = session.Id;
                            using (var t = new Transaction(doc, DocumentTracker.TransactionPrefix + " fam " + verb + (q != null ? " " + q.Key : "")))
                            {
                                FailureLog.Attach(t, failures);
                                t.Start();
                                try
                                {
                                    data = verb == "connector" ? FamilyOps.UpsertConnector(doc, q, changes, diffs, pending) : FamilyOps.Param(doc, a, changes, diffs);
                                }
                                catch (Exception)
                                {
                                    t.RollBack();
                                    throw;
                                }
                                if (changes.Count == 0) t.RollBack();
                                else if (t.Commit() != TransactionStatus.Committed)
                                {
                                    fail = "Revit rejected the change and it was rolled back: " + string.Join(" | ", failures.Errors.Take(5));
                                    failStatus = Status.RolledBack;
                                }
                            }
                            break;
                    }
                }
                catch (OpFail ex) { fail = ex.Message; failStatus = ex.State; }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { fail = ex.GetType().Name + ": " + ex.Message; failStatus = Status.Error; }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { fail = Describe(ex); failStatus = Status.Error; }
                finally { _tracker.CurrentWriter = null; }
                return true;
            }, highPriority: !write, busyAfter: Busy, ct: ct).ConfigureAwait(false);
            if (!r.Ok) return FromWork(request.Id, r);
            if (fail != null)
            {
                var d = new Dictionary<string, object>();
                if (failures.Items.Count > 0) d["revit_warnings"] = failures.Items.Select(i => (object)i.ToJson()).ToList();
                return new Response { Id = request.Id, State = failStatus, Warning = fail, Data = d.Count > 0 ? d : null };
            }
            if (write)
            {
                data["changes"] = changes.Count == 0 ? (object)"none (it was already as requested)" : changes.Cast<object>().ToList();
                if (pending.Count > 0) data["pending"] = pending.Cast<object>().ToList();
                if (failures.Items.Count > 0) data["revit_warnings"] = failures.Items.Select(i => (object)i.ToJson()).ToList();
            }
            if (diffs.Count > 0) data["differences"] = diffs.Cast<object>().ToList();
            data["ms"] = sw.ElapsedMilliseconds;
            return new Response
            {
                Id = request.Id,
                State = diffs.Count > 0 ? Status.CompletedWithDifferences : Status.Ok,
                Data = data,
                Warning = diffs.Count > 0 ? "Applied, but verification found differences from the request: review them." : null,
            };
        }

        // ---------------- doc ----------------

        private async Task<Response> DocOp(Session session, Request request, CancellationToken ct)
        {
            var a = request.Args;
            var verb = (Arg.GetString(a, "action") ?? "").Trim().ToLowerInvariant();
            if (verb == "save_as") { verb = "save"; if (Arg.GetString(a, "as") == null) return Response.Fail(request.Id, Status.Invalid, "save_as needs as=path"); }
            if (verb != "save" && verb != "sync" && verb != "sync" && verb != "pdf" && verb != "ifc" && verb != "schedule_csv" && verb != "close" && verb != "verify_disk")
                return Response.Fail(request.Id, Status.Invalid, "doc: save, save_as, sync, pdf, ifc, schedule_csv, close or verify_disk");
            if (!_config.AllowsDocOps)
                return Response.Fail(request.Id, Status.Rejected, "this Revit does not have save, sync or export enabled through this connector (levels " + _config.Levels + "). Tell the user.");
            var diffs = new List<string>();
            var pend = new DocOps.Pending();
            Dictionary<string, object> data = null;
            string fail = null, failStatus = Status.Error;
            var sw = Stopwatch.StartNew();
            var job = _jobs.Create(session.Id, "doc " + verb);
            job.Task = Arg.GetString(a, Wire.TaskArg);
            async Task Body()
            {
                job.Phase = "waiting for Revit";
                var r = await RunMain((app, watch, budget) =>
                {
                    job.Phase = "in Revit: " + verb;
                    var doc = DocTarget.Resolve(app, out var docErr);
                    if (doc == null) { fail = docErr; failStatus = Status.TargetUnavailable; return true; }
                    try
                    {
                        switch (verb)
                        {
                            case "save": data = DocOps.Save(app, doc, a, pend, diffs); break;
                            case "sync":  data = DocOps.Sync(doc, a, diffs, _tracker); break;
                            case "pdf": data = DocOps.Pdf(doc, a, session.Sets, DocRegistry.Instance.IdOf(doc), pend, diffs); break;
                            case "ifc": data = ExportOps.Ifc(doc, a, pend, diffs); break;
                            case "schedule_csv": data = ExportOps.ScheduleCsv(doc, a, pend, diffs); break;
                            case "close": data = DocOps.Close(app, doc, a); break;
                            case "verify_disk": data = DocOps.VerifyOnDisk(app, doc, a, session.Sets, DocRegistry.Instance.IdOf(doc)); break;
                        }
                    }
                    catch (OpFail ex) { fail = ex.Message; failStatus = ex.State; }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { fail = ex.GetType().Name + ": " + ex.Message; failStatus = Status.Error; }
                    return true;
                }, highPriority: false, busyAfter: Busy, ct: job.Cts.Token).ConfigureAwait(false);
                if (!r.Ok) { FailJob(job, r, write: true); return; }
                if (fail != null) { _jobs.Fail(job, failStatus, fail); return; }
                if (pend.Files.Count > 0)
                {
                    job.Phase = "verifying files";
                    var expected = data.TryGetValue("n_sheets", out var nh) && nh is long l ? (int)l : 0;
                    data["files"] = DocOps.VerifyFiles(pend, expected, diffs);
                }
                if (diffs.Count > 0) data["differences"] = diffs.Cast<object>().ToList();
                data["ms"] = sw.ElapsedMilliseconds;
                _jobs.Complete(job, data);
            }
            // 0.9.1 (Block I): document operations go through the scheduler: fair sharing between agents and only one at a time
            // per document. Nothing has been touched while queued: a full queue or cancellation while queued ends as a rejection (retryable).
            var owner = Correlation.Current?.AgentId ?? session.Id;
            var exclusive = "doc:" + (TargetId ?? "active");
            _jobs.Run(job, async () =>
            {
                var started = false;
                job.Phase = "queued in the scheduler";
                try { await _scheduler.Run(owner, exclusive, async () => { started = true; await Body().ConfigureAwait(false); return true; }, job.Cts.Token).ConfigureAwait(false); }
                catch (SchedulerBusyException ex) { _jobs.Fail(job, Status.Rejected, ex.Message + ". Nothing was applied."); }
                catch (OperationCanceledException) when (!started) { _jobs.Fail(job, Status.Rejected, "cancelled in the scheduler queue before starting: nothing was applied"); }
            });
            var resp = await AwaitJob(request, job, ct).ConfigureAwait(false);
            if (resp.State == Status.Ok && diffs.Count > 0)
            {
                resp.State = Status.CompletedWithDifferences;
                resp.Warning = "Finished, but verification found differences: review them.";
            }
            return resp;
        }
    }
}
