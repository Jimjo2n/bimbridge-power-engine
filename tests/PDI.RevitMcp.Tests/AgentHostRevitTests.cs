// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.AgentHost;
using PDI.RevitMcp.AgentHost.Bench;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// alpha3: AgentHost → RevitLink (conexión del puente) → pipe → add-in FALSO. Prueba la lógica del adaptador real (identidad, alcance,
    /// autorización del operador, ensayo, incertidumbre). NO es validación nativa: la nativa es NRT-04 en Revit.
    /// </summary>
    public class AgentHostRevitTests
    {
        private const string Key = "archivo:c:\\nrt\\copia.rvt", Print = "huella-copia-1";

        private sealed class FakeBim : IRequestHandler
        {
            public readonly Dictionary<long, string> Comments = new Dictionary<long, string> { [101] = null, [102] = "x", [103] = null, [104] = null, [900] = null };
            public bool Workshared, LeakTrial, ExtraIdInPlan, Modified;
            public int Pid = 4242;
            public string Levels = "RPW";
            public int CommitDelayMs;
            /// <summary>beta1 NRT-05: archivo en disco que representa el .rvt (guardar le añade bytes; verificar_disco lee DiskComments).</summary>
            public string DiskFile;
            public Dictionary<long, string> DiskComments = new Dictionary<long, string>();
            public bool SaveDoesNothing;
            // rc1
            public int ForcePage;                 // >0: el "add-in" pagina la consulta con este tamaño
            public bool TotalChangesOnPage2;      // simula un modelo que cambia entre páginas
            public string OmitContextFlag;        // flag del context que el "add-in" no informa
            public bool CommitNoop;               // commit responde ok pero no aplica (falso éxito)
            public long[] QcCounts = { 0, 0, 0 }; // sin_tag, huérfanos, duplicados sobrantes
            public int QueryCalls, CheckCalls;
            public int BusyReads;                 // las próximas N lecturas responden revit_ocupado (Revit en reposo lento)
            public void Seed(int n) { lock (Comments) for (var i = 0; i < n; i++) Comments[1000 + i] = null; }
            public int Saves;
            public int Commits, TrialCommits;
            private readonly Dictionary<string, List<(long Id, string Value)>> _plans = new Dictionary<string, List<(long Id, string Value)>>();
            public void OnSessionClosed(Session session) { }
            private int _n;

            public Dictionary<string, object> HelloInfo() => new Dictionary<string, object> { ["revit"] = "2024" };
            public void SessionClosed(Session s) { }

            public async Task<Response> HandleAsync(Session s, Request r, CancellationToken ct)
            {
                if ((r.Op == Wire.OpQuery || r.Op == Wire.OpCheck) && Interlocked.Decrement(ref BusyReads) >= 0)
                    return Response.Fail(r.Id, Status.RevitOcupado, "simulado: Revit no atendió");
                switch (r.Op)
                {
                    case Wire.OpContext:
                    {
                        var cx = new Dictionary<string, object>
                        {
                            ["pid"] = (long)Pid, ["doc"] = "d1", ["documento"] = "copia", ["ruta"] = "C:\\nrt\\copia.rvt", ["clave_doc"] = Key, ["huella_doc"] = Print,
                            ["worksharing"] = Workshared, ["solo_lectura"] = false, ["modificado"] = Modified, ["niveles_mcp"] = Levels, ["retener"] = false,
                        };
                        if (OmitContextFlag != null) cx.Remove(OmitContextFlag);
                        return new Response { Id = r.Id, Rev = 7, Datos = cx };
                    }
                    case Wire.OpCheck:
                        CheckCalls++;
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object>
                        {
                            ["luminarias"] = 10L, ["sin_tag"] = new Dictionary<string, object> { ["n"] = QcCounts[0] },
                            ["tags_huerfanos"] = new Dictionary<string, object> { ["n"] = QcCounts[1] },
                            ["tags_duplicados"] = new Dictionary<string, object> { ["n"] = QcCounts[2] > 0 ? 1L : 0L, ["sobrantes"] = QcCounts[2] },
                        } };
                    case Wire.OpQuery:
                    {
                        lock (Comments)
                        {
                            var ps = Arg.GetLong(r.Args, "page_size") ?? 50;
                            if (ps < 1 || ps > 200) return new Response { Id = r.Id, Estado = Status.Invalido, Aviso = "page_size entre 1 y 200" };   // igual que CatalogRunner
                            var ids = Arg.GetStringList(r.Args, "ids").Select(long.Parse).ToList();
                            var sel = (ids.Count > 0 ? ids.Where(Comments.ContainsKey) : Comments.Keys.Where(k => k < 900)).ToList();
                            if (Arg.GetBool(r.Args, "count") == true) return new Response { Id = r.Id, Datos = new Dictionary<string, object> { ["total"] = (long)sel.Count } };
                            QueryCalls++;
                            var f = Arg.GetStringList(r.Args, "f");
                            var rows = sel.Select(i => (object)f.Select(x => x == "id" ? (object)i : x == "Comments" ? Comments[i] : (object)("v_" + x)).ToList()).ToList();
                            var dq = new Dictionary<string, object> { ["total"] = (long)sel.Count, ["cols"] = f.Cast<object>().ToList(), ["rows"] = rows };
                            if (ForcePage > 0)
                            {
                                var pg = (int)(Arg.GetLong(r.Args, "page") ?? 1);
                                dq["rows"] = rows.Skip((pg - 1) * ForcePage).Take(ForcePage).ToList();
                                dq["page"] = (long)pg; dq["pages"] = (long)((sel.Count + ForcePage - 1) / ForcePage);
                                if (TotalChangesOnPage2 && pg == 2) dq["total"] = (long)sel.Count + 1;
                            }
                            return new Response { Id = r.Id, Datos = dq };
                        }
                    }
                    case Wire.OpPlan:
                    {
                        var changes = new List<(long Id, string Value)>();
                        foreach (Dictionary<string, object> op in (List<object>)r.Args["ops"])
                        {
                            var value = (string)op["value"];
                            lock (Comments) changes.AddRange(((string)op["ids"]).Split(',').Select(long.Parse).Where(i => Comments[i] != value).Select(i => (i, value)));
                        }
                        if (ExtraIdInPlan) changes.Add((900, "x"));
                        var id = "P-" + (++_n);
                        _plans[id] = changes;
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object>
                        {
                            ["plan_id"] = id, ["cambios"] = (long)changes.Count,
                            ["filas"] = changes.Select(c => (object)new List<object> { c.Id, "set_param", "Comments", Comments[c.Id] ?? "", c.Value }).ToList(),
                        } };
                    }
                    case Wire.OpCommit:
                    {
                        var p = _plans[(string)r.Args["plan_id"]];
                        var trial = Arg.GetString(r.Args, "modo") == "ensayo";
                        if (trial && Engine.TrialGuard.Refuse(Modified, "commit modo=ensayo") is string refuse)   // igual que el add-in (P0)
                            return Response.Fail(r.Id, Status.Rechazado, refuse);
                        if (trial)
                        {
                            TrialCommits++;
                            Dictionary<long, string> before;
                            lock (Comments) { before = new Dictionary<long, string>(Comments); foreach (var c in p) Comments[c.Id] = c.Value; }
                            var tempOk = p.All(c => Comments[c.Id] == c.Value);
                            if (!LeakTrial) lock (Comments) foreach (var kv in before) Comments[kv.Key] = kv.Value;
                            return new Response { Id = r.Id, Estado = LeakTrial ? Status.Desconocido : "ensayado", Datos = new Dictionary<string, object>
                            {
                                ["plan_id"] = r.Args["plan_id"],
                                ["ensayo"] = new Dictionary<string, object> { ["grupo_estado"] = LeakTrial ? "Started" : "RolledBack", ["temporal_ok"] = tempOk, ["restauracion_ok"] = !LeakTrial },
                            } };
                        }
                        Commits++;
                        if (!CommitNoop) { lock (Comments) foreach (var c in p) Comments[c.Id] = c.Value; Modified = true; }
                        if (CommitDelayMs > 0) await Task.Delay(CommitDelayMs, CancellationToken.None);
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object> { ["plan_id"] = r.Args["plan_id"], ["verificacion"] = new Dictionary<string, object> { ["releidos"] = (long)p.Count } } };
                    }
                    case Wire.OpJob:
                        return new Response { Id = r.Id };
                    case Wire.OpDoc:
                    {
                        var verb = Arg.GetString(r.Args, "accion");
                        if (verb == "guardar")
                        {
                            Saves++;
                            if (!SaveDoesNothing)
                            {
                                lock (Comments) DiskComments = new Dictionary<long, string>(Comments);
                                System.IO.File.AppendAllText(DiskFile, "save" + Saves);
                                Modified = false;
                            }
                            return new Response { Id = r.Id, Datos = new Dictionary<string, object> { ["accion"] = "guardar" } };
                        }
                        if (verb == "verificar_disco")
                        {
                            if (Modified) return Response.Fail(r.Id, Status.NecesitaUsuario, "cambios sin guardar");
                            var ids = Arg.GetStringList(r.Args, "ids").Select(long.Parse).ToList();
                            return new Response { Id = r.Id, Datos = new Dictionary<string, object>
                            {
                                ["sha256"] = RevitCli.FileSha256(DiskFile),
                                ["filas"] = ids.Select(i => (object)new List<object> { i, DiskComments.ContainsKey(i), DiskComments.TryGetValue(i, out var v) ? v : null }).ToList(),
                            } };
                        }
                        return Response.Fail(r.Id, Status.Invalido, verb);
                    }
                }
                return Response.Fail(r.Id, Status.NoSoportado, r.Op);
            }
        }

        private static (PipeHost Host, string Pipe) Host(FakeBim fake)
        {
            var pipe = "PDI.RevitMcp.ahtest." + Guid.NewGuid().ToString("N").Substring(0, 8);
            var h = new PipeHost(pipe, null, new SessionRegistry(), fake, new EngineConfig());
            h.Start();
            return (h, pipe);
        }

        private static PlanDraft Plan(string value = "NRT", params long[] ids) => PlanFor(Print, value, ids);

        private static PlanDraft PlanFor(string print, string value = "NRT", params long[] ids)
        {
            var d = new PlanDraft { Objective = "t", DocKey = Key, DocPrint = print, LengthUnit = "mm", Coordinates = "interno_revit" };
            var q = new OpDraft { Operation = "consultar" }; q.Params["categoria"] = "Doors"; d.Ops.Add(q);
            var w = new OpDraft { Operation = "fijar_parametro" };
            w.Scope["categoria"] = "Doors"; w.Scope["ids"] = (ids.Length == 0 ? new long[] { 101, 102, 103 } : ids).Cast<object>().ToList();
            w.Params["parametro"] = "Comments"; w.Params["valor"] = value; w.Params["unidades"] = "texto";
            w.DependsOn.Add("#0");
            d.Ops.Add(w);
            d.AssignIds();
            return d;
        }

        private static RevitBackend Backend(string pipe, FakeBim fake, string mode, out string err) =>
            RevitBackend.Open(RevitLink.ForTestPipe(pipe, fake.Pid), mode, out err);

        [Fact]
        public void Trial_FromAgentHost_IsEnsayada_ModelUnchanged_NothingPersisted()
        {
            var fake = new FakeBim();
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "ensayo", out var err))
            {
                Assert.Null(err);
                var before = new Dictionary<long, string>(fake.Comments);
                var dir = AH.NewDir("rv-ensayo");
                using (var r = PlanRunner.Open(dir, Plan(), b, new RunnerOptions { RealWrites = "ensayo" }, out var diags))
                {
                    Assert.NotNull(r);
                    var s = r.Run();
                    var w = r.State.Ops[r.Plan.Ops[1].Id];
                    Assert.Equal(OpState.Ensayada, w.State);
                    Assert.Contains("ensayadas", s);
                    Assert.Equal("RolledBack", w.Evidence["addin_grupo_estado"]);
                    Assert.Equal(3L, w.Evidence["restaurados_relectura"]);
                }
                Assert.Equal(before, fake.Comments);
                Assert.Equal(1, fake.TrialCommits);
                Assert.Equal(0, fake.Commits);
                Assert.Contains(b.Link.Calls, c => (string)c["op"] == Wire.OpCommit && (string)c["modo"] == "ensayo");
            }
        }

        [Fact]
        public void WritesNeedOperatorAuthorization_AndMatchingMode()
        {
            var fake = new FakeBim();
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "ensayo", out _))
            {
                Assert.Null(PlanRunner.Open(AH.NewDir("rv-noauth"), Plan(), b, new RunnerOptions(), out var d1));
                Assert.Contains(d1, d => d.Code == "escrituras_no_permitidas");
                Assert.Null(PlanRunner.Open(AH.NewDir("rv-mismatch"), Plan(), b, new RunnerOptions { RealWrites = "persistente" }, out var d2));
                Assert.Contains(d2, d => d.Code == "escrituras_no_permitidas");
                Assert.Equal(0, fake.TrialCommits + fake.Commits);
            }
        }

        [Fact]
        public void Identity_WrongPid_ReadOnlyLevels_Workshared_AndOtherDocument_AreRefused()
        {
            var fake = new FakeBim();
            var (host, pipe) = Host(fake);
            using (host)
            {
                Assert.Null(RevitBackend.Open(RevitLink.ForTestPipe(pipe, 1), "ensayo", out var e1));
                Assert.Contains("pid", e1);
                fake.Levels = "R";
                Assert.Null(Backend(pipe, fake, "ensayo", out var e2));
                Assert.Contains("no permite cambios", e2);
                fake.Levels = "RPW"; fake.Workshared = true;
                Assert.Null(Backend(pipe, fake, "persistente", out var e3));
                Assert.Contains("workshared", e3);
                fake.Workshared = false;
                using (var b = Backend(pipe, fake, "ensayo", out _))
                {
                    var other = PlanFor("otra-huella");
                    Assert.Null(PlanRunner.Open(AH.NewDir("rv-doc"), other, b, new RunnerOptions { RealWrites = "ensayo" }, out var d));
                    Assert.Contains(d, x => x.Code == ErrorClass.DocumentoDistinto);
                }
                Assert.Null(RevitLink.ForPid(0, out var e4));
                Assert.Contains("nunca elige", e4);
            }
        }

        [Fact]
        public void TrialThatDoesNotRestore_IsNeverReportedAsEnsayada()
        {
            var fake = new FakeBim { LeakTrial = true };
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "ensayo", out _))
            using (var r = PlanRunner.Open(AH.NewDir("rv-leak"), Plan(), b, new RunnerOptions { RealWrites = "ensayo" }, out _))
            {
                r.Run();
                var w = r.State.Ops[r.Plan.Ops[1].Id];
                Assert.Equal(OpState.RequiereReconciliar, w.State);
                Assert.Equal(1, fake.TrialCommits);   // no se repitió
            }
        }

        [Fact]
        public void PlanOutsideScope_IsRejected_BeforeAnyCommit()
        {
            var fake = new FakeBim { ExtraIdInPlan = true };
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "ensayo", out _))
            using (var r = PlanRunner.Open(AH.NewDir("rv-scope"), Plan(), b, new RunnerOptions { RealWrites = "ensayo" }, out _))
            {
                r.Run();
                var w = r.State.Ops[r.Plan.Ops[1].Id];
                Assert.Equal(OpState.Fallida, w.State);
                Assert.Contains("fuera del alcance", w.Note);
                Assert.Equal(0, fake.TrialCommits + fake.Commits);
            }
        }

        [Fact]
        public void PersistentCommit_TimesOutAfterApplying_IsObserved_NotRepeated()
        {
            var fake = new FakeBim { CommitDelayMs = 1500 };
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "persistente", out var err))
            {
                Assert.Null(err);
                b.Link.WriteTimeout = TimeSpan.FromMilliseconds(400);
                using (var r = PlanRunner.Open(AH.NewDir("rv-timeout"), Plan("FINAL"), b, new RunnerOptions { RealWrites = "persistente" }, out _))
                {
                    r.Run();
                    var w = r.State.Ops[r.Plan.Ops[1].Id];
                    Assert.Equal(1, fake.Commits);   // un timeout no es "no aplicado": no se repite
                    Assert.True(w.State == OpState.Verificada || w.State == OpState.RequiereVerificacion, w.State + " " + w.Note);
                    Assert.Contains("efecto_incierto", System.IO.File.ReadAllText(r.State.JournalPath));
                }
                Assert.All(new long[] { 101, 102, 103 }, i => Assert.Equal("FINAL", fake.Comments[i]));
            }
        }

        /// <summary>
        /// NRT-03 preparado (sin Revit): el AgentHost cae en cada punto crítico de una escritura persistente y otro AgentHost
        /// retoma el mismo diario. Nunca se repite un commit cuyo efecto pudo ocurrir; el resultado final se verifica observando el modelo.
        /// Sólo cae el AgentHost (excepción simulada); el "Revit" (FakeBim) sigue vivo, para verificar la recuperación independiente del cliente.
        /// </summary>
        [Theory]
        [InlineData("backend.antes_enviar", 1)]     // antes de enviar: no hubo efecto, se puede enviar al reanudar
        [InlineData("backend.tras_commit", 1)]      // tras un posible commit: se observa, no se repite
        [InlineData("runner.tras_ejecutar", 1)]     // aplicado pero sin registrar el resultado
        [InlineData("runner.en_verificacion", 1)]   // durante la verificación
        [InlineData("runner.tras_verificar", 1)]    // verificado pero sin registrar
        public void Nrt03_AgentHostCrash_AtEachPoint_ResumesWithoutDuplicateCommit(string point, int expectedCommits)
        {
            var fake = new FakeBim();
            var (host, pipe) = Host(fake);
            var dir = AH.NewDir("rv-nrt03");
            var plan = Plan("NRT03");
            using (host)
            {
                using (var b1 = Backend(pipe, fake, "persistente", out var e1))
                {
                    Assert.Null(e1);
                    var f = new Faults().Configure(point, "excepcion");
                    b1.Faults = f;
                    using (var r1 = PlanRunner.Open(dir, plan, b1, new RunnerOptions { RealWrites = "persistente", Faults = f, LeaseTtl = TimeSpan.FromMilliseconds(1500) }, out var d1))
                    {
                        Assert.NotNull(r1);
                        Assert.Throws<CrashException>(() => r1.Run());
                    }
                }
                System.Threading.Thread.Sleep(1700);   // vence el lease del AgentHost caído
                using (var b2 = Backend(pipe, fake, "persistente", out var e2))
                using (var r2 = PlanRunner.Open(dir, null, b2, new RunnerOptions { RealWrites = "persistente", LeaseTtl = TimeSpan.FromSeconds(30) }, out var d2))
                {
                    Assert.Null(e2);
                    Assert.True(r2 != null, string.Join("; ", d2 ?? new List<Diagnostic>()));
                    r2.Run();
                    var w = r2.State.Ops[r2.Plan.Ops[1].Id];
                    Assert.True(w.State == OpState.Verificada, point + ": " + w.State + " " + w.Note);
                }
                Assert.Equal(expectedCommits, fake.Commits);
                Assert.All(new long[] { 101, 102, 103 }, i => Assert.Equal("NRT03", fake.Comments[i]));
                Assert.Null(fake.Comments[104]);
            }
        }

        [Fact]
        public void P0_TrialOnModifiedDocument_IsRefusedBeforeAnyChange_AndNotUncertain()
        {
            var fake = new FakeBim { Modified = true };
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "ensayo", out var err))
            using (var r = PlanRunner.Open(AH.NewDir("rv-p0"), Plan(), b, new RunnerOptions { RealWrites = "ensayo" }, out _))
            {
                Assert.Null(err);
                r.Run();
                var w = r.State.Ops[r.Plan.Ops[1].Id];
                Assert.Equal(OpState.Fallida, w.State);                      // rechazo previo: no incierto, no reconciliar
                Assert.Contains(Engine.TrialGuard.Code, (w.Note ?? "") + " " + (w.Error ?? ""));
                Assert.Equal(0, fake.TrialCommits);
                Assert.True(fake.Modified);
                Assert.Null(fake.Comments[101]); Assert.Equal("x", fake.Comments[102]);
            }
        }

        [Fact]
        public void Semantics_ConsultaIsRead_EnsayoIsWrite()
        {
            Assert.Equal(OpSemantics.ModeQuery, OpSemantics.ExecMode(new Dictionary<string, object> { ["modo"] = "consulta" }, out _));
            Assert.Equal(OpSemantics.ModeTrial, OpSemantics.ExecMode(new Dictionary<string, object> { ["modo"] = "prueba" }, out _));
            Assert.Equal(OpSemantics.Kind.Write, OpSemantics.Classify(Wire.OpExec, new Dictionary<string, object> { ["modo"] = "ensayo" }, out _));
            Assert.Equal(OpSemantics.Kind.Read, OpSemantics.Classify(Wire.OpExec, new Dictionary<string, object> { ["modo"] = "consulta" }, out _));
        }

        private static Nrt05Options Nrt05Opts(FakeBim fake, string dir) => new Nrt05Options
        {
            Pid = fake.Pid, OutDir = dir, Category = "Doors", Param = "Comments", Value = "NRT05", ExpectedPath = "C:\\nrt\\copia.rvt",
            Ids = new List<long> { 101, 102, 103 }, Authorized = true, DiskPath = _ => fake.DiskFile,
        };

        private static FakeBim Nrt05Fake()
        {
            var f = new FakeBim { DiskFile = System.IO.Path.Combine(AH.NewDir("nrt05-disk"), "copia.rvt") };
            System.IO.File.WriteAllText(f.DiskFile, "rvt-v0");
            lock (f.Comments) f.DiskComments = new Dictionary<long, string>(f.Comments);
            return f;
        }

        /// <summary>beta1 NRT-05 contra el add-in falso: commit persistente, guardado, SHA distinto, verificación en disco y sin commit duplicado.</summary>
        [Fact]
        public void Nrt05_PersistentWrite_SavedAndVerifiedOnDisk_NoDuplicateCommit()
        {
            var fake = Nrt05Fake();
            var (host, pipe) = Host(fake);
            var dir = AH.NewDir("nrt05");
            using (host)
            using (var link = RevitLink.ForTestPipe(pipe, fake.Pid))
            {
                var code = RevitCli.Nrt05Run(link, Nrt05Opts(fake, dir));
                var rep = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "nrt05_informe.json"));
                Assert.True(code == 0, rep);
                Assert.Equal(1, fake.Commits);
                Assert.Equal(1, fake.Saves);
                Assert.All(new long[] { 101, 102, 103 }, i => Assert.Equal("NRT05", fake.DiskComments[i]));
                Assert.Contains("\"sha256_cambio\":true", rep.Replace(" ", ""));
            }
        }

        [Fact]
        public void Nrt05_SaveThatDoesNotChangeTheFile_Fails()
        {
            var fake = Nrt05Fake(); fake.SaveDoesNothing = true;
            var (host, pipe) = Host(fake);
            var dir = AH.NewDir("nrt05-nosave");
            using (host)
            using (var link = RevitLink.ForTestPipe(pipe, fake.Pid))
                Assert.Equal(2, RevitCli.Nrt05Run(link, Nrt05Opts(fake, dir)));   // hash igual = no persistió: nunca APROBADA
        }

        [Theory]
        [InlineData("sin_autorizacion")]
        [InlineData("ruta_distinta")]
        [InlineData("workshared")]
        [InlineData("modificado")]
        public void Nrt05_Blocked_BeforeAnyWrite(string caso)
        {
            var fake = Nrt05Fake();
            if (caso == "workshared") fake.Workshared = true;
            if (caso == "modificado") fake.Modified = true;
            var (host, pipe) = Host(fake);
            var o = Nrt05Opts(fake, AH.NewDir("nrt05-b"));
            if (caso == "sin_autorizacion") o.Authorized = false;
            if (caso == "ruta_distinta") o.ExpectedPath = "C:\\nrt\\otra.rvt";
            using (host)
            using (var link = RevitLink.ForTestPipe(pipe, fake.Pid))
                Assert.Equal(3, RevitCli.Nrt05Run(link, o));
            Assert.Equal(0, fake.Commits);
            Assert.Equal(0, fake.Saves);
        }

        [Fact]
        public void OpState_DirectiveNames_CoverEveryState()
        {
            foreach (var s in OpState.All) Assert.False(string.IsNullOrEmpty(OpState.DirectiveName(s)));
            Assert.Equal("AUTHORIZED", OpState.DirectiveName(OpState.Validada, authorized: true));
            Assert.Equal("OUTCOME_UNKNOWN", OpState.DirectiveName(OpState.RequiereVerificacion));
            Assert.Equal("ROLLED_BACK", OpState.DirectiveName(OpState.Ensayada));
        }

        // ============================== rc1 ==============================

        private static PlanDraft LotePlan(Dictionary<long, string> values)
        {
            var d = new PlanDraft { Objective = "lote", DocKey = Key, DocPrint = Print, LengthUnit = "mm", Coordinates = "interno_revit" };
            var w = new OpDraft { Operation = "fijar_parametro_lote" };
            w.Scope["categoria"] = "Doors"; w.Scope["ids"] = values.Keys.Cast<object>().ToList();
            w.Params["parametro"] = "Comments";
            w.Params["valores"] = values.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => (object)kv.Value);
            d.Ops.Add(w); d.AssignIds();
            return d;
        }

        [Theory]
        [InlineData("worksharing")]
        [InlineData("solo_lectura")]
        [InlineData("modificado")]
        [InlineData("retener")]
        [InlineData("huella_doc")]
        public void Rc1_FailClosed_MissingContextFlag_RefusesWrites_ButAllowsRead(string flag)
        {
            var fake = new FakeBim { OmitContextFlag = flag };
            var (host, pipe) = Host(fake);
            using (host)
            {
                Assert.Null(Backend(pipe, fake, "persistente", out var e1)); Assert.NotNull(e1);
                Assert.Null(Backend(pipe, fake, "ensayo", out var e2)); Assert.NotNull(e2);
                using (var b = Backend(pipe, fake, "lectura", out var e3)) Assert.True(flag == "huella_doc" || b != null, e3);
            }
            Assert.Equal(0, fake.Commits + fake.TrialCommits);
        }

        [Fact]
        public void Rc11_SlowIdleRevit_ReadsRetried_WriteVerified_OneCommit()
        {
            var fake = new FakeBim();
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "persistente", out var err))
            using (var r = PlanRunner.Open(AH.NewDir("rc11-busy"), LotePlan(new Dictionary<long, string> { [101] = "A", [102] = "B" }), b, new RunnerOptions { RealWrites = "persistente" }, out var diags))
            {
                Assert.True(r != null, err + string.Join("; ", diags ?? new List<Diagnostic>()));
                fake.BusyReads = 2;
                r.Run();
                var w = r.State.Ops[r.Plan.Ops[0].Id];
                Assert.True(w.State == OpState.Verificada, w.State + " " + w.Note);
            }
            Assert.Equal(1, fake.Commits);
            Assert.True(fake.BusyReads < 0);
        }

        /// <summary>
        /// 0.8.0 B0 (informe §9): 586 luminarias, leer y cambiar un parámetro de cada una. Uno a uno (1 lectura + 1 escritura por elemento,
        /// como en la hipótesis del informe) frente a un plan de BIMBRIDGE (3 lotes de ≤200). Add-in FALSO por la tubería real:
        /// las cifras son MEDIDO_SIMULADO y los tokens ESTIMADOS (caracteres/4). Con BIMBRIDGE_BENCH_OUT=&lt;carpeta&gt; escribe B0_586.json.
        /// </summary>
        [Fact]
        public void Bench_B0_586_OneByOne_vs_Plan()
        {
            const int N = 586;
            var values = Enumerable.Range(0, N).ToDictionary(i => 1000L + i, i => "LT-" + (i % 7));

            // A · uno a uno
            var fa = new FakeBim(); fa.Seed(N);
            var (ha, pa) = Host(fa);
            Dictionary<string, object> wireA; long agentCharsA = 0; long msA;
            using (ha)
            using (var link = RevitLink.ForTestPipe(pa, fa.Pid))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var cx = link.Call(Wire.OpContext, new Dictionary<string, object>(), false);
                var doc = (string)((Dictionary<string, object>)cx.Datos)["doc"];
                foreach (var kv in values)
                {
                    var q = new Dictionary<string, object> { [Wire.DocArg] = doc, ["page_size"] = 1L, ["f"] = new List<object> { "id", "Comments" }, ["ids"] = new List<object> { kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
                    var qr = link.Call(Wire.OpQuery, q, false);
                    var pl = new Dictionary<string, object> { [Wire.DocArg] = doc, ["ops"] = new List<object> { new Dictionary<string, object> { ["op"] = "set_param", ["ids"] = kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), ["name"] = "Comments", ["value"] = kv.Value } } };
                    var pr = link.Call(Wire.OpPlan, pl, false);
                    var cm = new Dictionary<string, object> { [Wire.DocArg] = doc, ["plan_id"] = ((Dictionary<string, object>)pr.Datos)["plan_id"] };
                    var cr = link.Call(Wire.OpCommit, cm, true);
                    // lo que el agente escribe y lee en esas dos llamadas de herramienta (lectura; escritura = plan+commit)
                    agentCharsA += Json.Write(q).Length + Json.Write(qr.ToJson()).Length + Json.Write(pl).Length + Json.Write(pr.ToJson()).Length + Json.Write(cr.ToJson()).Length;
                }
                msA = sw.ElapsedMilliseconds;
                wireA = Benchmarks.Wire(link.Calls);
            }
            Assert.Equal(N, fa.Commits);
            Assert.True(values.All(kv => fa.Comments[kv.Key] == kv.Value));

            // B · plan BIMBRIDGE: 3 operaciones de lote (≤200 objetivos cada una)
            var fb = new FakeBim(); fb.Seed(N);
            var (hb, pb) = Host(fb);
            Dictionary<string, object> wireB; long agentCharsB; long msB; int ops;
            using (hb)
            {
                var link = RevitLink.ForTestPipe(pb, fb.Pid);
                using (var b = RevitBackend.Open(link, "persistente", out var err))
                {
                    Assert.Null(err);
                    var d = new PlanDraft { Objective = "B0 586", DocKey = Key, DocPrint = Print, LengthUnit = "mm", Coordinates = "interno_revit" };
                    foreach (var chunk in values.Select((kv, i) => (kv, i)).GroupBy(x => x.i / RevitBackend.MaxTargets))
                    {
                        var w = new OpDraft { Operation = "fijar_parametro_lote" };
                        w.Scope["categoria"] = "Doors"; w.Scope["ids"] = chunk.Select(x => (object)x.kv.Key).ToList();
                        w.Params["parametro"] = "Comments";
                        w.Params["valores"] = chunk.ToDictionary(x => x.kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), x => (object)x.kv.Value);
                        d.Ops.Add(w);
                    }
                    d.AssignIds();
                    ops = d.Ops.Count;
                    var planJson = Json.Write(d.ToJson());
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using (var r = PlanRunner.Open(AH.NewDir("bench-b0"), d, b, new RunnerOptions { RealWrites = "persistente" }, out var diags))
                    {
                        Assert.True(r != null, string.Join("; ", diags ?? new List<Diagnostic>()));
                        var summary = r.Run();
                        msB = sw.ElapsedMilliseconds;
                        Assert.All(r.Plan.Ops, o => Assert.Equal(OpState.Verificada, r.State.Ops[o.Id].State));
                        var report = Json.Write(Handoff.Build(r));
                        // agente: envía el plan (validar), lo ejecuta (resumen) y pide el informe de verificación/handoff
                        agentCharsB = planJson.Length + summary.Length + report.Length;
                    }
                    wireB = Benchmarks.Wire(link.Calls);
                }
            }
            Assert.Equal(3, ops);
            Assert.Equal(3, fb.Commits);
            Assert.True(values.All(kv => fb.Comments[kv.Key] == kv.Value));

            var agentCallsA = 2L * N;   // hipótesis del informe: una lectura y una escritura por elemento
            const long agentCallsB = 3; // validar · ejecutar · informe
            Assert.True((long)wireB["llamadas_cable"] < (long)wireA["llamadas_cable"] / 50);

            var result = new Dictionary<string, object>
            {
                ["id"] = "B0", ["estado"] = Benchmarks.Simulated, ["elementos"] = (long)N,
                ["nota"] = "add-in falso por la tubería real (sin Revit); los tiempos no representan Revit; tokens " + TokenEstimate.Method,
                ["uno_a_uno"] = new Dictionary<string, object> { ["llamadas_agente"] = agentCallsA, ["chars_agente"] = agentCharsA, ["tokens_estimados_agente"] = TokenEstimate.FromChars(agentCharsA), ["ms"] = msA, ["commits"] = (long)fa.Commits, ["cable"] = wireA },
                ["plan_bimbridge"] = new Dictionary<string, object> { ["llamadas_agente"] = agentCallsB, ["operaciones"] = (long)ops, ["chars_agente"] = agentCharsB, ["tokens_estimados_agente"] = TokenEstimate.FromChars(agentCharsB), ["ms"] = msB, ["commits"] = (long)fb.Commits, ["cable"] = wireB },
                ["reduccion_llamadas_agente"] = Math.Round(100.0 * (agentCallsA - agentCallsB) / agentCallsA, 2),
                ["reduccion_tokens_estimados_agente"] = Math.Round(100.0 * (agentCharsA - agentCharsB) / agentCharsA, 2),
            };
            var outDir = Environment.GetEnvironmentVariable("BIMBRIDGE_BENCH_OUT");
            if (!string.IsNullOrEmpty(outDir)) { System.IO.Directory.CreateDirectory(outDir); System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "B0_586.json"), Json.Write(result)); }
        }

        private static PlanDraft IndependentPlan(Dictionary<string, object> expected, string buildSource, Dictionary<string, object> exceptions = null)
        {
            var d = new PlanDraft { Objective = "contraste", DocKey = Key, DocPrint = Print, LengthUnit = "mm", Coordinates = "interno_revit" };
            var v = new OpDraft { Operation = "verificar_independiente" };
            v.Scope["categoria"] = "Doors";
            v.Params["fuente_id"] = "E-1.3.02"; v.Params["fuente_tipo"] = "plano"; v.Params["fuente_construccion"] = buildSource;
            v.Params["fuente_sha256"] = PDI.RevitMcp.Engine.Home.Sha256Hex("E-1.3.02 contenido");
            v.Params["parametro_clave"] = "Comments"; v.Params["esperado"] = expected;
            if (exceptions != null) v.Params["excepciones"] = exceptions;
            d.Ops.Add(v); d.AssignIds();
            return d;
        }

        [Fact]
        public void V080_IndependentVerification_AgainstModel_FindsSeededOmissionAndExtra_ReadOnly()
        {
            var fake = new FakeBim();
            lock (fake.Comments) { fake.Comments[101] = "F1"; fake.Comments[102] = "F2"; fake.Comments[103] = "F7"; fake.Comments[104] = "F9"; }
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "lectura", out var err))
            {
                Assert.Null(err);
                var exp = new Dictionary<string, object> { ["F1"] = "-", ["F2"] = "-", ["F3"] = "-" };
                using (var r = PlanRunner.Open(AH.NewDir("v080-ind"), IndependentPlan(exp, "markup-riser", new Dictionary<string, object> { ["F9"] = "fuente hoja 2" }), b, new RunnerOptions(), out var diags))
                {
                    Assert.True(r != null, string.Join("; ", diags));
                    r.Run();
                    var rec = r.State.Ops[r.Plan.Ops[0].Id];
                    Assert.NotEqual(OpState.Verificada, rec.State);            // hay diferencias: no se certifica
                    var j = System.IO.File.ReadAllText(r.State.JournalPath);
                    Assert.Contains("F3", j);                                  // omitido
                    Assert.Contains("103", j);                                 // adicional (F7)
                }
                // misma fuente que la construcción → no independiente, no se certifica
                using (var r2 = PlanRunner.Open(AH.NewDir("v080-ind2"), IndependentPlan(new Dictionary<string, object> { ["F1"] = "-" }, "E-1.3.02"), b, new RunnerOptions(), out _))
                {
                    r2.Run();
                    Assert.NotEqual(OpState.Verificada, r2.State.Ops[r2.Plan.Ops[0].Id].State);
                    Assert.Contains("no_independiente", System.IO.File.ReadAllText(r2.State.JournalPath));
                }
            }
            Assert.Equal(0, fake.Commits);
        }

        [Fact]
        public void V080_Handoff_AfterCrash_ProposesReconcile_NotRepeat_AndAfterResumeIsDone()
        {
            var fake = new FakeBim();
            var (host, pipe) = Host(fake);
            var dir = AH.NewDir("v080-handoff");
            using (host)
            {
                var b1 = Backend(pipe, fake, "persistente", out var err);
                var f = new Faults().Configure("runner.tras_ejecutar", "excepcion");
                using (var r = PlanRunner.Open(dir, Plan("H"), b1, new RunnerOptions { RealWrites = "persistente", Faults = f, LeaseTtl = TimeSpan.FromMilliseconds(300) }, out _))
                {
                    Assert.Throws<CrashException>(() => r.Run());
                    var h = Handoff.Build(r);
                    Assert.Equal(Handoff.Schema, h["esquema"]);
                    Assert.NotEmpty((List<object>)h["inciertas"]);
                    Assert.Contains("NO repetir", (string)h["siguiente_paso_seguro"]);
                }
                System.Threading.Thread.Sleep(500);
                using (var b2 = Backend(pipe, fake, "persistente", out _))
                using (var r2 = PlanRunner.Open(dir, null, b2, new RunnerOptions { RealWrites = "persistente" }, out _))
                {
                    r2.Run();
                    var h2 = Handoff.Build(r2);
                    Assert.Empty((List<object>)h2["inciertas"]);
                    Assert.Equal("nada: plan terminado", h2["siguiente_paso_seguro"]);
                }
            }
            Assert.Equal(1, fake.Commits);
        }

        [Fact]
        public void Rc1_BatchWrite_DifferentValuesPerElement_OneCommit_Verified()
        {
            var fake = new FakeBim();
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "persistente", out var err))
            using (var r = PlanRunner.Open(AH.NewDir("rc1-lote"), LotePlan(new Dictionary<long, string> { [101] = "A", [102] = "B", [103] = "A" }), b, new RunnerOptions { RealWrites = "persistente" }, out var diags))
            {
                Assert.True(r != null, err + string.Join("; ", diags ?? new List<Diagnostic>()));
                r.Run();
                var w = r.State.Ops[r.Plan.Ops[0].Id];
                Assert.True(w.State == OpState.Verificada, w.State + " " + w.Note);
            }
            Assert.Equal(1, fake.Commits);
            Assert.Equal("A", fake.Comments[101]); Assert.Equal("B", fake.Comments[102]); Assert.Equal("A", fake.Comments[103]);
            Assert.Null(fake.Comments[104]);
        }

        [Fact]
        public void Rc1_BatchValidator_RejectsMismatchedIds_NonNumericKeys_AndOversize()
        {
            var ok = LotePlan(new Dictionary<long, string> { [1] = "x", [2] = "y" });
            Assert.Empty(IrValidator.Validate(ok));
            var bad = LotePlan(new Dictionary<long, string> { [1] = "x", [2] = "y" });
            bad.Ops[0].Scope["ids"] = new List<object> { 1L, 3L }; bad.AssignIds();
            Assert.Contains(IrValidator.Validate(bad), e => e.Contains("coincidir"));
            var nonNum = LotePlan(new Dictionary<long, string> { [1] = "x" });
            nonNum.Ops[0].Params["valores"] = new Dictionary<string, object> { ["abc"] = "x" }; nonNum.AssignIds();
            Assert.Contains(IrValidator.Validate(nonNum), e => e.Contains("numéricos"));
            var big = LotePlan(Enumerable.Range(1, IrValidator.MaxBatch + 1).ToDictionary(i => (long)i, i => "v"));
            Assert.Contains(IrValidator.Validate(big), e => e.Contains("entre 1 y"));
            var empty = LotePlan(new Dictionary<long, string> { [1] = "x" });
            empty.Ops[0].Params["valores"] = new Dictionary<string, object> { ["1"] = "" }; empty.AssignIds();
            Assert.NotEmpty(IrValidator.Validate(empty));
        }

        [Fact]
        public void Rc1_CommitThatClaimsSuccessButAppliesNothing_IsNeverVerified()
        {
            var fake = new FakeBim { CommitNoop = true };
            var (host, pipe) = Host(fake);
            using (host)
            using (var b = Backend(pipe, fake, "persistente", out _))
            using (var r = PlanRunner.Open(AH.NewDir("rc1-noop"), Plan("X"), b, new RunnerOptions { RealWrites = "persistente" }, out _))
            {
                r.Run();
                var w = r.State.Ops[r.Plan.Ops[1].Id];
                Assert.NotEqual(OpState.Verificada, w.State);
                Assert.Equal(1, fake.Commits);   // no se reintenta a ciegas
            }
        }

        [Fact]
        public void Rc1_PaginatedRead_ReadsEveryPage_AndDetectsModelChangeBetweenPages()
        {
            var fake = new FakeBim { ForcePage = 2 };
            var (host, pipe) = Host(fake);
            using (host)
            {
                using (var b = Backend(pipe, fake, "persistente", out _))
                using (var r = PlanRunner.Open(AH.NewDir("rc1-pag"), Plan("PAG", 101, 102, 103, 104), b, new RunnerOptions { RealWrites = "persistente" }, out _))
                {
                    r.Run();
                    Assert.Equal(OpState.Verificada, r.State.Ops[r.Plan.Ops[1].Id].State);
                }
                Assert.All(new long[] { 101, 102, 103, 104 }, i => Assert.Equal("PAG", fake.Comments[i]));
                Assert.True(fake.QueryCalls >= 3);
                var commits = fake.Commits;
                fake.TotalChangesOnPage2 = true;
                using (var b = Backend(pipe, fake, "persistente", out _))
                using (var r = PlanRunner.Open(AH.NewDir("rc1-pag2"), Plan("PAG2", 101, 102, 103, 104), b, new RunnerOptions { RealWrites = "persistente" }, out _))
                {
                    r.Run();
                    Assert.NotEqual(OpState.Verificada, r.State.Ops[r.Plan.Ops[1].Id].State);
                }
                Assert.Equal(commits, fake.Commits);   // el cambio entre páginas se detecta ANTES de escribir
            }
        }

        [Theory]
        [InlineData(0, 0, 0, true)]
        [InlineData(2, 0, 0, false)]
        [InlineData(0, 1, 0, false)]
        [InlineData(0, 0, 3, false)]
        public void Rc1_QcLightingTags_IsReadOnly_AndDeviationsFailThePostcondition(long sinTag, long huerf, long dup, bool pass)
        {
            var fake = new FakeBim { QcCounts = new[] { sinTag, huerf, dup } };
            var (host, pipe) = Host(fake);
            var d = new PlanDraft { Objective = "qc", DocKey = Key, DocPrint = Print, LengthUnit = "mm", Coordinates = "interno_revit" };
            var q = new OpDraft { Operation = "verificar_qc" }; q.Params["receta"] = RevitBackend.QcLightingTags; q.Scope["nivel"] = "L02";
            d.Ops.Add(q); d.AssignIds();
            using (host)
            using (var b = Backend(pipe, fake, "lectura", out var err))
            using (var r = PlanRunner.Open(AH.NewDir("rc1-qc"), d, b, new RunnerOptions(), out var diags))
            {
                Assert.True(r != null, err + string.Join("; ", diags ?? new List<Diagnostic>()));
                r.Run();
                var st = r.State.Ops[r.Plan.Ops[0].Id].State;
                Assert.Equal(pass, st == OpState.Verificada);
            }
            Assert.True(fake.CheckCalls >= 1);
            Assert.Equal(0, fake.Commits + fake.TrialCommits);
        }

        [Fact]
        public void Rc1_Nrt05_AnyNegativePreSaveCheck_StopsBeforeSave()
        {
            var fake = Nrt05Fake(); fake.CommitNoop = true;
            var (host, pipe) = Host(fake);
            using (host)
            using (var link = RevitLink.ForTestPipe(pipe, fake.Pid))
                Assert.Equal(2, RevitCli.Nrt05Run(link, Nrt05Opts(fake, AH.NewDir("rc1-nrt05"))));
            Assert.Equal(0, fake.Saves);
        }

        [Fact]
        public void Rc1_NativeGate_BlocksWithoutBothSignals_AndRevitCommandsNeverConnect()
        {
            Func<string, string> none = _ => null, yes = k => k == NativeGate.EnvVar ? NativeGate.EnvValue : null;
            Assert.False(NativeGate.Open(new[] { "revit", "nrt05" }, none));
            Assert.False(NativeGate.Open(new[] { "revit", "nrt05", NativeGate.Flag }, none));
            Assert.False(NativeGate.Open(new[] { "revit", "nrt05" }, yes));
            Assert.True(NativeGate.Open(new[] { "revit", "nrt05", NativeGate.Flag }, yes));
            if (Environment.GetEnvironmentVariable(NativeGate.EnvVar) != NativeGate.EnvValue)
                Assert.Equal(3, RevitCli.Run(new[] { "revit", "identidad", "--revit-pid", "1" }));
        }

        [Fact]
        public void Rc1_StableFile_CopiesAndHashes_AndDetectsConcurrentModification()
        {
            var dir = AH.NewDir("rc1-stable");
            var src = System.IO.Path.Combine(dir, "m.rvt");
            System.IO.File.WriteAllBytes(src, Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray());
            var r = Engine.StableFile.CopyAndHash(src, System.IO.Path.Combine(dir, "c1.rvt"), chunk: 65536);
            Assert.Equal(Engine.StableFile.HashFile(src), r.Sha256);
            Assert.Throws<System.IO.IOException>(() => Engine.StableFile.CopyAndHash(src, System.IO.Path.Combine(dir, "c2.rvt"),
                afterChunk: i => { if (i == 0) System.IO.File.AppendAllText(src, "otro proceso escribe"); }, chunk: 65536));
        }

        [Fact]
        public void Rc1_Nrt03Harness_AgainstFake_AllPoints_OneCommitEach_Verified()
        {
            var fake = Nrt05Fake();
            var (host, pipe) = Host(fake);
            var dir = AH.NewDir("rc1-nrt03");
            using (host)
            using (var link = RevitLink.ForTestPipe(pipe, fake.Pid))
            {
                var code = RevitCli.Nrt03Run(link, Nrt05Opts(fake, dir), RevitCli.Nrt03Points, TimeSpan.FromMilliseconds(1500));
                Assert.True(code == 0, System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "nrt03_informe.json")));
            }
            Assert.Equal(RevitCli.Nrt03Points.Length, fake.Commits);
            Assert.Equal(0, fake.Saves);
        }

        [Fact]
        public void Rc1_Nrt06Harness_AgainstFake_IntegratedPlan_RejectionsAndIdempotency()
        {
            var fake = Nrt05Fake();
            var (host, pipe) = Host(fake);
            var dir = AH.NewDir("rc1-nrt06");
            using (host)
            using (var link = RevitLink.ForTestPipe(pipe, fake.Pid))
            {
                var code = RevitCli.Nrt06Run(link, Nrt05Opts(fake, dir));
                Assert.True(code == 0, System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "nrt06_informe.json")));
            }
            Assert.Equal(2, fake.Commits);
            Assert.Equal(0, fake.Saves);
            Assert.EndsWith("fijo", fake.Comments[101]);
            Assert.EndsWith("#103", fake.Comments[103]);
        }

        [Fact]
        public void V080_Nrt07Harness_LostCommitResponse_UncertainThenVerified_OneCommit()
        {
            var fake = Nrt05Fake();
            var (host, pipe) = Host(fake);
            var dir = AH.NewDir("v080-nrt07");
            using (host)
            using (var link = RevitLink.ForTestPipe(pipe, fake.Pid))
            {
                var code = RevitCli.Nrt07Run(link, Nrt05Opts(fake, dir));
                Assert.True(code == 0, System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "nrt07_informe.json")));
            }
            Assert.Equal(1, fake.Commits);
            Assert.Equal(0, fake.Saves);
        }

        [Fact]
        public void Rc1_CampaignHarnesses_BlockWithoutAuthorizationOrWrongPath()
        {
            var fake = Nrt05Fake();
            var (host, pipe) = Host(fake);
            using (host)
            using (var link = RevitLink.ForTestPipe(pipe, fake.Pid))
            {
                var o = Nrt05Opts(fake, AH.NewDir("rc1-blk")); o.Authorized = false;
                Assert.Equal(3, RevitCli.Nrt03Run(link, o));
                Assert.Equal(3, RevitCli.Nrt06Run(link, o));
                o.Authorized = true; o.ExpectedPath = "C:\\otra\\ruta.rvt";
                Assert.Equal(3, RevitCli.Nrt06Run(link, o));
            }
            Assert.Equal(0, fake.Commits);
        }

        /// <summary>rc1 P1: banco de lotes contra el add-in falso por tubería real (IPC). Escribe resultados si PDI_BENCH_OUT está definido.</summary>
        [Fact]
        public void Rc1_Bench_BatchSizes_CallsBytesAndLatency()
        {
            var lines = new List<string> { "tamaño;modo;llamadas;commits;bytes;ms" };
            foreach (var n in new[] { 1, 10, 50, 200 })
            {
                foreach (var batch in new[] { true, false })
                {
                    if (!batch && n > 50) continue;   // N planes de 1 elemento: sólo como referencia
                    var fake = new FakeBim(); fake.Seed(n);
                    var (host, pipe) = Host(fake);
                    var ids = Enumerable.Range(1000, n).Select(x => (long)x).ToList();
                    using (host)
                    using (var b = Backend(pipe, fake, "persistente", out _))
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        if (batch)
                        {
                            using (var r = PlanRunner.Open(AH.NewDir("bench"), LotePlan(ids.ToDictionary(i => i, i => "V" + i)), b, new RunnerOptions { RealWrites = "persistente" }, out _))
                            { r.Run(); Assert.Equal(OpState.Verificada, r.State.Ops[r.Plan.Ops[0].Id].State); }
                        }
                        else
                        {
                            foreach (var id in ids)
                                using (var r = PlanRunner.Open(AH.NewDir("bench"), LotePlan(new Dictionary<long, string> { [id] = "V" + id }), b, new RunnerOptions { RealWrites = "persistente" }, out _))
                                    r.Run();
                        }
                        sw.Stop();
                        var bytes = b.Link.Calls.Sum(c => Convert.ToInt64(c["bytes_peticion"]) + Convert.ToInt64(c["bytes_respuesta"]));
                        lines.Add(n + ";" + (batch ? "lote" : "uno_a_uno") + ";" + b.Link.Calls.Count + ";" + fake.Commits + ";" + bytes + ";" + sw.ElapsedMilliseconds);
                        Assert.Equal(batch ? 1 : n, fake.Commits);
                    }
                }
            }
            var outFile = Environment.GetEnvironmentVariable("PDI_BENCH_OUT");
            if (!string.IsNullOrEmpty(outFile)) System.IO.File.WriteAllLines(outFile, lines);
        }
    }
}
