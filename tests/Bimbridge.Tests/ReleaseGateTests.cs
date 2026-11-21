// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bimbridge.Engine;
using Bimbridge.Engine.Tasks;
using Bimbridge.Protocol;
using Bimbridge.Scripting;
using Xunit;
using FakeModel = Bimbridge.Tests.TaskEngineTests.FakeModel;
using FakeBackend = Bimbridge.Tests.TaskEngineTests.FakeBackend;

namespace Bimbridge.Tests
{
    /// <summary>
    /// Tests for the 0.6.1 release gate (RELEASE_GATE_0.6.1.md). Each test names the requirement (Rnn) and the sub-criterion it proves.
    /// Designed to refute: each one attempts the path that must NOT happen and checks that the enforcement point prevents it.
    /// </summary>
    public class ReleaseGateTests
    {
        private const string DocKey = "archivo:c:\\practica.rvt";

        private static string TempRoot() => Path.Combine(Path.GetTempPath(), "bimbridge-gate-" + Guid.NewGuid().ToString("N"));

        private static TaskRecord Draft(string id, int n, Dictionary<string, object> extra = null, string docKey = DocKey, Dictionary<string, object> policy = null)
        {
            var args = new Dictionary<string, object> { ["n"] = (long)n };
            if (extra != null) foreach (var kv in extra) args[kv.Key] = kv.Value;
            return new TaskRecord
            {
                Id = id, Objective = "prueba", RecipeName = "fake", RecipeVersion = "1", RecipeHash = "h", ContentHash = "c-" + id + n,
                Args = args, Docs = new List<TaskDoc> { new TaskDoc { Key = docKey, Title = "practica", Generation = "inst#1" } },
                Policy = policy ?? new Dictionary<string, object>(),
            };
        }

        private static TaskManager Mgr(string root, FakeModel m, ITaskAuthority auth, AuditLog audit = null, string instance = "A") =>
            new TaskManager(new TaskStore(root), instance, t => new FakeBackend(m))
            {
                Delay = (d, ct) => Task.Delay(5, ct), MaxWait = TimeSpan.FromSeconds(20), BudgetMs = 200, Authority = auth, Audit = audit,
            };

        private static async Task<TaskRecord> Run(TaskManager mgr, TaskRecord draft)
        {
            mgr.Start(draft, "// receta", "prueba");
            await mgr.Completion(draft.Id);
            return mgr.Get(draft.Id, out _);
        }

        private static async Task<TaskRecord> Resume(TaskManager mgr, string id)
        {
            var r = mgr.Resume(id, "prueba", null);
            Assert.Null(r.Error);
            await mgr.Completion(id);
            return mgr.Get(id, out _);
        }

        private sealed class AllowAll : ITaskAuthority { public string Check(TaskRecord t, string c, DateTime n) => null; }

        // =========================== R01 audit first ===========================

