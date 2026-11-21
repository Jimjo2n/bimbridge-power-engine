// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// agenthost revit instancias | identidad --revit-pid N | nrt04 --revit-pid N --salida DIR --categoria C --ids a,b,c --parametro P --valor V
    /// NRT-04 (alpha3): the operation originates in AgentHost (validated IR plan → PlanRunner → RevitBackend → add-in → plan/commit modo=ensayo
    /// → rollback → independent re-read → evidence). Nothing is saved or synced; the model must be left as it was.
    /// </summary>
    public static partial class RevitCli
    {
        public static int Run(string[] a)
        {
            if (NativeGate.Check(a) is int blocked) return blocked;   // rc1: nothing touches Revit without the operator's authorization
            switch (a[1])
            {
                case "instances":
                    foreach (var d in InstanceDescriptor.ReadAll()) Console.WriteLine(d.Pid + " Revit " + d.RevitVersion + " add-in " + d.AddinVersion + " pipe " + d.PipeName);
                    return 0;
                case "identity":
                {
                    var link = RevitLink.ForPid(int.Parse(Program.Opt(a, "--revit-pid", "0")), out var err);
                    if (link == null) { Console.WriteLine("error " + err); return 1; }
                    using (link)
                    {
                        var b = RevitBackend.Open(link, "read", out err);
                        if (b == null) { Console.WriteLine("error " + err); return 1; }
                        var c = b.Context;
                        Console.WriteLine(Json.Write(new Dictionary<string, object>
                        {
                            ["pid"] = c.GetValueOrDefault("pid"), ["revit"] = c.GetValueOrDefault("revit"), ["addin"] = c.GetValueOrDefault("addin"),
                            ["doc"] = c.GetValueOrDefault("doc"), ["document"] = c.GetValueOrDefault("document"), ["path"] = c.GetValueOrDefault("path"),
                            ["doc_key"] = b.DocKey, ["doc_fingerprint"] = b.DocPrint, ["worksharing"] = c.GetValueOrDefault("worksharing"),
                            ["modified"] = c.GetValueOrDefault("modified"), ["mcp_levels"] = c.GetValueOrDefault("mcp_levels"),
                        }));
                        return 0;
                    }
                }
                case "nrt04":
                    return Nrt04(a);
                case "nrt05":
                    return Nrt05(a);
                case "nrt03":
                    return Nrt03(a);
                case "nrt06":
                    return Nrt06(a);
                case "nrt07":
                    return Nrt07(a);
                case "campaign080":
                    return RunCampaign080(a);
            }
            Console.Error.WriteLine("usage: revit instances | identity --revit-pid N | nrt04 --revit-pid N --salida DIR --categoria C --ids a,b,c --parametro P --valor V [--negativo-modificado] [--aceptar-workshared-local]\n       revit nrt05 --revit-pid N --ruta-esperada C:\\...\\copia.rvt --autorizo-persistente --salida DIR --categoria C --ids a,b,c --parametro P [--valor V]");
            return 64;
        }

        private static string Sha(string s) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(x => x.ToString("x2"))); }

        private static int Nrt04(string[] a)
        {
            var outDir = Program.Opt(a, "--salida") ?? throw new ArgumentException("missing --salida");
            Directory.CreateDirectory(outDir);
            var callsPath = Path.Combine(outDir, "calls.jsonl");
            var log = new List<string>();
            void Log(string s) { var line = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + s; log.Add(line); Console.WriteLine(line); File.AppendAllText(Path.Combine(outDir, "nrt04.log"), line + "\n"); }
            var pid = int.Parse(Program.Opt(a, "--revit-pid", "0"));
            var cat = Program.Opt(a, "--categoria") ?? throw new ArgumentException("missing --categoria");
            var ids = (Program.Opt(a, "--ids") ?? throw new ArgumentException("missing --ids (3 to 5 elements)")).Split(',').Select(x => long.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToList();
            if (ids.Count < 3 || ids.Count > 5) throw new ArgumentException("--ids: between 3 and 5 elements");
            var param = Program.Opt(a, "--parametro", "Comments");
            var value = Program.Opt(a, "--valor") ?? ("NRT-04 " + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            var report = new Dictionary<string, object> { ["test"] = "NRT-04", ["version"] = AgentHostService.Version, ["start"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            var checks = new Dictionary<string, object>();
            report["checks"] = checks;
            void Save() => File.WriteAllText(Path.Combine(outDir, "nrt04_report.json"), Json.Write(report));

            var link = RevitLink.ForPid(pid, out var err);
            if (link == null) { Log("BLOCKED: " + err); report["result"] = "BLOCKED"; report["reason"] = err; Save(); return 3; }
            link.OnCall = c => File.AppendAllText(callsPath, Json.Write(c) + "\n");
            using (link)
            {
                // 1. identity and policy
                var backend = RevitBackend.Open(link, "trial", out err);
                if (backend == null) { Log("BLOCKED: " + err); report["result"] = "BLOCKED"; report["reason"] = err; Save(); return 3; }
                var ctx = backend.Context;
                report["revit"] = new Dictionary<string, object>
                {
                    ["pid"] = ctx.GetValueOrDefault("pid"), ["version"] = ctx.GetValueOrDefault("revit"), ["build"] = ctx.GetValueOrDefault("build"),
                    ["addin"] = ctx.GetValueOrDefault("addin"), ["instance"] = ctx.GetValueOrDefault("instance"), ["pipe"] = link.Calls.LastOrDefault()?.GetValueOrDefault("pipe"),
                    ["mcp_levels"] = ctx.GetValueOrDefault("mcp_levels"),
                };
                report["document"] = new Dictionary<string, object>
                {
                    ["handle"] = ctx.GetValueOrDefault("doc"), ["title"] = ctx.GetValueOrDefault("document"), ["path"] = ctx.GetValueOrDefault("path"),
                    ["key"] = backend.DocKey, ["fingerprint"] = backend.DocPrint, ["worksharing"] = ctx.GetValueOrDefault("worksharing"),
                    ["modified_before"] = ctx.GetValueOrDefault("modified"),
                };
                Log("connected pid " + pid + " · doc " + ctx.GetValueOrDefault("document") + " · " + backend.DocKey + " · worksharing " + ctx.GetValueOrDefault("worksharing"));
                var acceptWs = a.Contains("--aceptar-workshared-local");
                var wsBlock = WorksharedPolicy(ctx, backend.DocKey, acceptWs, out var deviation);
                if (wsBlock != null) { Log("BLOCKED: " + wsBlock); report["result"] = "BLOCKED"; report["reason"] = wsBlock; Save(); return 3; }
                if (deviation != null) { Log("DEVIATION AUTHORIZED BY THE OPERATOR: " + deviation); report["operator_deviation"] = deviation; }

                // 2. independent baseline (values + relations)
                Dictionary<string, object> Snapshot(string label)
                {
                    var r = link.Call(Wire.OpQuery, new Dictionary<string, object>
                    {
                        [Wire.DocArg] = ctx["doc"], ["ids"] = ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList(),
                        ["f"] = new List<object> { "id", "cat", "fam", "type", "lvl", "host", param }, ["page_size"] = 100L,
                    }, false);
                    var c2 = link.Call(Wire.OpContext, new Dictionary<string, object>(), false);
                    var d = r.Data as Dictionary<string, object>;
                    var rows = d != null && d.TryGetValue("rows", out var rv) ? rv : null;
                    var json = Json.Write(rows);
                    return new Dictionary<string, object>
                    {
                        ["moment"] = label, ["status"] = r.State, ["rows"] = rows, ["fingerprint"] = Sha(json), ["rev"] = c2.Rev,
                        ["modified"] = (c2.Data as Dictionary<string, object>)?.GetValueOrDefault("modified"),
                    };
                }
                bool ValidSnapshot(Dictionary<string, object> snap, out string why)
                {
                    why = null;
                    if ((string)snap["status"] != Status.Ok) { why = "query " + snap["status"]; return false; }
                    var n = snap["rows"] is List<object> l ? l.Count : -1;
                    if (n != ids.Count) { why = "the snapshot has " + n + " rows and " + ids.Count + " were expected"; return false; }
                    return true;
                }
                var pre = Snapshot("before");
                // run4 (2026-10-28): Revit leaves IsModified=false after rolling back a TransactionGroup, even if the document had unsaved
                // changes. So as not to erase that warning for anyone, the trial only runs on a document with no pending changes.
                var negative = a.Contains("--negativo-modificado");
                report["test"] = negative ? "NRT-04N (negative IsModified=true)" : "NRT-04";
                if (negative && deviation != null) { Log("BLOCKED: the negative test admits no exceptions (--aceptar-workshared-local)"); report["result"] = "BLOCKED"; report["reason"] = "negative test on workshared"; Save(); return 3; }
                if (negative && !(pre["modified"] is bool modN && modN)) { Log("BLOCKED: the negative test requires a disposable document with an unsaved change (IsModified=true)"); report["baseline"] = pre; report["result"] = "BLOCKED"; report["reason"] = "document has no pending changes"; Save(); return 3; }
                if (!negative && pre["modified"] is bool mod0 && mod0) { Log("BLOCKED: the document has unsaved changes (IsModified=true); the Revit rollback would erase that warning"); report["baseline"] = pre; report["result"] = "BLOCKED"; report["reason"] = "document has unsaved changes"; Save(); return 3; }
                if (!ValidSnapshot(pre, out var whyPre)) { Log("BLOCKED: invalid baseline: " + whyPre); report["baseline"] = pre; report["result"] = "BLOCKED"; report["reason"] = "invalid baseline: " + whyPre; Save(); return 3; }
                report["baseline"] = pre;
                Log("baseline " + ((string)pre["fingerprint"]).Substring(0, 16) + " · rev " + pre["rev"]);

                // 3. IR plan (consultar + fijar_parametro trial) — validated by the AgentHost
                var draft = new PlanDraft { Objective = "NRT-04: E2E AgentHost→Revit trial on a disposable copy", DocKey = backend.DocKey, DocPrint = backend.DocPrint, LengthUnit = "mm", Coordinates = "revit_internal" };
                var q = new OpDraft { Operation = "query_check" }; q.Params["category"] = cat;
                draft.Ops.Add(q);
                var w = new OpDraft { Operation = "set_parameter" };
                w.Scope["category"] = cat; w.Scope["ids"] = ids.Cast<object>().ToList();
                w.Params["parameter"] = param; w.Params["value"] = value; w.Params["units"] = "text";
                w.Preconditions.Add(new Dictionary<string, object> { ["type"] = "targets", ["min"] = 3L, ["max"] = 5L });
                w.DependsOn.Add("#0");
                draft.Ops.Add(w);
                draft.AssignIds();
                var verrs = IrValidator.Validate(draft, out var vp);
                if (verrs.Count > 0) { Log("invalid plan: " + string.Join("; ", verrs)); report["result"] = "FAILED"; report["reason"] = verrs; Save(); return 1; }
                File.WriteAllText(Path.Combine(outDir, "plan_ir.json"), Json.Write(draft.ToJson()));
                var writeOp = vp.Ops[1];
                report["plan"] = new Dictionary<string, object> { ["hash"] = vp.Hash, ["operations"] = vp.Ops.Select(o => (object)(o.Id + " " + o.Operation)).ToList(), ["idempotency_key"] = writeOp.Id, ["derived_authorization"] = writeOp.Authorization.Cast<object>().ToList() };
                Log("plan " + vp.Hash + " · write " + writeOp.Id + " · temporary value '" + value + "'");

                // 4. authorization control: the SAME plan without operator authorization must be rejected before touching anything
                using (var denied = PlanRunner.Open(Path.Combine(outDir, "status_without_authorization"), draft, backend, new RunnerOptions(), out var ddiags))
                {
                    var refused = denied == null && ddiags.Any(x => x.Code == "writes_not_allowed");
                    checks["operator_authorization_required"] = refused;
                    Log("control: without operator authorization → " + (refused ? "rejected (correct)" : "NOT rejected"));
                }

                // 5. real run by the PlanRunner (journal, lease, fencing, postconditions)
                var stateDir = Path.Combine(outDir, "status");
                string summary;
                using (var r = PlanRunner.Open(stateDir, draft, backend, new RunnerOptions { RealWrites = "trial" }, out var diags))
                {
                    foreach (var d in diags) Log("diag " + d);
                    if (r == null) { report["result"] = "FAILED"; report["reason"] = diags.Select(x => (object)x.ToString()).ToList(); Save(); return 1; }
                    summary = r.Run();
                    var rec = r.State.Ops[writeOp.Id];
                    report["journal"] = new Dictionary<string, object>
                    {
                        ["summary"] = summary, ["op_status"] = rec.State, ["attempt"] = (long)rec.Attempt, ["error"] = rec.Error, ["note"] = rec.Note,
                        ["result"] = rec.Result, ["evidence"] = rec.Evidence, ["seq"] = r.State.Seq, ["fencing"] = r.State.Fencing,
                    };
                    Log("runner: " + summary);
                }

                if (negative)
                {
                    // P0 (Codex): both paths must reject BEFORE opening any group, and the pending change and IsModified must stay intact.
                    var d0n = (Dictionary<string, object>)report["journal"];
                    var noteText = Convert.ToString(d0n.GetValueOrDefault("note"), CultureInfo.InvariantCulture) + " " + Convert.ToString(d0n.GetValueOrDefault("error"), CultureInfo.InvariantCulture);
                    var commitCall = link.Calls.LastOrDefault(c => (string)c["op"] == Wire.OpCommit);
                    checks["trial_commit_rejected_by_guard"] = (string)d0n["op_status"] == OpState.Failed && noteText.Contains(Engine.TrialGuard.Code)
                                                                   && commitCall != null && (string)commitCall["status"] == Status.Rejected;
                    var er = link.Call(Wire.OpExec, new Dictionary<string, object> { [Wire.DocArg] = ctx["doc"], ["mode"] = "trial", ["code"] = "return 0;" }, true);
                    report["exec_trial"] = new Dictionary<string, object> { ["status"] = er.State, ["warning"] = er.Warning };
                    checks["exec_trial_rejected_by_guard"] = er.State == Status.Rejected && (er.Warning ?? "").Contains(Engine.TrialGuard.Code);
                    var postN = Snapshot("after");
                    report["after"] = postN;
                    checks["reread_valid"] = ValidSnapshot(postN, out _);
                    checks["identical_fingerprint"] = (bool)checks["reread_valid"] && (string)pre["fingerprint"] == (string)postN["fingerprint"];
                    checks["modified_still_true"] = postN["modified"] is bool m2 && m2;
                    checks["not_saved_or_synced"] = !link.Calls.Any(c => (string)c["op"] == Wire.OpDoc);
                    var passN = checks.Values.All(v => v is bool b && b);
                    report["result"] = passN ? "PASSED" : "FAILED";
                    report["end"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    report["calls"] = link.Calls.Cast<object>().ToList();
                    Save();
                    foreach (var kv in checks) Log((kv.Value is bool b3 && b3 ? "✓ " : "✗ ") + kv.Key);
                    Log("NRT-04N " + report["result"]);
                    return passN ? 0 : 2;
                }

                // 6. independent re-read after the rollback
                var post = Snapshot("after");
                report["after"] = post;
                var ctx2 = link.Call(Wire.OpContext, new Dictionary<string, object>(), false).Data as Dictionary<string, object>;
                var d0 = (Dictionary<string, object>)report["journal"];
                var ev = d0["evidence"] as Dictionary<string, object>;
                checks["trialed_op_status"] = (string)d0["op_status"] == OpState.Trialed;
                checks["group_rolledback_literal"] = ev != null && Convert.ToString(ev.GetValueOrDefault("addin_group_status"), CultureInfo.InvariantCulture) == "RolledBack";
                checks["addin_temporary_verification"] = ev != null && ev.GetValueOrDefault("addin_temporary_ok") is bool t && t;
                checks["addin_restoration"] = ev != null && ev.GetValueOrDefault("addin_restoration_ok") is bool ro && ro;
                checks["reread_valid"] = ValidSnapshot(post, out _);
                checks["identical_fingerprint"] = (bool)checks["reread_valid"] && (string)pre["fingerprint"] == (string)post["fingerprint"];
                // run4: the add-in rev also counts DocumentChanged from rolled-back transactions (invalidates caches: conservative).
                // Corrected and documented criterion: rev does not go back; model equality is proven by fingerprint + restoration.
                checks["rev_does_not_go_back"] = Convert.ToInt64(post["rev"] ?? 0L, CultureInfo.InvariantCulture) >= Convert.ToInt64(pre["rev"] ?? 0L, CultureInfo.InvariantCulture);
                report["rev_before_after"] = new List<object> { pre["rev"], post["rev"] };
                checks["modified_without_changes"] = Equals(pre["modified"], post["modified"]);
                checks["same_document"] = ctx2 != null && Arg.GetString(ctx2, "doc_key") == backend.DocKey && Arg.GetLong(ctx2, "pid") == pid;
                checks["not_saved_or_synced"] = !link.Calls.Any(c => (string)c["op"] == Wire.OpDoc);
                checks["not_persisted_as_applied"] = (string)d0["op_status"] != OpState.Verified;
                var pass = checks.Values.All(v => v is bool b && b);
                report["result"] = pass ? "PASSED" : "FAILED";
                report["end"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                report["calls"] = link.Calls.Cast<object>().ToList();
                Save();
                foreach (var kv in checks) Log((kv.Value is bool b2 && b2 ? "✓ " : "✗ ") + kv.Key);
                Log("NRT-04 " + report["result"]);
                return pass ? 0 : 2;
            }
        }
    
        /// <summary>
        /// alpha3 (2026-10-28, authorized by operator): NRT-04 requires a non-workshared document. With --aceptar-workshared-local the operator
        /// accepts a workshared one ONLY if it is a local file (not cloud/ACC/BIM 360) whose central (if any) is also local and not on a network
        /// share like \\server; the harness stays trial-only (rollback) and never syncs. Returns the blocking reason or null.
        /// </summary>
        public static string WorksharedPolicy(Dictionary<string, object> ctx, string docKey, bool accept, out string deviation)
        {
            deviation = null;
            if (!(ctx.GetValueOrDefault("worksharing") is bool ws && ws)) return null;
            if (!accept) return "the document is workshared; NRT-04 only on a disposable local copy (the operator can accept a local copy with --aceptar-workshared-local)";
            bool Local(string p) => !string.IsNullOrWhiteSpace(p) && p.Length > 2 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
            var docPath = ctx.GetValueOrDefault("path") as string;
            var central = ctx.GetValueOrDefault("central") as string;
            if ((docKey ?? "").StartsWith("nube:", StringComparison.OrdinalIgnoreCase)) return "workshared in the cloud: not accepted even with --aceptar-workshared-local";
            if (!Local(docPath)) return "workshared without a local drive path (" + (docPath ?? "empty") + "): not accepted";
            if (!string.IsNullOrWhiteSpace(central) && !Local(central)) return "workshared with a non-local central (" + central + "): not accepted";
            deviation = "local workshared document accepted by the operator (--aceptar-workshared-local); path " + docPath + "; central " + (central ?? "(none)") + "; trial with rollback only, no save or sync";
            return null;
        }
}
}
