// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Bimbridge.Bridge;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Typed transport to ONE Revit chosen by the operator (alpha3). Reuses the bridge connection (<see cref="RevitConnection"/>:
    /// protocol handshake, check that the pipe server is the announced Revit, heartbeat, timeouts that are NOT mistaken for
    /// "not applied"). It never picks a Revit on its own: explicit pid, or an explicit test pipe.
    /// Every call is recorded (op, ms, bytes, status) as evidence.
    /// </summary>
    public sealed class RevitLink : IDisposable
    {
        private readonly RevitConnection _c = new RevitConnection { ClientName = "agenthost" };
        public int ExpectedPid { get; }
        public TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
        public TimeSpan WriteTimeout = TimeSpan.FromSeconds(180);
        public readonly List<Dictionary<string, object>> Calls = new List<Dictionary<string, object>>();
        /// <summary>Called after every response (to flush evidence to disk immediately).</summary>
        public Action<Dictionary<string, object>> OnCall;

        /// <summary>Issue #4: trace of this AgentHost run; every call sends it in _traza and the add-in journal records it.</summary>
        public readonly string TraceId = "ah-" + Correlation.NewTraceId();

        private RevitLink(int pid, string pipe, InstanceDescriptor d)
        {
            ExpectedPid = pid;
            _c.FixedTarget = (pipe, d);
            // Issue #3: the AgentHost is a named agent. Without BIMBRIDGE_AGENT it declares itself by configuration as "AgentHost <pid>".
            if (string.IsNullOrWhiteSpace(_c.AgentName))
            {
                _c.AgentName = "AgentHost " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
                _c.AgentSource = AgentIdentity.SourceConfig;
            }
        }

        /// <summary>By pid: an instance descriptor with that pid must exist (the add-in loaded in THAT Revit).</summary>
        public static RevitLink ForPid(int pid, out string error)
        {
            error = null;
            if (pid <= 0) { error = "missing --revit-pid: the AgentHost never picks a Revit on its own"; return null; }
            var d = InstanceDescriptor.ReadAll().FirstOrDefault(x => x.Pid == pid);
            if (d == null)
            {
                var all = InstanceDescriptor.ReadAll();
                error = "there is no Revit with the add-in loaded and pid " + pid + ". Open with add-in: " +
                        (all.Count == 0 ? "none" : string.Join(", ", all.Select(x => x.Pid + " (" + x.RevitVersion + ")")));
                return null;
            }
            return new RevitLink(pid, d.PipeName, d);
        }

        /// <summary>Tests without Revit: explicit pipe and the pid the server will report in context.</summary>
        public static RevitLink ForTestPipe(string pipe, int pidInContext) => new RevitLink(pidInContext, pipe, null);

        /// <summary>Retries of a read on revit_ocupado (5 → up to 6 attempts, ~40 s in total with the add-in wait).</summary>
        public int ReadBusyRetries { get; set; } = 5;

        public Response Call(string op, Dictionary<string, object> args, bool write)
        {
            var sw = Stopwatch.StartNew();
            var reqBytes = Json.Write(args ?? new Dictionary<string, object>()).Length;
            args = new Dictionary<string, object>(args ?? new Dictionary<string, object>()) { [Correlation.TraceArg] = TraceId };
            // rc1.1: an idle Revit sometimes takes >5 s to service the ExternalEvent (measured in the rc1 native campaign).
            // Only READS are retried on revit_ocupado: they change nothing, so repeating them is safe.
            // Writes are never retried here; their uncertainty is resolved by the runner reading the model.
            var attempts = 0;
            Response r;
            while (true)
            {
                attempts++;
                r = _c.CallAsync(op, args ?? new Dictionary<string, object>(), write ? WriteTimeout : ReadTimeout, CancellationToken.None).GetAwaiter().GetResult();
                if (write || r.State != Status.RevitBusy || attempts > ReadBusyRetries) break;
                Thread.Sleep(Math.Min(4000, 500 * attempts));
            }
            var rec = new Dictionary<string, object>
            {
                ["t"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), ["op"] = op, ["ms"] = sw.ElapsedMilliseconds,
                ["request_bytes"] = (long)reqBytes, ["response_bytes"] = (long)Json.Write(r.ToJson()).Length, ["status"] = r.State,
                ["pid"] = (long)(_c.Instance?.Pid ?? ExpectedPid), ["pipe"] = _c.FixedTarget?.Pipe,
            };
            if (attempts > 1) rec["attempts"] = (long)attempts;
            if (args != null && args.TryGetValue("mode", out var m)) rec["mode"] = m;
            Calls.Add(rec);
            OnCall?.Invoke(rec);
            return r;
        }

        public void Dispose() => _c.Dispose();
    }

    /// <summary>
    /// REAL AgentHost backend (alpha3): BIM Task IR → <see cref="RevitLink"/> → add-in → EXISTING typed plan/commit engine.
    /// There is no new transaction engine and no free-form C# code: fijar_parametro = plan set_param + commit (trial or apply mode).
    ///
    /// Guarantees:
    /// - identity: context pid = requested pid; document key and fingerprint = those of the plan; the plan sees ONLY its document (doc=dN);
    /// - scope: the ids the add-in plans must be exactly the resolved targets (otherwise nothing is committed);
    /// - authorization: the mode (ensayo/persistente) is set by the operator at launch; the add-in also requires its W level;
    ///   persistente only on a local, non-workshared, non-read-only document with no held queue;
    /// - uncertainty: timeout or lost connection during a commit ⇒ <see cref="UncertainEffectException"/> (the executor observes, does not repeat);
    /// - trial: Ok only if the add-in returns group RolledBack + restoration, AND an independent re-read matches the baseline.
    /// alpha3 catalog: consultar; fijar_parametro with text values. verificar_qc and values with units: not supported here (stated).
    /// </summary>
    public sealed class RevitBackend : IExecutionBackend, IDisposable
    {
        private readonly RevitLink _link;
        private readonly Dictionary<string, Baseline> _baselines = new Dictionary<string, Baseline>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, object>> _trialEvidence = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private string _docHandle;
        /// <summary>Crash points for NRT-03 (tests only; inert by default).</summary>
        public Faults Faults = new Faults();
        /// <summary>The add-in plan returns up to 30 diff rows; alpha3 requires seeing all of them to check the scope.</summary>
        /// <summary>rc1: 200 = one add-in page; the plan diff is checked row by row in a single response (measured in PERFORMANCE_rc1).</summary>
        public const int MaxTargets = 200;
        /// <summary>Maximum pages of a scope read (observation/postconditions): 50 × 200 = 10,000 elements.</summary>
        public const int MaxPages = 50;
        /// <summary>Add-in page_size limit (CatalogRunner.MaxPageSize). NRT-04 run3 (2026-10-28) failed by requesting 1000.</summary>
        public const int AddinMaxPageSize = 200;

        public sealed class Baseline
        {
            public List<long> Ids = new List<long>();
            public Dictionary<long, string> Values = new Dictionary<long, string>();
        }

        public string Name => "revit (pid " + _link.ExpectedPid + ", " + WriteMode + ")";
        public bool TouchesRealModel => true;
        public string DocKey { get; private set; }
        public string DocPrint { get; private set; }
        public string WriteMode { get; }
        public Dictionary<string, object> Context { get; private set; }
        public RevitLink Link => _link;

        private RevitBackend(RevitLink link, string mode) { _link = link; WriteMode = mode; }

        /// <summary>Connects and pins identity. mode: "trial" | "persistent" | "read".</summary>
        public static RevitBackend Open(RevitLink link, string mode, out string error)
        {
            error = null;
            if (mode != "trial" && mode != "persistent" && mode != "read") { error = "mode must be read, trial or persistent"; return null; }
            var b = new RevitBackend(link, mode);
            var r = link.Call(Wire.OpContext, new Dictionary<string, object>(), false);
            if (r.State != Status.Ok || !(r.Data is Dictionary<string, object> d)) { error = "context: " + r.State + " " + r.Warning; return null; }
            b.Context = d;
            var pid = Arg.GetLong(d, "pid");
            if (pid != link.ExpectedPid) { error = "the Revit that answered has pid " + pid + ", not " + link.ExpectedPid + "; not continuing"; return null; }
            b.DocKey = Arg.GetString(d, "doc_key");
            b.DocPrint = Arg.GetString(d, "doc_fingerprint");
            b._docHandle = Arg.GetString(d, "doc");
            if (b.DocKey == null || b._docHandle == null) { error = "the add-in did not report the active document identity (doc_key/doc): add-in older than alpha3 or no document"; return null; }
            if (mode != "read")
            {
                // rc1 P0 fail-closed: a missing or non-boolean flag is NOT "safe"; the explicit value is required.
                foreach (var flag in new[] { "worksharing", "read_only", "modified", "hold" })
                    if (Arg.GetBool(d, flag) == null) { error = "the add-in did not report '" + flag + "' as a boolean; without it nothing is written (fail-closed)"; return null; }
                if (string.IsNullOrEmpty(b.DocPrint)) { error = "the add-in did not report the document fingerprint; without strong identity nothing is written"; return null; }
                var levels = Arg.GetString(d, "mcp_levels") ?? "";
                if (levels.IndexOf('W') < 0) { error = "the add-in of that Revit does not allow changes (levels " + levels + "); the user decides that"; return null; }
            }
            if (mode == "persistent")
            {
                if (Arg.GetBool(d, "worksharing") != false) { error = "persistent rejected: the document is workshared"; return null; }
                if (Arg.GetBool(d, "read_only") != false) { error = "persistent rejected: read-only document"; return null; }
                var docPath = Arg.GetString(d, "path") ?? "";
                if (docPath.Length == 0 || docPath.StartsWith("BIM 360", StringComparison.OrdinalIgnoreCase) || docPath.StartsWith("Autodesk Docs", StringComparison.OrdinalIgnoreCase) || b.DocKey.StartsWith("nube:", StringComparison.OrdinalIgnoreCase))
                { error = "persistent rejected: the document is not a local file (" + docPath + ")"; return null; }
                if (Arg.GetBool(d, "hold") != false || (Arg.GetLong(d, "held_queue") ?? 0) > 0)
                { error = "persistent rejected: Hold/queue active in Revit (the user would apply the change later; it cannot be reconciled)"; return null; }
            }
            return b;
        }

        // =====================================================================================================

        public const string QcLightingTags = "qc_fixture_tags";

        private static bool IsWrite(ValidatedOp op) => op.Operation == "set_parameter" || op.Operation == "set_parameter_batch";

        /// <summary>Expected value per id: the same one for fijar_parametro; the one from the map for fijar_parametro_lote.</summary>
        private static Func<long, string> Expected(ValidatedOp op)
        {
            if (op.Operation == "set_parameter_batch")
            {
                var map = ((IDictionary<string, object>)op.Params["values"]).ToDictionary(kv => long.Parse(kv.Key, CultureInfo.InvariantCulture), kv => Values.Show(kv.Value));
                return id => map.TryGetValue(id, out var v) ? v : null;
            }
            var value = Values.Show(op.Params["value"]);
            return _ => value;
        }

        /// <summary>Lighting fixture and tag QC (add-in check lighting_audit, read-only): deviations = untagged + orphaned + surplus duplicates.</summary>
        private ExecResult RunQc(ValidatedOp op, out long deviations)
        {
            deviations = -1;
            var a = new Dictionary<string, object> { [Wire.DocArg] = _docHandle, ["name"] = "lighting_audit", ["tags"] = "one" };
            var lvl = op.ScopeValue("level"); if (lvl != null) a["lvl"] = new List<object> { lvl };
            var r = _link.Call(Wire.OpCheck, a, false);
            if (r.State != Status.Ok || !(r.Data is Dictionary<string, object> d)) return ExecResult.Fail(ErrorClass.BackendRejection, "check: " + r.State + " " + r.Warning);
            long N(string k, string f = "n") => d.TryGetValue(k, out var v) && v is Dictionary<string, object> b ? (Arg.GetLong(b, f) ?? -1) : -1;
            var untagged = N("untagged"); var orphans = N("orphan_tags"); var dup = N("duplicate_tags", "leftovers");
            if (untagged < 0 || orphans < 0 || dup < 0) return ExecResult.Fail(ErrorClass.BackendRejection, "check: incomplete add-in response (untagged/orphan_tags/duplicate_tags); not certified");
            deviations = untagged + orphans + dup;
            var ok = new ExecResult { Ok = true };
            ok.Data["deviations"] = deviations; ok.Data["untagged"] = untagged; ok.Data["orphan_tags"] = orphans; ok.Data["leftover_duplicate_tags"] = dup;
            ok.Data["fixtures"] = Arg.GetLong(d, "fixtures") ?? -1;
            return ok;
        }

        private static List<long> ScopeIds(ValidatedOp op)
        {
            if (!op.Scope.TryGetValue("ids", out var v) || v == null) return null;
            return ((System.Collections.IEnumerable)v).Cast<object>().Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToList();
        }

        private string Unsupported(ValidatedOp op)
        {
            switch (op.Operation)
            {
                case "query_check": return null;
                case "set_parameter":
                    if (op.Param("units") != "text") return "no_soportado_alpha3: set_parameter against Revit only with units=text (values with units are compared in alpha2/beta1)";
                    if (op.Params.ContainsKey("tolerance")) return "no_soportado_alpha3: tolerance does not apply to text";
                    return null;
                case "set_parameter_batch": return null;   // text values per id (validated by IrValidator)
                case "verify_qc":
                    return op.Param("recipe") == QcLightingTags ? null : "no_soportado_rc1: verify_qc against Revit only with recipe=" + QcLightingTags + " (read-only)";
                case "verify_independent": return null;
                default: return "no_soportado_rc1: " + op.Operation + " is not in the alpha3 Revit catalog";
            }
        }

        /// <summary>Reads (ids, parameter value) from the operation scope. Read-only.</summary>
        private List<(long Id, string Value)> Read(ValidatedOp op, out string error)
        {
            var rows = ReadRows(op, new[] { op.Param("parameter") }, out error);
            if (rows == null) return null;
            var res = rows.Select(r => (r.Id, r.Values[0])).ToList();
            var ids = ScopeIds(op);
            if (ids != null && (res.Count != ids.Count || res.Any(x => !ids.Contains(x.Item1))))
            { error = "scope.ids: the document does not contain exactly those elements with that filter (" + res.Count + "/" + ids.Count + ")"; return null; }
            return res;
        }

        /// <summary>0.8.0: generic paged read of several scope fields (read-only, fail-closed if the total changes).</summary>
        private List<(long Id, string[] Values)> ReadRows(ValidatedOp op, string[] fields, out string error)
        {
            error = null;
            var f = new List<object> { "id" }; f.AddRange(fields);
            var a = new Dictionary<string, object> { [Wire.DocArg] = _docHandle, ["page_size"] = (long)AddinMaxPageSize, ["f"] = f };
            var ids = ScopeIds(op);
            if (ids != null) a["ids"] = ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList();
            var cat = op.ScopeValue("category"); if (cat != null) a["cat"] = new List<object> { cat };
            var lvl = op.ScopeValue("level"); if (lvl != null) a["lvl"] = new List<object> { lvl };
            var r = _link.Call(Wire.OpQuery, a, false);
            if (r.State != Status.Ok || !(r.Data is Dictionary<string, object> d)) { error = "query: " + r.State + " " + r.Warning; return null; }
            var total = Arg.GetLong(d, "total");
            if (total == null) { error = "query: the add-in did not report total; cannot check that everything was read (fail-closed)"; return null; }
            var rows = d.TryGetValue("rows", out var rv) && rv is List<object> rl ? new List<object>(rl) : new List<object>();
            // rc1: typed paging. Following pages are requested until there are EXACTLY total rows; if the total changes between pages, abort.
            var pages = Arg.GetLong(d, "pages") ?? 1;
            if (pages > MaxPages) { error = "the scope spans " + pages + " pages (maximum " + MaxPages + "); narrow it"; return null; }
            // Issue #2: with a cursor, following pages come from the same read (same revision) without going back to the Revit thread.
            // Without a cursor (older add-in or snapshot too large) classic paging with a total check is kept.
            var cursor = Arg.GetString(d, "cursor");
            for (var pg = 2; pg <= pages && rows.Count < total; pg++)
            {
                var a2 = cursor != null
                    ? new Dictionary<string, object> { [Wire.DocArg] = _docHandle, ["cursor"] = cursor, ["page"] = (long)pg }
                    : new Dictionary<string, object>(a) { ["page"] = (long)pg };
                var r2 = _link.Call(Wire.OpQuery, a2, false);
                if (r2.State != Status.Ok || !(r2.Data is Dictionary<string, object> d2)) { error = "query page " + pg + ": " + r2.State + " " + r2.Warning; return null; }
                if (Arg.GetLong(d2, "total") != total) { error = "the total changed between pages (" + total + " → " + Arg.GetLong(d2, "total") + "): the model was modified during the read"; return null; }
                if (d2.TryGetValue("rows", out var rv2) && rv2 is List<object> rl2) rows.AddRange(rl2);
            }
            if (total != rows.Count) { error = "the scope has " + total + " elements and the read returned " + rows.Count; return null; }
            var res = new List<(long, string[])>();
            foreach (var row in rows.OfType<List<object>>())
            {
                var vals = new string[fields.Length];
                for (var i = 0; i < fields.Length; i++) vals[i] = row.Count > i + 1 && row[i + 1] != null ? Convert.ToString(row[i + 1], CultureInfo.InvariantCulture) : null;
                res.Add((Convert.ToInt64(row[0], CultureInfo.InvariantCulture), vals));
            }
            return res;
        }

        /// <summary>
        /// 0.8.0 (CAP-008): verificar_independiente. The expected values come from an independent source declared in the plan; the observed
        /// values are read from the model. Rejected if the expected source is the same one that guided the build. Read-only.
        /// </summary>
        private static List<string> SplitList(string s) =>
            string.IsNullOrWhiteSpace(s) ? new List<string>() : s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private ExecResult RunIndependent(ValidatedOp op, out long deviations)
        {
            deviations = -1;
            var keyParam = op.Param("key_parameter");
            var attr = op.Param("parameter");
            var fields = attr == null ? new[] { keyParam } : new[] { keyParam, attr };
            var rows = ReadRows(op, fields, out var err);
            if (rows == null) return ExecResult.Fail(ErrorClass.BackendRejection, err);
            var input = new Engine.Workflow.IndependentVerifier.Input
            {
                ExpectedSource = new Engine.Workflow.SourceRef { Id = op.Param("source_id"), Kind = op.Param("source_type_kind"), Revision = op.Param("source_revision"),
                                                                 Sha256 = op.Param("source_sha256"), DerivedFrom = SplitList(op.Param("source_derived_from")) },
                BuildSources = { new Engine.Workflow.SourceRef { Id = op.Param("source_construction"), Sha256 = op.Param("source_construction_sha256"), DerivedFrom = SplitList(op.Param("construction_derived_from")) } },
                BuildPlanFingerprint = op.Param("plan_fingerprint"), RequiredRevision = op.Param("required_revision"),
                CompareAttrs = attr == null ? new string[0] : new[] { attr },
            };
            foreach (var kv in (IDictionary<string, object>)op.Params["expected"])
            {
                var it = new Engine.Workflow.IndependentVerifier.Item { Key = kv.Key };
                if (attr != null) it.Attrs[attr] = Values.Show(kv.Value);
                input.Expected.Add(it);
            }
            if (op.Params.TryGetValue("exceptions", out var ex) && ex is IDictionary<string, object> exm)
                foreach (var kv in exm) input.Exceptions[kv.Key] = Values.Show(kv.Value);
            foreach (var row in rows)
            {
                var it = new Engine.Workflow.IndependentVerifier.Item { Key = string.IsNullOrEmpty(row.Values[0]) ? null : row.Values[0], ElementId = row.Id };
                if (attr != null) it.Attrs[attr] = row.Values[1];
                if (it.Key == null) { it.Key = "(no " + keyParam + ") #" + row.Id.ToString(CultureInfo.InvariantCulture); }
                input.Observed.Add(it);
            }
            var v = Engine.Workflow.IndependentVerifier.Verify(input);
            if (v.State != "ok" && v.State != "differences") return ExecResult.Fail(ErrorClass.Precondition, "verify_independent: " + v.State + " — " + v.Reason);
            deviations = v.Missing.Count + v.Extra.Count + v.Duplicates.Count + v.Mismatches.Count;
            var ok = new ExecResult { Ok = true };
            ok.Data["deviations"] = deviations;
            foreach (var kv in v.ToJson()) ok.Data[kv.Key] = kv.Value;
            return ok;
        }

        public Preflight Prepare(ValidatedOp op)
        {
            var u = Unsupported(op);
            if (u != null) return new Preflight { Error = u };
            if (op.Operation == "query_check")
            {
                var r = _link.Call(Wire.OpQuery, new Dictionary<string, object> { [Wire.DocArg] = _docHandle, ["cat"] = new List<object> { op.Param("category") }, ["count"] = true }, false);
                if (r.State != Status.Ok || !(r.Data is Dictionary<string, object> d)) return new Preflight { Error = "query: " + r.State + " " + r.Warning };
                return new Preflight { Targets = (int)(Arg.GetLong(d, "total") ?? 0) };
            }
            if (op.Operation == "verify_qc" || op.Operation == "verify_independent") return new Preflight { Targets = 1 };
            if (WriteMode == "read") return new Preflight { Error = "writes not authorized by the operator (read mode)" };
            var rows = Read(op, out var err);
            if (rows == null) return new Preflight { Error = err };
            if (rows.Count > MaxTargets) return new Preflight { Error = "maximum " + MaxTargets + " targets per operation (the plan diff is checked row by row); there are " + rows.Count };
            return new Preflight { Targets = rows.Count };
        }

        public ExecResult Execute(ValidatedOp op, ExecContext ctx)
        {
            if (op.Operation == "query_check")
            {
                var p = Prepare(op);
                if (p.Error != null) return ExecResult.Fail(ErrorClass.BackendRejection, p.Error);
                var ok = new ExecResult { Ok = true };
                ok.Data["elements"] = (long)p.Targets;
                return ok;
            }
            if (op.Operation == "verify_qc") return RunQc(op, out _);
            if (op.Operation == "verify_independent") return RunIndependent(op, out _);
            var param = op.Param("parameter");
            var expected = Expected(op);
            // 1. baseline (before any write): values and relations the trial must restore
            var rows = Read(op, out var err);
            if (rows == null) return ExecResult.Fail(ErrorClass.Precondition, err);
            var bl = new Baseline { Ids = rows.Select(x => x.Id).ToList(), Values = rows.ToDictionary(x => x.Id, x => x.Value) };
            _baselines[op.Id] = bl;
            if (bl.Ids.Count == 0) return ExecResult.Fail(ErrorClass.NoTargets, "no targets at run time");

            // 2. typed plan (does not write; the add-in validates parameter type, read-only, edit permissions)
            // rc1: one plan with one set_param operation per distinct value (batch = several values, a single commit/TransactionGroup).
            var groups = bl.Ids.GroupBy(i => expected(i)).ToList();
            if (groups.Any(g => g.Key == null)) return ExecResult.Fail(ErrorClass.Precondition, "set_parameter_batch: there are targets with no value in the map");
            var opsJson = groups.Select(g => (object)new Dictionary<string, object> { ["op"] = "set_param", ["ids"] = string.Join(",", g), ["name"] = param, ["value"] = g.Key }).ToList();
            var pr = _link.Call(Wire.OpPlan, new Dictionary<string, object>
            {
                [Wire.DocArg] = _docHandle, ["ops"] = opsJson,
                ["summary"] = "AgentHost " + ctx.PlanHash + " " + op.Id + " attempt " + ctx.Attempt + " (" + WriteMode + ")",
            }, false);
            if (pr.State != Status.Ok || !(pr.Data is Dictionary<string, object> pd)) return ExecResult.Fail(ErrorClass.BackendRejection, "plan: " + pr.State + " " + pr.Warning);
            var planId = Arg.GetString(pd, "plan_id");
            List<long> planned;
            try { planned = PlannedIds(pd); }
            catch (InvalidOperationException ex) { Drop(planId); return ExecResult.Fail(ErrorClass.Precondition, ex.Message); }
            // 3. scope: what is planned is EXACTLY the targets (not one more); if Revit omitted any, say so and do not commit
            if (planned.Except(bl.Ids).Any()) { Drop(planId); return ExecResult.Fail(ErrorClass.Precondition, "the add-in plan touches elements outside the scope: " + string.Join(",", planned.Except(bl.Ids).Take(5))); }
            var unchanged = bl.Ids.Where(i => bl.Values[i] == expected(i)).ToList();
            var expectChanged = bl.Ids.Count - unchanged.Count;
            if (planned.Count != expectChanged)
            { Drop(planId); return ExecResult.Fail(ErrorClass.Precondition, "the add-in planned " + planned.Count + " changes and " + expectChanged + " were expected" + " (" + Arg.GetString(pd, "detail") + ")"); }
            if (expectChanged == 0)
            {
                Drop(planId);
                var same = new ExecResult { Ok = true };
                same.Data["elements"] = (long)bl.Ids.Count; same.Data["changed"] = 0L; same.Data["already_there"] = true;
                if (WriteMode == "trial") { same.Data["trial"] = true; _trialEvidence[op.Id] = new Dictionary<string, object> { ["unchanged"] = true }; }
                return same;
            }

            // 4. commit (trial or apply). From here on, a transport failure = uncertain effect.
            var args = new Dictionary<string, object> { ["plan_id"] = planId };
            if (WriteMode == "trial") args["mode"] = "trial";
            else args[Wire.KeyArg] = op.Id + "#" + ctx.Fencing.ToString(CultureInfo.InvariantCulture);
            Faults.Hit("backend.antes_enviar", op.Id);
            var cr = _link.Call(Wire.OpCommit, args, true);
            Faults.Hit("backend.after_commit", op.Id);   // the add-in may have applied and the AgentHost crashes before recording
            if (Faults.Fire("backend.respuesta_perdida", op.Id))   // NRT-07: the add-in applied, but the IPC response is lost
                cr = Response.Fail(cr.Id, Status.Unknown, "simulated: commit response lost in the pipe (the real effect is unknown)");
            var cd = cr.Data as Dictionary<string, object>;
            if (WriteMode == "trial")
            {
                if (cr.State == "trialed" && cd != null)
                {
                    var trial = cd.TryGetValue("trial", out var ev) ? ev as Dictionary<string, object> : null;
                    var res = new ExecResult { Ok = true };
                    res.Data["elements"] = (long)bl.Ids.Count;
                    res.Data["changed"] = (long)planned.Count;
                    res.Data["trial"] = true;
                    res.Data["plan_id"] = planId;
                    if (trial != null) res.Data["addin_evidence"] = trial;
                    _trialEvidence[op.Id] = trial ?? new Dictionary<string, object>();
                    return res;
                }
                if (cr.State == Status.Conflict || cr.State == Status.RolledBack || cr.State == Status.Invalid || cr.State == Status.Rejected)
                    return ExecResult.Fail(cr.State == Status.Conflict ? ErrorClass.Precondition : ErrorClass.BackendRejection, "commit trial: " + cr.State + " " + cr.Warning);
                throw new UncertainEffectException("commit trial: " + cr.State + " " + cr.Warning);
            }
            if (cr.State == Status.Ok && cd != null)
            {
                var res = new ExecResult { Ok = true };
                res.Data["elements"] = (long)bl.Ids.Count;
                res.Data["changed"] = (long)planned.Count;
                res.Data["plan_id"] = planId;
                if (cd.TryGetValue("verification", out var vv)) res.Data["addin_verification"] = vv;
                return res;
            }
            if (cr.State == Status.Conflict || cr.State == Status.RolledBack || cr.State == Status.Invalid || cr.State == Status.Rejected)
                return ExecResult.Fail(cr.State == Status.Conflict ? ErrorClass.Precondition : ErrorClass.BackendRejection, "commit: " + cr.State + " " + cr.Warning);
            throw new UncertainEffectException("commit: " + cr.State + " " + cr.Warning);
        }

        private static List<long> PlannedIds(Dictionary<string, object> pd)
        {
            var ids = new List<long>();
            if (pd.TryGetValue("rows", out var f) && f is List<object> rows)
                foreach (var row in rows.OfType<List<object>>())
                    if (row.Count > 0 && row[0] is long l) ids.Add(l);
            var total = Arg.GetLong(pd, "changes") ?? ids.Count;
            if (total != ids.Count) throw new InvalidOperationException("the plan returned " + total + " changes but only " + ids.Count + " rows (skipped_rows); alpha3 requires seeing all of them");
            return ids;
        }

        private void Drop(string planId)
        {
            if (planId == null) return;
            try { _link.Call(Wire.OpJob, new Dictionary<string, object> { ["plan_id"] = planId, ["op"] = "drop" }, false); } catch (Exception) { }
        }

        /// <summary>Recovery: what is in the model NOW for this operation. Read-only.</summary>
        public Observation Observe(ValidatedOp op, ExecContext ctx)
        {
            if (!IsWrite(op)) return new Observation { Effect = Effect.NotObserved, Detail = "read: no effect" };
            List<(long Id, string Value)> rows;
            try { rows = Read(op, out var err); if (rows == null) return new Observation { Effect = Effect.Uncertain, Detail = err }; }
            catch (Exception ex) { return new Observation { Effect = Effect.Uncertain, Detail = "could not read the model: " + ex.Message }; }
            var expected = Expected(op);
            if (rows.Count == 0) return new Observation { Effect = Effect.NoTargets, Detail = "the scope selects no elements" };
            var have = rows.Count(x => x.Value == expected(x.Id));
            _baselines.TryGetValue(op.Id, out var bl);
            if (WriteMode == "trial")
            {
                // a trial never leaves an effect: if the baseline exists and matches, there was no persistent effect.
                if (bl != null && rows.All(x => bl.Values.TryGetValue(x.Id, out var b0) && b0 == x.Value)) return new Observation { Effect = Effect.NotObserved, Detail = "trial: the model matches the baseline" };
                if (bl == null && have == 0) return new Observation { Effect = Effect.NotObserved, Detail = "trial: no target has the temporary value" };
                return new Observation { Effect = Effect.Uncertain, Detail = "trial: " + have + "/" + rows.Count + " targets have the temporary value or differ from the baseline" };
            }
            if (have == rows.Count) return new Observation { Effect = Effect.Observed, Detail = "all targets have the value (" + rows.Count + ")" };
            if (have == 0) return new Observation { Effect = Effect.NotObserved, Detail = "no target has the value" };
            return new Observation { Effect = Effect.Partial, Detail = have + "/" + rows.Count + " targets have the value" };
        }

        public PostcheckResult CheckPostconditions(ValidatedOp op, ExecContext ctx)
        {
            if (op.Operation == "query_check") return new PostcheckResult { State = PostState.Satisfied, Detail = "query" };
            if (op.Operation == "verify_qc" || op.Operation == "verify_independent")
            {
                var q = op.Operation == "verify_qc" ? RunQc(op, out var dev) : RunIndependent(op, out dev);
                if (!q.Ok) return new PostcheckResult { State = PostState.NotEvaluable, Detail = q.Error ?? "check without data" };
                var pq = new PostcheckResult { State = dev == 0 ? PostState.Satisfied : PostState.NotSatisfied, Detail = "deviations = " + dev };
                foreach (var kv in q.Data) pq.Evidence[kv.Key] = kv.Value;
                return pq;
            }
            var rows = Read(op, out var err);
            if (rows == null) return new PostcheckResult { State = PostState.NotEvaluable, Detail = err };
            var expected = Expected(op);
            var post = new PostcheckResult();
            post.Evidence["reread"] = (long)rows.Count;
            if (WriteMode == "trial")
            {
                if (!_baselines.TryGetValue(op.Id, out var bl) || !_trialEvidence.TryGetValue(op.Id, out var ev))
                    return new PostcheckResult { State = PostState.NotEvaluable, Detail = "no baseline or trial evidence in this process (it restarted): not certified" };
                var restored = rows.Count(x => bl.Values.TryGetValue(x.Id, out var b0) && b0 == x.Value);
                post.Evidence["restored_reread"] = (long)restored;
                post.Evidence["baseline"] = bl.Ids.Select(i => (object)(i + "=" + (bl.Values[i] ?? "<null>"))).ToList();
                foreach (var kv in ev) post.Evidence["addin_" + kv.Key] = kv.Value;
                var noChanges = ev.ContainsKey("unchanged");
                var group = ev.TryGetValue("group_status", out var g) ? Convert.ToString(g, CultureInfo.InvariantCulture) : null;
                var tempOk = ev.TryGetValue("temporary_ok", out var t) && t is bool tb && tb;
                var restOk = ev.TryGetValue("restoration_ok", out var ro) && ro is bool rb && rb;
                var ok = restored == rows.Count && rows.Count == bl.Ids.Count && (noChanges || (group == "RolledBack" && tempOk && restOk));
                post.State = ok ? PostState.Satisfied : PostState.NotSatisfied;
                post.Detail = ok ? (noChanges ? "no changes needed; model intact" : "temporary value verified by the add-in, group " + group + ", independent re-read = baseline (" + restored + "/" + rows.Count + ")")
                                 : "group " + (group ?? "?") + ", temporary_ok " + tempOk + ", add-in restoration " + restOk + ", re-read " + restored + "/" + rows.Count;
                return post;
            }
            var have = rows.Count(x => x.Value == expected(x.Id));
            post.Evidence["with_value"] = (long)have;
            post.State = have == rows.Count ? PostState.Satisfied : PostState.NotSatisfied;
            post.Detail = have + "/" + rows.Count + " targets with " + op.Param("parameter") + " = expected value";
            return post;
        }

        public void Dispose() => _link.Dispose();
    }
}