        [Fact]
        public async Task R01_IntentFails_NoBatchRuns_ModelUntouched()
        {
            var root = TempRoot();
            var audit = new AuditLog(Path.Combine(root, "aud", "a.jsonl")) { FaultBeforeWrite = op => { if (op == "tarea_lote") throw new IOException("disk full (simulated)"); } };
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, new AllowAll(), audit), Draft("r01a", 20));
            Assert.Equal(TaskState.Partial, t.State);
            Assert.StartsWith("auditoria_no_disponible", t.Reason);
            Assert.Equal(0, m.Visits);
            Assert.Empty(m.Live);
        }

        [Fact]
        public async Task R01_OutcomeFails_AfterCommit_IsRecoverable_NoDuplicates()
        {
            var root = TempRoot();
            var path = Path.Combine(root, "aud", "a.jsonl");
            var writes = 0;
            var audit = new AuditLog(path);
            // exactly the first "result" of a batch fails: the intent is already there, the write happens, the result never arrives.
            audit.FaultBeforeWrite = op => { if (op == "tarea_lote" && ++writes == 2) throw new IOException("disk full (simulated)"); };
            var m = new FakeModel();
            var mgr = Mgr(root, m, new AllowAll(), audit);
            var t = await Run(mgr, Draft("r01b", 30));
            Assert.Equal(TaskState.Partial, t.State);
            Assert.StartsWith("auditoria_no_disponible", t.Reason);
            var pending = AuditLog.PendingIntents(path);
            Assert.Single(pending);
            var done = m.Live.Count;
            Assert.True(done > 0, "the batch did run before the audit failure");
            mgr.Get("r01b", out var units);
            Assert.Equal(done, units.Units.Values.Count(u => u.State == UnitState.Committed)); // it is in unidades.jsonl: recoverable
            audit.FaultBeforeWrite = null;
            t = await Resume(mgr, "r01b");
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(30, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.True(AuditLog.Verify(path).Ok);
        }

        [Fact]
        public async Task R01_EveryBatchAndBarrier_HasIntentAndOutcome()
        {
            var root = TempRoot();
            var path = Path.Combine(root, "aud", "a.jsonl");
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, new AllowAll(), new AuditLog(path)), Draft("r01c", 40, new Dictionary<string, object> { ["barrera_en"] = 20L }));
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Empty(AuditLog.PendingIntents(path));
            var lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l.Contains("\"tarea_barrera_pdf\"") && l.Contains("\"intencion\""));
            Assert.True(lines.Count(l => l.Contains("\"estado\":\"intencion\"")) >= 2);
            Assert.True(AuditLog.Verify(path).Ok);
        }

        [Fact]
        public void R01_AuditGate_RefusesToApply_WhenIntentCannotBePersisted()
        {
            var root = TempRoot();
            var audit = new AuditLog(Path.Combine(root, "a.jsonl")) { FaultBeforeWrite = _ => throw new IOException("out of space") };
            var applied = 0;
            var r = AuditGate.Apply(audit, "s", "aplicar_cola", "doc", new Dictionary<string, object>(), () => { applied++; return "aplicado"; }, x => x, why => why, (x, why) => why);
            Assert.Equal(0, applied);
            Assert.StartsWith("auditoria_no_disponible", r);
            Assert.Equal(0, AuditGate.Apply<string>(null, "s", "op", "doc", null, () => { applied++; return "x"; }, x => x, why => "rechazado", (x, why) => why) == "rechazado" ? applied : -1);

            // failing result: applied once, the intent stays pending
            var path2 = Path.Combine(root, "b.jsonl");
            var a2 = new AuditLog(path2);
            var n = 0;
            a2.FaultBeforeWrite = _ => { if (++n == 2) throw new IOException("connection dropped"); };
            var r2 = AuditGate.Apply(a2, "s", "aplicar_cola", "doc", new Dictionary<string, object>(), () => { applied++; return "aplicado"; }, x => x, why => why, (x, why) => x + "|" + why);
            Assert.StartsWith("aplicado|" + AuditGate.Incomplete, r2); // rc3: never returned as a normal result
            Assert.Equal(1, applied);
            Assert.Single(AuditLog.PendingIntents(path2));
        }

        // =========================== R02 integrity and concurrency ===========================

        [Fact]
        public void R02_InvalidChainAtInit_IsQuarantined_NewChainStartsClean()
        {
            var root = TempRoot(); Directory.CreateDirectory(root);
            var p = Path.Combine(root, "a.jsonl");
            var a = new AuditLog(p);
            for (var i = 0; i < 3; i++) a.Append("s", "op" + i, "ok");
            File.WriteAllText(p, File.ReadAllText(p).Replace("\"op1\"", "\"opX\"")); // tampering
            var b = new AuditLog(p);
            Assert.Single(b.Quarantined);
            Assert.True(File.Exists(b.Quarantined[0]));
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok);
            Assert.Equal(1, v.Lines);
            Assert.Contains("\"cuarentena\"", File.ReadAllText(p));
            b.Append("s", "despues", "ok");
            Assert.True(AuditLog.Verify(p).Ok);
        }

        [Fact]
        public void R02_TruncatedTailFromAnotherWriter_IsSetAsideWithEvidence_NotQuarantined()
        {
            // AUD-01: a fragment without a newline beyond the head is an interrupted write (never committed), not corruption.
            var root = TempRoot(); Directory.CreateDirectory(root);
            var p = Path.Combine(root, "a.jsonl");
            var a = new AuditLog(p);
            a.Append("s", "op", "ok");
            File.AppendAllText(p, "{\"seq\":2,\"t\":\"cort"); // another process died mid-line
            a.Append("s", "op2", "ok");
            Assert.Empty(a.Quarantined);
            Assert.Single(a.Repaired);
            Assert.Equal("{\"seq\":2,\"t\":\"cort", File.ReadAllText(a.Repaired[0]));   // the fragment is kept intact as evidence
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(3, v.Lines);                                                     // op, documented recovery, op2
            Assert.Contains("recuperacion_cola", File.ReadAllText(p));
        }

        [Fact]
        public void R02_WriteFailure_IsVisible_ChainStaysValid()
        {
            var root = TempRoot(); Directory.CreateDirectory(root);
            var p = Path.Combine(root, "a.jsonl");
            var a = new AuditLog(p);
            a.Append("s", "op", "ok");
            a.FaultBeforeWrite = _ => throw new IOException("disk full");
            Assert.Throws<JournalUnavailableException>(() => a.Append("s", "op", "ok"));
            Assert.Equal(1, a.Failures);
            a.FaultBeforeWrite = null;
            a.Append("s", "op", "ok");
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join(";", v.Errors));
            Assert.Equal(2, v.Lines);
        }

        [Fact]
        public void R02_EightRealProcesses_AppendConcurrently_ChainIsConsistent()
        {
            var root = TempRoot(); Directory.CreateDirectory(root);
            var p = Path.Combine(root, "a.jsonl");
            new AuditLog(p).Append("s", "genesis", "ok");
            var start = DateTime.UtcNow.AddSeconds(4).Ticks.ToString();
            // #27: bounded supervision of real processes. stdout/stderr drained from start, one wall-clock deadline for all, tree termination
            // if it expires, release in finally, and the chain is verified EVEN IF a child fails (the diagnostics do not hide a broken append).
            var runs = ProbeRun.StartAll(8, i => new[] { p, "50", "proc" + i, "audit", start }, TimeSpan.FromSeconds(180));
            try
            {
                var report = string.Join(" | ", runs.Select(r => r.Describe()));
                var v = AuditLog.Verify(p);
                Assert.True(v.Ok, "chain: " +string.Join("; ", v.Errors.Take(5)) + " || " + report);
                Assert.All(runs, r => Assert.True(r.Exited && r.ExitCode == 0 && r.Stdout.Trim() == "APPENDED 50", report));
                Assert.Equal(1 + 8 * 50, v.Lines);
                Assert.Empty(Directory.GetFiles(root, "*.cuarentena-*"));
                // No losses or duplicates: exactly 50 lines per process, seq without gaps (Verify) and unique cid where present.
                var lines = File.ReadAllLines(p).Where(l => l.Length > 0).Select(Json.ParseObject).ToList();
                for (var i = 0; i < 8; i++) Assert.Equal(50, lines.Count(l => Arg.GetString(l, "autor") == "proc" + i));
                var cids = lines.Select(l => Arg.GetString(l, "cid")).Where(c => c != null).ToList();
                Assert.Equal(cids.Count, cids.Distinct().Count());
                Assert.Empty(Directory.Exists(p + ".pend") ? Directory.GetFiles(p + ".pend", "*.json") : new string[0]);
            }
            finally
            {
                foreach (var r in runs) r.Dispose();
                try { Directory.Delete(root, true); } catch (Exception) { /* limpieza best-effort del directorio temporal */ }
            }
        }

        /// <summary>
        /// Supervised helper process (#27): pid, start and end, exit code, bounded stdout/stderr, tree termination when the deadline expires,
        /// and guaranteed release. A start failure is recorded as such (not as empty output).
        /// </summary>
        private sealed class ProbeRun : IDisposable
        {
            private const int MaxChars = 4000;
            public System.Diagnostics.Process Proc;
            public int Pid;
            public DateTime StartedUtc, EndedUtc;
            public bool Exited, Killed;
            public int ExitCode = int.MinValue;
            public string Stdout = "", Stderr = "", StartError;
            private Task<string> _out, _err;

            public static List<ProbeRun> StartAll(int n, Func<int, string[]> args, TimeSpan deadline)
            {
                var runs = new List<ProbeRun>();
                for (var i = 0; i < n; i++)
                {
                    var r = new ProbeRun { StartedUtc = DateTime.UtcNow };
                    try
                    {
                        r.Proc = LeaseFencingTests.StartProbe(args(i));
                        r.Pid = r.Proc.Id;
                        r._out = r.Proc.StandardOutput.ReadToEndAsync();   // drained from start: a child never blocks on a full buffer
                        r._err = r.Proc.StandardError.ReadToEndAsync();
                    }
                    catch (Exception ex) { r.StartError = ex.GetType().Name + ": " + ex.Message; }
                    runs.Add(r);
                }
                var until = DateTime.UtcNow + deadline;
                foreach (var r in runs.Where(x => x.Proc != null))
                {
                    var left = until - DateTime.UtcNow;
                    r.Exited = r.Proc.WaitForExit((int)Math.Max(0, left.TotalMilliseconds));
                    if (!r.Exited)
                    {
                        try { r.Proc.Kill(entireProcessTree: true); r.Killed = true; } catch (Exception) { }
                        r.Exited = r.Proc.WaitForExit(5000);
                    }
                    r.EndedUtc = DateTime.UtcNow;
                    if (r.Exited) r.ExitCode = r.Proc.ExitCode;
                    // Bounded read: if a grandchild keeps the stream open, it does not wait forever.
                    r.Stdout = Bounded(r._out); r.Stderr = Bounded(r._err);
                }
                return runs;
            }

            private static string Bounded(Task<string> t)
            {
                if (t == null) return "";
                if (!t.Wait(5000)) return "<stream not closed after 5 s>";
                var s = t.Result ?? "";
                return s.Length > MaxChars ? s.Substring(0, MaxChars) + "…" : s;
            }

            public string Describe() =>
                StartError != null ? "arranque fallido: " + StartError
                : "pid=" + Pid + " exit=" + (Exited ? ExitCode.ToString() : "not exited") + (Killed ? " (killed on deadline)" : "") +
                  " t=" + (EndedUtc - StartedUtc).TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s" +
                  " out='" + Stdout.Trim() + "' err='" + Stderr.Trim() + "'";

            public void Dispose()
            {
                if (Proc == null) return;
                try { if (!Proc.HasExited) Proc.Kill(entireProcessTree: true); } catch (Exception) { }   // no orphans
                Proc.Dispose();
            }
        }

        // =========================== R03 independent authority ===========================

        private static (GrantStore store, GrantAuthority auth) Grants(string root)
        {
            var g = new GrantStore(Path.Combine(root, "autorizaciones"));
            return (g, new GrantAuthority(g));
        }

        [Fact]
        public async Task R03_AgentRequestAlone_DoesNotAuthorize_NothingRuns()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            // the agent "asks for" capabilities: recorded as a request and as a requested policy, not as a permission
            g.Request("r03a", DocKey, null, "practica", new[] { Capability.Modify }, "agente", "x", DateTime.UtcNow);
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, auth), Draft("r03a", 10, policy: new Dictionary<string, object>
            {
                ["solicitadas"] = new List<object> { Capability.Modify }, ["capacidades"] = new List<object> { "all" }, ["aprobado"] = true,
            }));
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.StartsWith("autorizacion_requerida", t.Reason);
            Assert.Equal(0, m.Visits);
            Assert.Single(g.PendingRequests());
        }

        [Fact]
        public async Task R03_DefaultAuthority_DeniesWrites()
        {
            var m = new FakeModel();
            var mgr = new TaskManager(new TaskStore(TempRoot()), "A", t => new FakeBackend(m)) { Delay = (d, ct) => Task.Delay(5, ct) };
            var t = await Run(mgr, Draft("r03b", 5));
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.Equal(0, m.Visits);
        }

        [Fact]
        public async Task R03_Grant_IsBoundToTaskAndDocument()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            g.Approve("otra-tarea", DocKey, null, new[] { Capability.Modify }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            g.Approve("r03c", "archivo:c:\\otro.rvt", null, new[] { Capability.Modify }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            var m = new FakeModel();
            var mgr = Mgr(root, m, auth);
            var t = await Run(mgr, Draft("r03c", 5));
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.Contains("another document", t.Reason);
            Assert.Equal(0, m.Visits);
            g.Approve("r03c", DocKey, null, new[] { Capability.Modify }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            t = await Resume(mgr, "r03c");
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(5, m.Live.Count);
        }

        [Fact]
        public async Task R03_RevocationMidRun_StopsBeforeNextUnit()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            var grant = g.Approve("r03d", DocKey, null, new[] { Capability.Modify }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            var m = new FakeModel { MsPerUnit = 50 };
            var countAtRevoke = -1;
            m.OnVisit = v => { if (v == 2) { g.Revoke(grant.Id, DateTime.UtcNow); countAtRevoke = m.Live.Count; } };
            var mgr = Mgr(root, m, auth);
            var t = await Run(mgr, Draft("r03d", 200));
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.Contains("revoked", t.Reason);
            // the last-safe-point authority cache lasts <=2 s: at most the current visit finishes
            Assert.True(m.Live.Count < 200);
            Assert.True(m.Visits <= 3, "visits after revoking: " + m.Visits);
        }

        [Fact]
        public async Task R03_ExpiredGrant_IsNotReused()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            g.Approve("r03e", DocKey, null, new[] { Capability.Modify }, TimeSpan.FromMilliseconds(1), "persona", DateTime.UtcNow.AddMinutes(-5));
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, auth), Draft("r03e", 5));
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.Contains("expired", t.Reason);
            Assert.Equal(0, m.Visits);
        }

        [Fact]
        public void R03_ForgedOrEditedGrant_IsIgnored_AndNonGrantableIsRefused()
        {
            var root = TempRoot();
            var (g, _) = Grants(root);
            var ok = g.Approve("t", DocKey, null, new[] { Capability.Modify }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            var f = Path.Combine(g.Dir, "c_" + ok.Id + ".json");
            File.WriteAllText(f, File.ReadAllText(f).Replace("\"revit_modificar\"", "\"revit_sync\""));
            Assert.NotNull(g.Check("t", DocKey, null, Capability.Sync, DateTime.UtcNow));
            Assert.NotNull(g.Check("t", DocKey, null, Capability.Modify, DateTime.UtcNow)); // tampered -> ignored entirely
            File.WriteAllText(Path.Combine(g.Dir, "c_falsa.json"), "{\"id\":\"falsa\",\"tarea\":\"t\",\"doc\":\"" + DocKey.Replace("\\", "\\\\") + "\",\"capacidades\":[\"revit_modificar\"],\"vence\":\"2099-01-01T00:00:00Z\",\"firma\":\"00\"}");
            Assert.NotNull(g.Check("t", DocKey, null, Capability.Modify, DateTime.UtcNow));
            Assert.Throws<ArgumentException>(() => g.Approve("t", DocKey, null, new[] { "proceso" }, TimeSpan.FromHours(1), "x", DateTime.UtcNow));
        }

        [Fact]
        public async Task R03_BarrierWithoutItsOwnCapability_DoesNotRun()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            g.Approve("r03g", DocKey, null, new[] { Capability.Modify }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow); // without revit_exportar
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, auth), Draft("r03g", 10, new Dictionary<string, object> { ["barrera_en"] = 5L }));
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.Contains(Capability.Export, t.Reason);
            Assert.Equal(0, m.Barriers);
            Assert.Equal(5, m.Live.Count); // what comes before the barrier yes (authorized), the barrier and what follows no
        }

        [Fact]
        public async Task R03_ReadOnlyTask_RunsWithoutGrant_UnderDefaultDeny()
        {
            var m = new FakeModel();
            var t = await Run(Mgr(TempRoot(), m, DenyWritesAuthority.Instance), Draft("ro", 5, policy: new Dictionary<string, object> { ["requiere"] = new List<object>() }));
            Assert.Equal(TaskState.Applied, t.State);
        }

        // =========================== R04 Roslyn and DLL ===========================

        [Fact]
        public void R04_ExecBoundary_WritesNeedTrustedSession_QueriesAreRestricted()
        {
            Assert.StartsWith("codigo_confiable_requerido", CapabilityPolicy.ExecDecision(true, false, new string[0], out var p1));
            Assert.Equal(ExecutionProfile.RestrictedAutonomous, p1);
            Assert.Null(CapabilityPolicy.ExecDecision(false, false, new string[0], out _));
            Assert.NotNull(CapabilityPolicy.ExecDecision(false, false, new[] { "proceso:System.Diagnostics.Process.Start" }, out _));
            Assert.NotNull(CapabilityPolicy.ExecDecision(false, false, new[] { "archivo_escritura:System.IO.File.WriteAllText" }, out _));
            Assert.Null(CapabilityPolicy.ExecDecision(true, true, new[] { "proceso:System.Diagnostics.Process.Start" }, out var p2));
            Assert.Equal(ExecutionProfile.TrustedInteractive, p2);
        }

        [Fact]
        public void R04_ExternalReferences_DecidedBeforeLoading()
        {
            var root = TempRoot(); Directory.CreateDirectory(root);
            var addin = Path.Combine(root, "addin"); Directory.CreateDirectory(addin);
            var inside = Path.Combine(addin, "Ayuda.dll"); File.WriteAllText(inside, "x");
            var outside = Path.Combine(root, "Foreign.dll"); File.WriteAllText(outside, "malicious-content");
            var known = Path.Combine(root, "Conocida.dll"); File.WriteAllText(known, "contenido-conocido");
            Func<string, string> sha = f => Home.FileSha256(f);
            var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Home.FileSha256(known) };
            Assert.Empty(ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, new[] { inside, known }, sha, trusted, addin));
            var d = ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, new[] { outside }, sha, trusted, addin);
            Assert.Single(d);
            // traversal: a path that starts like the add-in folder but leaves it
            Assert.Single(ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, new[] { Path.Combine(addin, "..", "Foreign.dll") }, sha, trusted, addin));
            // same path, changed content -> no longer the trusted one
            File.WriteAllText(known, "contenido-cambiado");
            Assert.Single(ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, new[] { known }, sha, trusted, addin));
            Assert.Empty(ReferencePolicy.Denied(ExecutionProfile.TrustedInteractive, new[] { outside }, sha, trusted, addin));
        }

        [Theory, InlineData(2026)]
        public void R04_EvasionAttempts_AreDetected(int year)
        {
            var cases = new[]
            {
                "var t = typeof(object).Assembly.GetType(\"System.Diagnostics.Process\"); return t;",
                "var m = typeof(string).GetMethod(\"Copy\"); return m;",
                "System.Func<int> f = () => 1; return f.DynamicInvoke();",
                "var e = (System.Linq.Expressions.Expression<System.Func<int>>)(() => 1); return e.Compile();",
                "return System.AppDomain.CurrentDomain.GetAssemblies().Length;",
                "System.Threading.Tasks.Task.Run(() => 1); return 1;",
                "var p = new System.IO.Pipes.NamedPipeClientStream(\"x\"); return p;",
                "dynamic d = doc; return d.Title;",
            };
            foreach (var c in cases)
            {
                var caps = ScriptingTests.CapsOf(c, year);
                Assert.True(CapabilityPolicy.Denied(ExecutionProfile.RestrictedAutonomous, caps).Count > 0, "not detected: " + c);
            }
        }

        [Fact]
        public async Task R04_NonInstalledRecipe_EvenReadOnly_NeedsHumanApproval()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            var m = new FakeModel();
            var mgr = Mgr(root, m, auth);
            // what the add-in writes in the policy for read-only inline codigo=
            var t = await Run(mgr, Draft("r04x", 5, policy: new Dictionary<string, object> { ["requiere"] = new List<object> { Capability.ExternalRecipe } }));
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.Contains(Capability.ExternalRecipe, t.Reason);
            Assert.Equal(0, m.Visits);
            g.Approve("r04x", DocKey, null, new[] { Capability.ExternalRecipe }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            t = await Resume(mgr, "r04x");
            Assert.Equal(TaskState.Applied, t.State);
        }

        // =========================== R05 reconciliation ===========================

        [Fact]
        public async Task R05_ConfirmedUnits_CannotBeReconciledBackToPending()
        {
            var root = TempRoot();
            var m = new FakeModel { CrashAfterCommitAt = "u0003" };
            m.Unverifiable.Add("u0003");
            var mgr = Mgr(root, m, new AllowAll());
            var t = await Run(mgr, Draft("r05a", 6));
            mgr = Mgr(root, m, new AllowAll()); // "Revit crashed": another engine
            t = await Resume(mgr, "r05a");
            mgr.Get("r05a", out var units);
            Assert.Equal(UnitState.Unknown, units.Get("u0003").State);
            Assert.Equal(UnitState.Committed, units.Get("u0001").State);

            var err = mgr.Reconcile("r05a", new List<string> { "u0001" }, applied: false, note: "quiero repetirla", who: "agente");
            Assert.StartsWith("transicion_ilegal", err);
            Assert.StartsWith("evidencia_requerida", mgr.Reconcile("r05a", new List<string> { "u0003" }, applied: true, note: " ", who: "agente"));
            mgr.Get("r05a", out units);
            Assert.Equal(UnitState.Committed, units.Get("u0001").State);
        }

        [Fact]
        public async Task R05_NotAppliedDecision_NeedsHumanGrant_ThenRunsExactlyOnce()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            g.Approve("r05b", DocKey, null, new[] { Capability.Modify }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            var m = new FakeModel { CrashAfterCommitAt = "u0002" };
            m.Unverifiable.Add("u0002");
            var mgr = Mgr(root, m, auth);
            await Run(mgr, Draft("r05b", 4));
            mgr = Mgr(root, m, auth);
            var t = await Resume(mgr, "r05b");
            Assert.True(t.State == TaskState.Interrupted && t.Reason.Contains("could not be verified"), t.State + ": " + t.Reason);
            Assert.StartsWith("autorizacion_requerida", mgr.Reconcile("r05b", null, applied: false, note: "not in the model (query id 123)", who: "agente"));
            g.Approve("r05b", DocKey, null, new[] { Capability.ReconcileNotApplied }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            m.Live.Remove("u0002"); // the person checked that it was not left
            Assert.Null(mgr.Reconcile("r05b", null, applied: false, note: "not in the model (query id 123)", who: "agente"));
            m.Unverifiable.Clear();
            t = await Resume(mgr, "r05b");
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(4, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task R05_FinishedTask_IsNotReopened_AndHistoryIsKept()
        {
            var root = TempRoot();
            var m = new FakeModel();
            var mgr = Mgr(root, m, new AllowAll());
            var t = await Run(mgr, Draft("r05c", 3));
            Assert.Equal(TaskState.Applied, t.State);
            Assert.StartsWith("transicion_ilegal", mgr.Reconcile("r05c", new List<string> { "u0001" }, applied: false, note: "x", who: "agente"));
            var lines = File.ReadAllLines(Path.Combine(root, "r05c", "unidades.jsonl")).Length;
            Assert.True(lines >= 4);
        }

        [Fact]
        public void R05_HandWrittenIllegalEvent_IsIgnoredOnReplay()
        {
            var log = new UnitLog();
            log.Apply(new Dictionary<string, object> { ["ev"] = "inicio", ["claves"] = new List<object> { "a" } });
            log.Apply(new Dictionary<string, object> { ["ev"] = "fin", ["clave"] = "a", ["estado"] = UnitState.Committed, ["seq"] = 1L });
            log.Apply(new Dictionary<string, object> { ["ev"] = "reconciliada", ["clave"] = "a", ["estado"] = UnitState.Pending });
            Assert.Equal(UnitState.Committed, log.Get("a").State);
            Assert.Equal(1, log.IllegalEvents);
        }

        // =========================== R06 authority and document ===========================

        [Fact]
        public async Task R06_TwoTasksSameDocument_SecondDoesNotRun()
        {
            var root = TempRoot();
            var m = new FakeModel { MsPerUnit = 20 };
            var second = (TaskRecord)null;
            var mgrB = Mgr(root, new FakeModel(), new AllowAll(), instance: "B");
            m.OnVisit = v => { if (v == 2 && second == null) { mgrB.Start(Draft("r06b", 3), "// r", "otro"); second = mgrB.Completion("r06b").ContinueWith(_ => mgrB.Get("r06b", out var __)).Result; } };
            var mgrA = Mgr(root, m, new AllowAll(), instance: "A");
            var a = await Run(mgrA, Draft("r06a", 100));
            Assert.Equal(TaskState.Applied, a.State);
            Assert.NotNull(second);
            Assert.Contains("the document is being modified by task", second.Reason);
            // once the first finishes, the second can continue
            var b = await Resume(mgrB, "r06b");
            Assert.Equal(TaskState.Applied, b.State);
        }

        [Fact]
        public async Task R06_DifferentDocuments_RunInParallel()
        {
            var root = TempRoot();
            var m1 = new FakeModel(); var m2 = new FakeModel();
            var t1 = Run(Mgr(root, m1, new AllowAll(), instance: "A"), Draft("d1", 50));
            var t2 = Run(Mgr(root, m2, new AllowAll(), instance: "B"), Draft("d2", 50, docKey: "archivo:c:\\otro.rvt"));
            Assert.Equal(TaskState.Applied, (await t1).State);
            Assert.Equal(TaskState.Applied, (await t2).State);
        }

        [Fact]
        public async Task R06_LeaseStolenDuringRevitWait_NoUnitStartsAfterwards()
        {
            var root = TempRoot();
            var m = new FakeModel { MsPerUnit = 10 };
            var thief = new TaskStore(root);
            m.OnVisit = v =>
            {
                // the visit already entered the "Revit thread" (batch validated), but before the first transaction another process takes the task
                if (v == 2) Assert.True(thief.TryLease("r06c", "OTRA", "B", TimeSpan.FromHours(3), DateTime.UtcNow.AddHours(2), out _));
            };
            var t = await Run(Mgr(root, m, new AllowAll()), Draft("r06c", 200));
            // The task belongs to B (valid lease): the old runner does not write its "interrumpida" over B's tarea.json.
            Assert.Equal(TaskState.Running, t.State);
            Assert.Contains("lease_perdido", m.PreflightRefusals);
            var afterSteal = m.Live.Count;
            Assert.True(afterSteal > 0 && afterSteal < 200);
            Assert.Equal(0, m.Duplicates);
        }

        // =========================== R07 checkpoints ===========================

        [Fact]
        public async Task R07_CheckpointFailure_StopsTask_ResumeIsConsistent()
        {
            var root = TempRoot();
            var m = new FakeModel { MsPerUnit = 20 };
            var store = new TaskStore(root);
            var saves = 0;
            var failing = true;
            store.FaultBeforeSave = id => { if (failing && ++saves == 6) throw new IOException("out of space (simulated)"); };
            var mgr = new TaskManager(store, "A", t => new FakeBackend(m)) { Delay = (d, ct) => Task.Delay(5, ct), Authority = new AllowAll(), BudgetMs = 200 };
            mgr.Start(Draft("r07", 300), "// r", "p");
            await mgr.Completion("r07");
            var mem = mgr.Get("r07", out _);
            Assert.NotEqual(TaskState.Applied, mem.State);
            Assert.True(m.Live.Count < 300, "the engine did not continue after a failed checkpoint");
            var executed = m.Live.Count;
            failing = false;
            var mgr2 = new TaskManager(new TaskStore(root), "B", t => new FakeBackend(m)) { Delay = (d, ct) => Task.Delay(5, ct), Authority = new AllowAll(), BudgetMs = 200 };
            var t2 = await Resume(mgr2, "r07");
            Assert.Equal(TaskState.Applied, t2.State);
            Assert.Equal(300, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
        }
    }
}
