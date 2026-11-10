// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Tasks;
using PDI.RevitMcp.Scripting;
using Xunit;
using FakeModel = PDI.RevitMcp.Tests.TaskEngineTests.FakeModel;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// rc3: pruebas de los hallazgos de la revisión crítica de 0.6.1-rc2 (commit 0222b6e). Cada prueba intenta el camino defectuoso.
    /// Las marcadas "REPRO" fallan sobre el código de rc2 (ver docs/0.6.1/evidencia/repro_rc2.log).
    /// </summary>
    public class Rc3Tests
    {
        private const string DocKey = "archivo:c:\\practica.rvt";
        private static string TempRoot() { var r = Path.Combine(Path.GetTempPath(), "pdi-rc3-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(r); return r; }
        private static readonly DateTime Now = DateTime.UtcNow;

        // =========================== P0-1 clave HMAC ===========================

        [Fact] // REPRO
        public void Key_GrantSurvivesRebuildOfStore()
        {
            var dir = TempRoot();
            var s1 = new GrantStore(dir);
            s1.Approve("t", DocKey, "P", new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
            var s2 = new GrantStore(dir); // reinicio de Revit
            Assert.Null(s2.Check("t", DocKey, "P", Capability.Modificar, Now));
            Assert.Single(s2.All());
        }

        [Fact] // REPRO
        public void Key_TwoIndependentInstances_ShareTheSameKey()
        {
            var dir = TempRoot();
            var a = new GrantStore(dir);
            var b = new GrantStore(dir);
            a.Approve("t", DocKey, "P", new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
            b.Approve("t2", DocKey, "P", new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
            Assert.Null(b.Check("t", DocKey, "P", Capability.Modificar, Now));
            Assert.Null(a.Check("t2", DocKey, "P", Capability.Modificar, Now));
        }

        [Fact] // REPRO
        public void Key_RevocationAfterRestart_IsEffective()
        {
            var dir = TempRoot();
            var g = new GrantStore(dir).Approve("t", DocKey, "P", new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
            Assert.True(new GrantStore(dir).Revoke(g.Id, Now));
            var s3 = new GrantStore(dir);
            Assert.Contains("revocada", s3.Check("t", DocKey, "P", Capability.Modificar, Now.AddSeconds(1)));
        }

        [Fact]
        public void Key_Rc2Format_IsStillAccepted()
        {
            var dir = TempRoot();
            var s1 = new GrantStore(dir);
            s1.Approve("t", DocKey, "P", new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
            // rc2 escribía sólo el Base64 de la clave, sin cabecera: se reescribe así con la misma clave.
            var path = Path.Combine(dir, "clave.bin");
            var b64 = File.ReadAllLines(path).Last(l => l.Trim().Length > 0 && l != GrantStore.KeyHeader).Trim();
            File.WriteAllText(path, b64);
            Assert.Null(new GrantStore(dir).Check("t", DocKey, "P", Capability.Modificar, Now));
        }

        [Fact]
        public void Key_FileIsLabelledAsEncoding_NotEncryption()
        {
            var dir = TempRoot();
            new GrantStore(dir);
            var text = File.ReadAllText(Path.Combine(dir, "clave.bin"));
            Assert.StartsWith(GrantStore.KeyHeader, text);
            Assert.Equal(GrantStore.KeyBytes, Convert.FromBase64String(text.Split('\n')[1].Trim()).Length);
        }

        [Theory]
        [InlineData("basura que no es base64 !!")]
        [InlineData("AAAA")]                        // base64 válido pero 3 bytes
        [InlineData("PDI-GRANT-KEY v1 base64\n")]   // truncada tras la cabecera
        [InlineData("")]
        public void Key_CorruptOrTruncated_IsRefused_NotRegenerated(string content)
        {
            var dir = TempRoot();
            var path = Path.Combine(dir, "clave.bin");
            File.WriteAllText(path, content);
            Assert.Throws<GrantStore.GrantKeyException>(() => new GrantStore(dir));
            Assert.Equal(content, File.ReadAllText(path)); // intacta: la decide una persona
        }

        [Fact]
        public void Key_MissingWithExistingGrants_IsRefused_NoNewKey()
        {
            var dir = TempRoot();
            new GrantStore(dir).Approve("t", DocKey, "P", new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
            File.Delete(Path.Combine(dir, "clave.bin"));
            var ex = Assert.Throws<GrantStore.GrantKeyException>(() => new GrantStore(dir));
            Assert.Contains("concesiones firmadas", ex.Message);
            Assert.False(File.Exists(Path.Combine(dir, "clave.bin")));
        }

        [Fact]
        public void Key_ConcurrentInitialisation_ProducesOneKey()
        {
            for (var round = 0; round < 5; round++)
            {
                var dir = TempRoot();
                var stores = new GrantStore[16];
                // Issue #21: una excepción en un Thread suelto abortaba el proceso de pruebas entero. Se captura y se informa como fallo.
                var errors = new Exception[stores.Length];
                using (var go = new ManualResetEventSlim(false))
                {
                    var threads = Enumerable.Range(0, stores.Length).Select(i => new Thread(() =>
                    {
                        go.Wait();
                        try { stores[i] = new GrantStore(dir); } catch (Exception ex) { errors[i] = ex; }
                    })).ToList();
                    threads.ForEach(t => t.Start());
                    go.Set();
                    threads.ForEach(t => t.Join());
                }
                Assert.True(errors.All(e => e == null), "ronda " + round + ": " + string.Join(" | ", errors.Where(e => e != null).Select(e => e.GetType().Name + ": " + e.Message)));
                stores[0].Approve("t", DocKey, "P", new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
                Assert.All(stores, s => Assert.Null(s.Check("t", DocKey, "P", Capability.Modificar, Now)));
                Assert.Empty(Directory.GetFiles(dir, "clave.bin.tmp-*"));
            }
        }

        /// <summary>Issue #21, reproducción determinista: otro handle tiene clave.bin abierto SIN compartir lectura (como un escáner
        /// recién creada la clave). Antes: GrantKeyException inmediata. Ahora: espera acotada y lee la MISMA clave.</summary>
        [Fact]
        public void Key_TransientSharingViolation_WaitsAndReadsSameKey()
        {
            var dir = TempRoot();
            var first = new GrantStore(dir);
            first.Approve("t", DocKey, "P", new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
            var keyPath = Path.Combine(dir, "clave.bin");
            using (var held = new ManualResetEventSlim(false))
            {
                var holder = new Thread(() =>
                {
                    using (new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None)) { held.Set(); Thread.Sleep(400); }
                });
                holder.Start();
                held.Wait();
                var again = new GrantStore(dir);   // se abre mientras el otro handle no comparte
                holder.Join();
                Assert.Null(again.Check("t", DocKey, "P", Capability.Modificar, Now));
            }
        }

        /// <summary>Si la violación de compartición persiste más allá de la ventana, falla cerrado y controlado (sin abortar, sin regenerar,
        /// sin el secreto en el mensaje).</summary>
        [Fact]
        public void Key_PersistentSharingViolation_FailsClosedWithoutSecret()
        {
            var dir = TempRoot();
            new GrantStore(dir);
            var keyPath = Path.Combine(dir, "clave.bin");
            var secret = File.ReadAllText(keyPath);
            var saved = GrantStore.ReadRetryWindow;
            GrantStore.ReadRetryWindow = TimeSpan.FromMilliseconds(150);
            try
            {
                using (new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var ex = Assert.Throws<GrantStore.GrantKeyException>(() => new GrantStore(dir));
                    var b64 = secret.Replace("\r", "").Split('\n').Where(l => l.Length > 0).Last();
                    Assert.DoesNotContain(b64, ex.Message);
                }
                Assert.Equal(secret, File.ReadAllText(keyPath));   // no se regeneró
            }
            finally { GrantStore.ReadRetryWindow = saved; }
        }

        [Fact]
        public void Key_AccessDenied_IsNotRetriedAndFailsClosed()
        {
            // Un error que no es de compartición (aquí: la ruta es un directorio) no entra en la espera.
            var dir = TempRoot();
            Directory.CreateDirectory(Path.Combine(dir, "clave.bin"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<GrantStore.GrantKeyException>(() => new GrantStore(dir));
            Assert.True(sw.ElapsedMilliseconds < 2000);
        }

        [Fact]
        public void Key_ConcurrentInitialisation_AcrossProcesses_ProducesOneKey()
        {
            var dir = TempRoot();
            var procs = Enumerable.Range(0, 6).Select(_ => LeaseFencingTests.StartProbe(new[] { dir, "1", "x", "clave", DateTime.UtcNow.AddMilliseconds(2500).Ticks.ToString() })).ToList();
            foreach (var p in procs) { Assert.True(p.WaitForExit(30000)); Assert.Equal(0, p.ExitCode); }
            var tasks = procs.Select(p => p.StandardOutput.ReadToEnd().Trim()).Select(l => l.Substring("GRANT ".Length)).ToList();
            // Todas las concesiones firmadas por procesos distintos validan con la clave única que quedó en disco.
            var s = new GrantStore(dir);
            Assert.Equal(6, s.All().Count);
            Assert.All(tasks, t => Assert.Null(s.Check(t, "doc", "P", Capability.Modificar, DateTime.UtcNow)));
        }

        // =========================== P1-5 huella documental ===========================

        [Fact] // REPRO (huella ausente)
        public void Print_MissingCurrentPrint_DoesNotWidenAGrantBoundToAPrint()
        {
            var g = new GrantStore(TempRoot());
            g.Approve("t", DocKey, "P1", new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
            Assert.Contains("huella", g.Check("t", DocKey, null, Capability.Modificar, Now));
            Assert.Contains("otra identidad", g.Check("t", DocKey, "P2", Capability.Modificar, Now)); // reemplazado en la misma ruta
            Assert.Null(g.Check("t", DocKey, "P1", Capability.Modificar, Now));
        }

        [Fact] // REPRO (concesión sin huella)
        public void Print_GrantWithoutPrint_DoesNotCoverADocumentThatHasOne()
        {
            var g = new GrantStore(TempRoot());
            g.Approve("t", DocKey, null, new[] { Capability.Modificar }, TimeSpan.FromHours(1), "operator", Now);
            Assert.NotNull(g.Check("t", DocKey, "P1", Capability.Modificar, Now));
            Assert.Null(g.Check("t", DocKey, null, Capability.Modificar, Now)); // misma identidad (ninguna) que al aprobar
        }

        // =========================== P0-2 frontera de recetas ===========================

        /// <summary>Receta simulada: cada fase deja rastro en <see cref="Events"/> y puede "escribir" en el modelo (WriteRev).</summary>
        public class FakeRecipe
        {
            public static List<string> Events;
            public static FakeModel Model;
            public FakeRecipe() { Events.Add("constructor"); Ctor(); }
            protected virtual void Ctor() { }
            public virtual void Preparar() { Events.Add("preparar"); }
            public virtual IEnumerable<string> Unidades(int n) { for (var i = 1; i <= n; i++) { Events.Add("movenext"); yield return "u" + i.ToString("D4"); } }
            public void Ejecutar(string key) { Events.Add("ejecutar"); lock (Model.Lock) { Model.Live[key] = "x"; WriteRev++; } }
            public static long WriteRev;
        }
        public sealed class CtorWrites : FakeRecipe { protected override void Ctor() { WriteRev++; Events.Add("efecto_constructor"); } }
        public sealed class PrepareWrites : FakeRecipe { public override void Preparar() { base.Preparar(); WriteRev++; } }
        public sealed class MoveNextWrites : FakeRecipe
        {
            public override IEnumerable<string> Unidades(int n) { Events.Add("movenext"); WriteRev++; yield return "u0001"; }
        }

        /// <summary>Backend que reproduce el flujo del add-in (RevitTaskBackend): compilar (aquí: nada) → instanciar, Preparar, enumerar y
        /// ejecutar, cada fase a través de <see cref="RecipeGate"/>.</summary>
        private sealed class GatedBackend : ITaskBackend
        {
            private readonly FakeModel _m;
            private readonly Func<FakeRecipe> _factory;
            private FakeRecipe _r;
            private TaskRecord _task;
            private readonly RecipeGate _gate;
            public GatedBackend(FakeModel m, Func<FakeRecipe> factory)
            {
                _m = m; _factory = factory;
                _gate = new RecipeGate(() => _task?.Preflight != null ? _task.Preflight() : "la tarea no está en ejecución", () => FakeRecipe.WriteRev);
            }
            public BackendResult<bool> CheckContext(TaskRecord task) => BackendResult<bool>.Ok(true);
            public string CurrentGeneration(TaskRecord task) => "inst#1";

            public Task<BackendResult<List<UnitRef>>> EnumerateAsync(TaskRecord task, CancellationToken ct)
            {
                _task = task;
                var why = _r == null ? _gate.Create(_factory, out _r) : null;
                if (why != null) return Task.FromResult(BackendResult<List<UnitRef>>.Fail(why));
                why = _gate.Run(RecipeGate.Phase.Preparar, () => _r.Preparar());
                if (why != null) return Task.FromResult(BackendResult<List<UnitRef>>.Fail(why));
                IEnumerator<string> e = null;
                why = _gate.Run(RecipeGate.Phase.Enumerar, () => e = _r.Unidades((int)(PDI.RevitMcp.Protocol.Arg.GetLong(task.Args, "n") ?? 3)).GetEnumerator());
                var list = new List<UnitRef>();
                while (why == null)
                {
                    var more = false;
                    why = _gate.Run(RecipeGate.Phase.Enumerar, () => more = e.MoveNext());
                    if (why != null || !more) break;
                    list.Add(new UnitRef { Key = e.Current });
                }
                if (why != null) return Task.FromResult(BackendResult<List<UnitRef>>.Fail(why));
                return Task.FromResult(BackendResult<List<UnitRef>>.Ok(list));
            }

            public Task<BackendResult<List<UnitOutcome>>> RunBatchAsync(TaskRecord task, IReadOnlyList<UnitRef> units, int budgetMs, CancellationToken ct)
            {
                var outs = new List<UnitOutcome>();
                foreach (var u in units)
                {
                    var why = _gate.Run(RecipeGate.Phase.Ejecutar, () => _r.Ejecutar(u.Key));
                    if (why != null) break;
                    var o = new UnitOutcome { Key = u.Key, State = UnitState.Confirmada, Changed = 1 };
                    o.Created.Add(new object[] { 1L, "uid-" + u.Key });
                    outs.Add(o);
                }
                return Task.FromResult(BackendResult<List<UnitOutcome>>.Ok(outs));
            }

            public Task<BackendResult<UnitOutcome>> RunBarrierAsync(TaskRecord task, UnitRef barrier, CancellationToken ct) =>
                Task.FromResult(BackendResult<UnitOutcome>.Ok(new UnitOutcome { Key = barrier.Key, State = UnitState.Confirmada }));
            public Task<BackendResult<List<UnitOutcome>>> VerifyAsync(TaskRecord task, IReadOnlyList<UnitRecord> units, bool reopened, CancellationToken ct) =>
                Task.FromResult(BackendResult<List<UnitOutcome>>.Ok(units.Select(u => new UnitOutcome { Key = u.Key, State = UnitState.Confirmada }).ToList()));
            public Task<BackendResult<Dictionary<string, object>>> PostconditionAsync(TaskRecord task, CancellationToken ct) =>
                Task.FromResult(BackendResult<Dictionary<string, object>>.Ok(new Dictionary<string, object>()));
        }

        /// <summary>Autoridad que registra cada consulta en el mismo diario que la receta (para comprobar el orden) y se puede revocar.</summary>
        private sealed class RecordingAuthority : ITaskAuthority
        {
            public int AllowFirst = int.MaxValue;
            public int Calls;
            public string Check(TaskRecord t, string c, DateTime n)
            {
                lock (FakeRecipe.Events) FakeRecipe.Events.Add("autoridad");
                return ++Calls <= AllowFirst ? null : "revocada por la persona";
            }
        }

        private static readonly object RecipeLock = new object();

        private static async Task<TaskRecord> RunRecipe(Func<FakeRecipe> factory, ITaskAuthority auth, int n = 3)
        {
            var root = TempRoot();
            var m = new FakeModel();
            FakeRecipe.Model = m;
            var mgr = new TaskManager(new TaskStore(root), "A", t => new GatedBackend(m, factory))
            { Delay = (d, ct) => Task.Delay(5, ct), MaxWait = TimeSpan.FromSeconds(10), BudgetMs = 200, Authority = auth };
            var draft = new TaskRecord
            {
                Id = "r" + Guid.NewGuid().ToString("N").Substring(0, 6), Objective = "x", RecipeName = "fake", RecipeVersion = "1", RecipeHash = "h", ContentHash = Guid.NewGuid().ToString("N"),
                Args = new Dictionary<string, object> { ["n"] = (long)n },
                Docs = new List<TaskDoc> { new TaskDoc { Key = DocKey, Title = "p", Generation = "inst#1" } },
                Policy = new Dictionary<string, object> { ["requiere"] = new List<object> { Capability.Modificar } },
            };
            mgr.Start(draft, "// receta", "p");
            await mgr.Completion(draft.Id);
            return mgr.Get(draft.Id, out _);
        }

        [Fact] // REPRO (en rc2 el constructor corría en `tarea iniciar` y en EnsureRecipe, antes de autorizar)
        public async Task Recipe_WithoutGrant_ConstructorNeverRuns()
        {
            lock (RecipeLock) { FakeRecipe.Events = new List<string>(); FakeRecipe.WriteRev = 0; }
            var t = await RunRecipe(() => new CtorWrites(), DenyWritesAuthority.Instance);
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Contains("autorizacion_requerida", t.Reason);
            Assert.DoesNotContain("constructor", FakeRecipe.Events);
            Assert.Equal(0, FakeRecipe.WriteRev);
        }

        [Fact]
        public async Task Recipe_RevokedBeforeEnumerate_NothingOfTheRecipeRuns()
        {
            lock (RecipeLock) { FakeRecipe.Events = new List<string>(); FakeRecipe.WriteRev = 0; }
            var auth = new RecordingAuthority { AllowFirst = 1 }; // pasa la comprobación inicial del motor; se revoca justo después
            var t = await RunRecipe(() => new FakeRecipe(), auth);
            Assert.NotEqual(TaskState.Aplicada, t.State);
            Assert.Contains("autorizacion_requerida", t.Reason);
            Assert.DoesNotContain("constructor", FakeRecipe.Events);
            Assert.DoesNotContain("preparar", FakeRecipe.Events);
        }

        [Fact]
        public async Task Recipe_ConstructorWithSideEffect_IsStoppedAsReadPhaseWrite()
        {
            lock (RecipeLock) { FakeRecipe.Events = new List<string>(); FakeRecipe.WriteRev = 0; }
            var t = await RunRecipe(() => new CtorWrites(), new RecordingAuthority());
            Assert.Equal(TaskState.Parcial, t.State);
            Assert.Contains("escritura_en_fase_de_lectura", t.Reason);
            Assert.Contains("constructor", t.Reason);
            Assert.DoesNotContain("preparar", FakeRecipe.Events);
            Assert.DoesNotContain("ejecutar", FakeRecipe.Events);
        }

        [Fact]
        public async Task Recipe_PrepareThatWrites_IsStopped_NoUnitRuns()
        {
            lock (RecipeLock) { FakeRecipe.Events = new List<string>(); FakeRecipe.WriteRev = 0; }
            var t = await RunRecipe(() => new PrepareWrites(), new RecordingAuthority());
            Assert.Equal(TaskState.Parcial, t.State);
            Assert.Contains("'preparar'", t.Reason);
            Assert.DoesNotContain("ejecutar", FakeRecipe.Events);
        }

        [Fact]
        public async Task Recipe_EnumeratorSideEffectInMoveNext_IsStopped()
        {
            lock (RecipeLock) { FakeRecipe.Events = new List<string>(); FakeRecipe.WriteRev = 0; }
            var t = await RunRecipe(() => new MoveNextWrites(), new RecordingAuthority());
            Assert.Equal(TaskState.Parcial, t.State);
            Assert.Contains("'enumerar'", t.Reason);
            Assert.DoesNotContain("ejecutar", FakeRecipe.Events);
        }

        [Fact]
        public async Task Recipe_Authorized_RunsNormally_AndAuthorityPrecedesEveryRecipeCall()
        {
            lock (RecipeLock) { FakeRecipe.Events = new List<string>(); FakeRecipe.WriteRev = 0; }
            var t = await RunRecipe(() => new FakeRecipe(), new RecordingAuthority(), n: 4);
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(4, FakeRecipe.Model.Live.Count);
            var ev = FakeRecipe.Events;
            Assert.Equal("autoridad", ev[0]);
            Assert.True(ev.IndexOf("autoridad") < ev.IndexOf("constructor"));
            Assert.Equal(1, ev.Count(e => e == "constructor"));
        }

        [Fact]
        public void Gate_WithoutRunningTask_RefusesEverything()
        {
            var ran = false;
            var gate = new RecipeGate(null, () => 0);
            Assert.NotNull(gate.Run(RecipeGate.Phase.Constructor, () => ran = true));
            Assert.False(ran);
        }

        // ---- analizador y metadatos (lo que `tarea iniciar` lee sin instanciar) ----

        [Fact]
        public void Analyzer_OwnTransactionsAndUiCommands_AreAlwaysDeniedForAutonomousCode()
        {
            var caps = ScriptingTests.CapsOf("using (var t = new Transaction(doc, \"x\")) { t.Start(); t.Commit(); } return 1;", 2026);
            Assert.Contains(caps, c => c.StartsWith("revit_transaccion"));
            Assert.NotEmpty(CapabilityPolicy.Denied(ExecutionProfile.RestrictedAutonomous, caps, Capability.Grantable));
            var caps2 = ScriptingTests.CapsOf("var g = new TransactionGroup(doc); var s = new SubTransaction(doc); return 1;", 2026);
            Assert.True(caps2.Count(c => c.StartsWith("revit_transaccion")) >= 2);
        }

        [Fact]
        public void Metadata_IsReadFromSource_ConservativeWhenNotConstant()
        {
            Assert.Equal("Lectura", RecipeMetadata.Constant("class R : Receta { public override PoliticaTx Transaccion => PoliticaTx.Lectura; }", "Transaccion"));
            Assert.Equal("3", RecipeMetadata.Constant("class R : Receta { public override string Version { get { return \"3\"; } } }", "Version"));
            // comentado o calculado → null (el llamador asume que escribe)
            Assert.Null(RecipeMetadata.Constant("class R : Receta { // public override PoliticaTx Transaccion => PoliticaTx.Lectura;\n }", "Transaccion"));
            Assert.Null(RecipeMetadata.Constant("class R : Receta { public override PoliticaTx Transaccion => X ? PoliticaTx.Lectura : PoliticaTx.PorUnidad; }", "Transaccion"));
            Assert.Null(RecipeMetadata.Constant("class Otra { public PoliticaTx Transaccion => PoliticaTx.Lectura; }", "Transaccion"));
        }

        [Fact]
        public void InstalledRecipes_Regression_CompileWithoutDeniedCapabilities_AndMetadataMatches()
        {
            var dir = Path.Combine(ScriptingTests.RepoRoot, "src", "PDI.RevitMcp.Addin", "recetas");
            var files = Directory.GetFiles(dir, "*.cs").OrderBy(f => f).ToList();
            Assert.Equal(5, files.Count);
            var expectRead = new HashSet<string> { "exportar_laminas", "montaje_soportes", "qc_manifiesto" };
            foreach (var f in files)
            {
                var code = File.ReadAllText(f);
                var name = Path.GetFileNameWithoutExtension(f);
                foreach (var year in new[] { 2022, 2026 })
                {
                    var caps = ScriptingTests.CapsOf(code, year);
                    var denied = CapabilityPolicy.Denied(ExecutionProfile.RestrictedAutonomous, caps, Capability.Grantable);
                    Assert.True(denied.Count == 0, name + " " + year + ": " + string.Join(", ", denied));
                }
                Assert.Equal(expectRead.Contains(name) ? "Lectura" : null, RecipeMetadata.Constant(code, "Transaccion"));
            }
        }

        // =========================== P1-3 integridad de AuditLog ===========================

        private static string EditLine(string path, int lineIndex, Func<string, string> edit)
        {
            var lines = File.ReadAllText(path).Split('\n').ToList();
            lines[lineIndex] = edit(lines[lineIndex]);
            var t = string.Join("\n", lines);
            File.WriteAllText(path, t);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            return t;
        }

        private static string SameSizeEdit(string line) => line.Replace("\"ok\"", "\"KO\"");

        [Fact] // REPRO
        public void Audit_LastRecordEdited_SameSize_IsDetected()
        {
            var p = Path.Combine(TempRoot(), "a.jsonl");
            var a = new AuditLog(p);
            for (var i = 0; i < 5; i++) a.Append("s", "op", "ok");
            var before = new FileInfo(p).Length;
            EditLine(p, 4, SameSizeEdit);
            Assert.Equal(before, new FileInfo(p).Length);
            Assert.False(AuditLog.Verify(p).Ok);                // la cabeza lo detecta
            a.Append("s", "op", "ok");                           // la instancia viva también (relectura del último registro)
            Assert.Single(a.Quarantined);
        }

        [Fact] // REPRO
        public void Audit_MiddleRecordEdited_SameSize_IsDetectedByLiveInstance()
        {
            var p = Path.Combine(TempRoot(), "a.jsonl");
            var a = new AuditLog(p);
            for (var i = 0; i < 5; i++) a.Append("s", "op", "ok");
            EditLine(p, 2, SameSizeEdit);
            a.Append("s", "op", "ok");
            Assert.Single(a.Quarantined);
            Assert.True(AuditLog.Verify(p).Ok);
        }

        [Fact] // REPRO
        public void Audit_LastRecordRemoved_IsDetected()
        {
            var p = Path.Combine(TempRoot(), "a.jsonl");
            var a = new AuditLog(p);
            for (var i = 0; i < 4; i++) a.Append("s", "op", "ok");
            var lines = File.ReadAllLines(p);
            File.WriteAllText(p, string.Join("\n", lines.Take(3)) + "\n");
            var v = AuditLog.Verify(p);
            Assert.False(v.Ok);
            Assert.Contains(v.Errors, e => e.Contains("faltan registros"));
        }

        [Fact]
        public void Audit_HeadOneBehind_AfterCrash_IsTolerated()
        {
            var p = Path.Combine(TempRoot(), "a.jsonl");
            var a = new AuditLog(p);
            a.Append("s", "op", "ok");
            var head = File.ReadAllText(AuditLog.HeadPath(p));
            a.Append("s", "op", "ok");
            File.WriteAllText(AuditLog.HeadPath(p), head); // caída entre escribir la línea y la cabeza
            Assert.True(AuditLog.Verify(p).Ok);
        }

        [Fact] // REPRO
        public void Audit_Quarantine_KeepsPendingIntentsVisible()
        {
            var p = Path.Combine(TempRoot(), "a.jsonl");
            var a = new AuditLog(p);
            var id = a.Intent("s", "aplicar_cola", "doc");
            a.Append("s", "otro", "ok");
            a.Append("s", "otro", "ok");
            EditLine(p, 1, SameSizeEdit); // corrompe la cadena
            var b = new AuditLog(p);    // al abrir: cuarentena
            Assert.Single(b.Quarantined);
            var pending = AuditLog.PendingIntents(p);
            Assert.Contains(pending, d => (string)d["intencion"] == id);
            // y un resultado posterior la cierra
            b.Outcome("s", "aplicar_cola", id, "reconciliada");
            Assert.DoesNotContain(AuditLog.PendingIntents(p), d => (string)d["intencion"] == id);
        }

        [Fact]
        public void Audit_ManyAppends_StillCheap()
        {
            var p = Path.Combine(TempRoot(), "a.jsonl");
            var a = new AuditLog(p);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 300; i++) a.Append("s", "op", "ok");
            Assert.True(sw.ElapsedMilliseconds < 15000, sw.ElapsedMilliseconds + " ms");
            Assert.True(AuditLog.Verify(p).Ok);
        }

        // =========================== P1-4 resultado tras fallo de auditoría ===========================

        [Fact] // REPRO
        public void AuditGate_OutcomeFailsAfterCommit_ResultIsMarkedIncomplete_IntentStaysPending()
        {
            var p = Path.Combine(TempRoot(), "a.jsonl");
            var a = new AuditLog(p);
            var n = 0;
            a.FaultBeforeWrite = _ => { if (++n == 2) throw new IOException("disco lleno"); };
            var applied = 0;
            var r = AuditGate.Apply(a, "s", "aplicar_cola", "doc", null,
                () => { applied++; return new Dictionary<string, object> { ["estado"] = "aplicado" }; },
                x => (string)x["estado"], why => new Dictionary<string, object> { ["estado"] = "rechazado", ["motivo"] = why },
                (x, why) => { x["auditoria"] = AuditGate.Incompleta; x["motivo"] = why; return x; });
            Assert.Equal(1, applied);
            Assert.Equal("aplicado", r["estado"]);                // no se finge reversión
            Assert.Equal(AuditGate.Incompleta, r["auditoria"]);   // pero no se presenta como verificado
            Assert.Contains("pendiente de reconciliar", (string)r["motivo"]);
            Assert.Single(AuditLog.PendingIntents(p));
        }
    }
}
