// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>NRT-05 parameters (beta1). The operator decides all of them at launch; the plan cannot change them.</summary>
    public sealed class Nrt05Options
    {
        public int Pid;
        public string OutDir, Category, Param = "Comments", Value, ExpectedPath;
        public List<long> Ids = new List<long>();
        /// <summary>--autorizo-persistente: explicit operator authorization to write, save and verify on disk.</summary>
        public bool Authorized;
        public TimeSpan DocTimeout = TimeSpan.FromMinutes(10);
        /// <summary>Revit path → path readable by this process (identity on the PC; tests redirect it to a temp file).</summary>
        public Func<string, string> DiskPath = p => p;
    }

    /// <summary>
    /// NRT-05 (beta1): real PERSISTENT write originating in AgentHost, and its persistence on disk.
    /// BIM Task IR → AgentHost (PlanRunner, journal, lease, fencing) → persistent RevitBackend → add-in → commit → observe → verify
    /// → doc guardar → SHA-256 of the .rvt (must CHANGE) → doc verificar_disco (opens a COPY of the saved file in the background
    /// and re-reads the values) → resume the same journal (there must be no second commit) → evidence.
    /// Only on a local, non-workshared, disposable .rvt whose actual path matches --ruta-esperada. Never syncs or closes.
    /// </summary>
    public static partial class RevitCli
    {
        private static int Nrt05(string[] a)
        {
            var o = new Nrt05Options
            {
                Pid = int.Parse(Program.Opt(a, "--revit-pid", "0"), CultureInfo.InvariantCulture),
                OutDir = Program.Opt(a, "--salida") ?? throw new ArgumentException("missing --salida"),
                Category = Program.Opt(a, "--categoria") ?? throw new ArgumentException("missing --categoria"),
                Param = Program.Opt(a, "--parametro", "Comments"),
                Value = Program.Opt(a, "--valor"),
                ExpectedPath = Program.Opt(a, "--ruta-esperada"),
                Authorized = a.Contains("--autorizo-persistente"),
                Ids = (Program.Opt(a, "--ids") ?? throw new ArgumentException("missing --ids (3 to 5 elements)")).Split(',').Select(x => long.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToList(),
            };
            var link = RevitLink.ForPid(o.Pid, out var err);
            if (link == null)
            {
                Directory.CreateDirectory(o.OutDir);
                File.WriteAllText(Path.Combine(o.OutDir, "nrt05_report.json"), Json.Write(new Dictionary<string, object> { ["test"] = "NRT-05", ["result"] = "BLOCKED", ["reason"] = err }));
                Console.WriteLine("BLOCKED: " + err);
                return 3;
            }
            using (link) return Nrt05Run(link, o);
        }

        public static string FileSha256(string path)
        {
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var h = SHA256.Create())
                return string.Concat(h.ComputeHash(s).Select(x => x.ToString("x2")));
        }

        /// <summary>NRT-05 core over an already open connection (also used by the tests with a fake add-in).</summary>
        public static int Nrt05Run(RevitLink link, Nrt05Options o)
        {
            Directory.CreateDirectory(o.OutDir);
            var callsPath = Path.Combine(o.OutDir, "calls.jsonl");
            void Log(string s)
            {
                var line = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + s;
                Console.WriteLine(line); File.AppendAllText(Path.Combine(o.OutDir, "nrt05.log"), line + "\n");
            }
            var value = o.Value ?? ("NRT-05 " + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            var report = new Dictionary<string, object> { ["test"] = "NRT-05", ["version"] = AgentHostService.Version, ["start"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            var checks = new Dictionary<string, object>();
            report["checks"] = checks;
            void Save() => File.WriteAllText(Path.Combine(o.OutDir, "nrt05_report.json"), Json.Write(report));
            int Blocked(string why) { Log("BLOCKED: " + why); report["result"] = "BLOCKED"; report["reason"] = why; report["calls"] = link.Calls.Cast<object>().ToList(); Save(); return 3; }
            int Failed(object why) { Log("FAILED: " + Json.Write(why)); report["result"] = "FAILED"; report["reason"] = why; report["calls"] = link.Calls.Cast<object>().ToList(); Save(); return 1; }

            if (!o.Authorized) return Blocked("missing --autorizo-persistente: NRT-05 saves the file; only with explicit operator authorization");
            if (o.Ids.Count < 3 || o.Ids.Count > 5) return Blocked("--ids: between 3 and 5 elements");
            if (string.IsNullOrWhiteSpace(o.ExpectedPath)) return Blocked("missing --ruta-esperada (the actual path of the disposable .rvt)");
            link.OnCall = c => File.AppendAllText(callsPath, Json.Write(c) + "\n");

            // 1. identity + policy: the persistent backend already rejects workshared, cloud/non-local, read-only and held queue.
            var backend = RevitBackend.Open(link, "persistent", out var err);
            if (backend == null) return Blocked(err);
            var ctx = backend.Context;
            var docPath = ctx.GetValueOrDefault("path") as string;
            report["revit"] = new Dictionary<string, object>
            {
                ["pid"] = ctx.GetValueOrDefault("pid"), ["version"] = ctx.GetValueOrDefault("revit"), ["build"] = ctx.GetValueOrDefault("build"),
                ["addin"] = ctx.GetValueOrDefault("addin"), ["instance"] = ctx.GetValueOrDefault("instance"), ["pipe"] = link.Calls.LastOrDefault()?.GetValueOrDefault("pipe"),
                ["mcp_levels"] = ctx.GetValueOrDefault("mcp_levels"),
            };
            report["document"] = new Dictionary<string, object>
            {
                ["handle"] = ctx.GetValueOrDefault("doc"), ["title"] = ctx.GetValueOrDefault("document"), ["path"] = docPath, ["key"] = backend.DocKey,
                ["fingerprint"] = backend.DocPrint, ["worksharing"] = ctx.GetValueOrDefault("worksharing"), ["modified_before"] = ctx.GetValueOrDefault("modified"),
            };
            Log("connected pid " + o.Pid + " · " + docPath + " · " + backend.DocKey);
            string Norm(string p) => (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
            if (!string.Equals(Norm(docPath), Norm(o.ExpectedPath), StringComparison.OrdinalIgnoreCase))
                return Blocked("the actual document path (" + docPath + ") is not the authorized one (" + o.ExpectedPath + ")");
            var file = o.DiskPath(docPath);
            if (!File.Exists(file)) return Blocked("file not found on disk: " + docPath);

            Dictionary<string, object> Snapshot(string label)
            {
                var r = link.Call(Wire.OpQuery, new Dictionary<string, object>
                {
                    [Wire.DocArg] = ctx["doc"], ["ids"] = o.Ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList(),
                    ["f"] = new List<object> { "id", o.Param }, ["page_size"] = 100L,
                }, false);
                var c2 = link.Call(Wire.OpContext, new Dictionary<string, object>(), false);
                var rows = (r.Data as Dictionary<string, object>)?.GetValueOrDefault("rows") as List<object>;
                var vals = new Dictionary<string, object>();
                foreach (var row in rows ?? new List<object>())
                    if (row is List<object> l && l.Count >= 2) vals[Convert.ToString(l[0], CultureInfo.InvariantCulture)] = l[1];
                return new Dictionary<string, object>
                {
                    ["moment"] = label, ["status"] = r.State, ["values"] = vals, ["rev"] = c2.Rev,
                    ["modified"] = (c2.Data as Dictionary<string, object>)?.GetValueOrDefault("modified"),
                    ["doc_key"] = (c2.Data as Dictionary<string, object>)?.GetValueOrDefault("doc_key"),
                };
            }
            bool AllEqual(Dictionary<string, object> vals, string v) =>
                vals.Count == o.Ids.Count && o.Ids.All(i => vals.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out var x) && Equals(Convert.ToString(x, CultureInfo.InvariantCulture), v));

            // 2. baseline: document with no pending changes (the harness save must not carry other people's changes) and file SHA.
            var pre = Snapshot("before");
            report["baseline"] = pre;
            if (pre["status"] as string != Status.Ok || ((Dictionary<string, object>)pre["values"]).Count != o.Ids.Count) return Blocked("invalid baseline (" + pre["status"] + ")");
            if (pre["modified"] is bool m0 && m0) return Blocked("the document has unsaved changes; NRT-05 would save changes that are not its own");
            if (AllEqual((Dictionary<string, object>)pre["values"], value)) return Blocked("the elements already have the value '" + value + "': the save would prove nothing");
            var shaBefore = FileSha256(file);
            report["sha256_before"] = shaBefore;
            Log("baseline · rev " + pre["rev"] + " · sha " + shaBefore.Substring(0, 16));

            // 3. IR plan
            var draft = new PlanDraft { Objective = "NRT-05: E2E persistent AgentHost→Revit write on a disposable copy", DocKey = backend.DocKey, DocPrint = backend.DocPrint, LengthUnit = "mm", Coordinates = "revit_internal" };
            var q = new OpDraft { Operation = "query_check" }; q.Params["category"] = o.Category;
            draft.Ops.Add(q);
            var w = new OpDraft { Operation = "set_parameter" };
            w.Scope["category"] = o.Category; w.Scope["ids"] = o.Ids.Cast<object>().ToList();
            w.Params["parameter"] = o.Param; w.Params["value"] = value; w.Params["units"] = "text";
            w.Preconditions.Add(new Dictionary<string, object> { ["type"] = "targets", ["min"] = 3L, ["max"] = 5L });
            w.DependsOn.Add("#0");
            draft.Ops.Add(w);
            draft.AssignIds();
            var verrs = IrValidator.Validate(draft, out var vp);
            if (verrs.Count > 0) return Failed(verrs);
            File.WriteAllText(Path.Combine(o.OutDir, "plan_ir.json"), Json.Write(draft.ToJson()));
            var writeOp = vp.Ops[1];
            report["plan"] = new Dictionary<string, object>
            {
                ["hash"] = vp.Hash, ["operations"] = vp.Ops.Select(x => (object)(x.Id + " " + x.Operation)).ToList(), ["op_id"] = writeOp.Id,
                ["derived_authorization"] = writeOp.Authorization.Cast<object>().ToList(),
                ["original_values"] = pre["values"], ["expected_value"] = value,
            };
            Log("plan " + vp.Hash + " · write " + writeOp.Id + " · value '" + value + "'");

            // 4. control: without operator authorization the same plan is rejected before touching anything
            using (var denied = PlanRunner.Open(Path.Combine(o.OutDir, "status_without_authorization"), draft, backend, new RunnerOptions(), out var ddiags))
                checks["operator_authorization_required"] = denied == null && ddiags.Any(x => x.Code == "writes_not_allowed");

            // 5. persistent run (real commit) by the PlanRunner
            var stateDir = Path.Combine(o.OutDir, "status");
            using (var r = PlanRunner.Open(stateDir, draft, backend, new RunnerOptions { RealWrites = "persistent" }, out var diags))
            {
                if (r == null) return Failed(diags.Select(x => (object)x.ToString()).ToList());
                var summary = r.Run();
                var rec = r.State.Ops[writeOp.Id];
                report["journal"] = new Dictionary<string, object>
                {
                    ["summary"] = summary, ["op_status"] = rec.State, ["directive_status"] = OpState.DirectiveName(rec.State, wroteNothing: rec.Attempt == 0),
                    ["attempt"] = (long)rec.Attempt, ["error"] = rec.Error, ["note"] = rec.Note, ["result"] = rec.Result, ["evidence"] = rec.Evidence,
                    ["seq"] = r.State.Seq, ["fencing"] = r.State.Fencing, ["idempotency_key"] = writeOp.Id + "#" + r.State.Fencing.ToString(CultureInfo.InvariantCulture),
                    ["journal_path"] = r.State.JournalPath,
                };
                Log("runner: " + summary + " · " + rec.State);
            }
            var d0 = (Dictionary<string, object>)report["journal"];
            checks["verified_op_status"] = (string)d0["op_status"] == OpState.Verified;
            var commits1 = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit);
            checks["single_commit"] = commits1 == 1;
            report["transaction"] = link.Calls.Where(c => (string)c["op"] == Wire.OpCommit).Select(c => (object)new Dictionary<string, object> { ["status"] = c["status"], ["ms"] = c["ms"], ["mode"] = c.GetValueOrDefault("mode") }).ToList();

            // 6. independent observation after the commit (in memory)
            var post = Snapshot("after_commit");
            report["after_commit"] = post;
            checks["values_after_commit"] = AllEqual((Dictionary<string, object>)post["values"], value);
            checks["same_document"] = (post["doc_key"] as string) == backend.DocKey;
            checks["modified_after_commit"] = post["modified"] is bool m1 && m1;
            // rc1 P0: conditional save. ANY failed prior check (authorization, op, single commit, values,
            // document, modified) stops the harness before saving; nothing potentially persistent is done afterwards.
            var failedBefore = checks.Where(kv => !(kv.Value is bool ok0 && ok0)).Select(kv => kv.Key).ToList();
            if (failedBefore.Count > 0)
            {
                report["result"] = "FAILED"; report["reason"] = "failed prior checks (" + string.Join(", ", failedBefore) + "): not saving"; report["calls"] = link.Calls.Cast<object>().ToList(); Save();
                Log("FAILED before saving: " + string.Join(", ", failedBefore));
                return 2;
            }

            // 7. save (an event separate from the commit) and check that the file CHANGED on disk
            var oldTimeout = link.WriteTimeout;
            link.WriteTimeout = o.DocTimeout;
            var save = link.Call(Wire.OpDoc, new Dictionary<string, object> { [Wire.DocArg] = ctx["doc"], ["action"] = "save" }, true);
            report["save"] = new Dictionary<string, object> { ["status"] = save.State, ["warning"] = save.Warning, ["data"] = save.Data };
            checks["save_ok"] = save.State == Status.Ok;
            var shaAfter = File.Exists(file) ? FileSha256(file) : null;
            report["sha256_after"] = shaAfter;
            checks["sha256_changed"] = shaAfter != null && shaAfter != shaBefore;
            var afterSave = Snapshot("after_save");
            report["after_save"] = afterSave;
            checks["no_pending_changes_after_save"] = afterSave["modified"] is bool m2 && !m2;
            Log("save " + save.State + " · sha " + (shaAfter ?? "?").Substring(0, Math.Min(16, (shaAfter ?? "?").Length)));

            if (!(bool)checks["save_ok"] || !(bool)checks["sha256_changed"] || !(bool)checks["no_pending_changes_after_save"])
            {
                report["result"] = "FAILED"; report["reason"] = "the save was not proven: no disk verification and no resume"; report["calls"] = link.Calls.Cast<object>().ToList(); Save();
                foreach (var kv in checks) Log((kv.Value is bool bx && bx ? "✓ " : "✗ ") + kv.Key);
                Log("NRT-05 FAILED after saving");
                return 2;
            }

            // 8. persistence: open a COPY of the saved file (another Document, without touching the open one) and re-read
            var disk = link.Call(Wire.OpDoc, new Dictionary<string, object>
            {
                [Wire.DocArg] = ctx["doc"], ["action"] = "verify_disk", ["parameter"] = o.Param,
                ["ids"] = o.Ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList(),
            }, true);
            link.WriteTimeout = oldTimeout;
            var dd = disk.Data as Dictionary<string, object>;
            report["disk_verification"] = new Dictionary<string, object> { ["status"] = disk.State, ["warning"] = disk.Warning, ["data"] = dd };
            var diskVals = new Dictionary<string, object>();
            foreach (var row in (dd?.GetValueOrDefault("rows") as List<object>) ?? new List<object>())
                if (row is List<object> l && l.Count >= 3 && l[1] is bool ex && ex) diskVals[Convert.ToString(l[0], CultureInfo.InvariantCulture)] = l[2];
            checks["disk_expected_values"] = disk.State == Status.Ok && AllEqual(diskVals, value);
            checks["disk_sha_is_saved"] = dd != null && string.Equals(dd.GetValueOrDefault("sha256") as string, shaAfter, StringComparison.OrdinalIgnoreCase);
            Log("verify_disk " + disk.State);

            // 9. duplicate: resuming THE SAME journal must not produce a second commit or change the model
            using (var again = PlanRunner.Open(stateDir, null, backend, new RunnerOptions { RealWrites = "persistent" }, out var diags2))
            {
                if (again != null) report["resumption"] = again.Run();
                else report["resumption"] = diags2.Select(x => (object)x.ToString()).ToList();
            }
            checks["no_duplicate_commit"] = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) == commits1;
            var post2 = Snapshot("after_resume");
            report["after_resume"] = post2;
            checks["resume_does_not_modify"] = post2["modified"] is bool m3 && !m3 && AllEqual((Dictionary<string, object>)post2["values"], value);

            // 10. metrics (from calls.jsonl)
            report["metrics"] = link.Calls.GroupBy(c => (string)c["op"]).ToDictionary(g => g.Key, g => (object)new Dictionary<string, object>
            {
                ["n"] = (long)g.Count(), ["ms_total"] = g.Sum(c => Convert.ToInt64(c["ms"], CultureInfo.InvariantCulture)),
                ["ms_max"] = g.Max(c => Convert.ToInt64(c["ms"], CultureInfo.InvariantCulture)),
                ["request_bytes"] = g.Sum(c => Convert.ToInt64(c["request_bytes"], CultureInfo.InvariantCulture)),
                ["response_bytes"] = g.Sum(c => Convert.ToInt64(c["response_bytes"], CultureInfo.InvariantCulture)),
            });
            var pass = checks.Values.All(v => v is bool b && b);
            report["result"] = pass ? "PASSED" : "FAILED";
            report["end"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report["calls"] = link.Calls.Cast<object>().ToList();
            Save();
            foreach (var kv in checks) Log((kv.Value is bool b2 && b2 ? "✓ " : "✗ ") + kv.Key);
            Log("NRT-05 " + report["result"]);
            return pass ? 0 : 2;
        }
    }
}
