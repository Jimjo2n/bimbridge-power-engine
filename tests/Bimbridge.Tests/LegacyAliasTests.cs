// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// 0.9.4: the vocabulary is English. For one version, requests and persisted data written in the 0.9.3 (Spanish) vocabulary are
    /// still accepted (LegacyAliases). The Spanish strings in this file are intentional test data for those aliases.
    /// </summary>
    public class LegacyAliasTests
    {
        private static string NewDir(string tag) { var d = Path.Combine(Path.GetTempPath(), "bb-legacy-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }

        [Fact]
        public void ExecRequest_WithSpanishNamesAndValues_IsNormalizedToEnglish()
        {
            var a = new Dictionary<string, object>
            {
                ["codigo"] = "return 1;", ["modo"] = "ejecutar", ["tx"] = "grupo", ["clave"] = "k1", ["tarea"] = "t1", ["guardar"] = "mio",
                ["archivo"] = @"C:\x.cs", ["reconciliar"] = "no_aplicado", ["args"] = new Dictionary<string, object> { ["nivel"] = "L1" },
            };
            LegacyAliases.NormalizeArgs(Wire.OpExec, a);
            Assert.Equal("return 1;", a["code"]);
            Assert.Equal("run", a["mode"]);
            Assert.Equal("group", a["tx"]);
            Assert.Equal("k1", a[Wire.KeyArg]);
            Assert.Equal("t1", a[Wire.TaskArg]);
            Assert.Equal("mio", a["save_as"]);
            Assert.Equal(@"C:\x.cs", a["file"]);
            Assert.Equal("not_applied", a["reconcile"]);
            // Script arguments belong to the script: never renamed.
            Assert.Equal("L1", ((Dictionary<string, object>)a["args"])["nivel"]);
            Assert.DoesNotContain(a.Keys, k => LegacyAliases.IsLegacy(k));
            Assert.Equal(OpSemantics.Kind.Write, OpSemantics.Classify(Wire.OpExec, a, out _));

            var list = new Dictionary<string, object> { ["nombre"] = "lista" };
            LegacyAliases.NormalizeArgs(Wire.OpExec, list);
            Assert.Equal("list", list["name"]);
        }

        [Fact]
        public void PlanOps_WithSpanishArgumentNames_AreNormalized_ButUserDataIsNot()
        {
            var a = new Dictionary<string, object>
            {
                ["tarea"] = "t1",
                ["ops"] = new List<object>
                {
                    new Dictionary<string, object> { ["op"] = "view_create", ["tipo"] = "plano", ["nivel"] = "L1", ["origen"] = 12L, ["plantilla"] = "ninguna" },
                    new Dictionary<string, object> { ["op"] = "set_param", ["ids"] = "$x", ["nombre"] = "Comments", ["valor"] = "A" },
                    new Dictionary<string, object> { ["op"] = "view_set", ["ids"] = "5", ["detalle"] = "bajo", ["rango"] = new Dictionary<string, object> { ["corte"] = 4.0, ["fondo"] = 0.0 } },
                    new Dictionary<string, object> { ["op"] = "set_values", ["ids"] = "5", ["values"] = new Dictionary<string, object> { ["nivel"] = "keep me" } },
                    new Dictionary<string, object> { ["op"] = "place", ["origen"] = "pdf:abc" },
                },
            };
            LegacyAliases.NormalizeArgs(Wire.OpPlan, a);
            Assert.Equal("t1", a[Wire.TaskArg]);
            var ops = ((List<object>)a["ops"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal("plan", ops[0]["type"]);
            Assert.Equal("L1", ops[0]["level"]);
            Assert.Equal(12L, ops[0]["source"]);           // view_create: the source view
            Assert.Equal("none", ops[0]["template"]);
            Assert.Equal("Comments", ops[1]["name"]);
            Assert.Equal("A", ops[1]["value"]);
            Assert.Equal("coarse", ops[2]["detail"]);
            var range = (Dictionary<string, object>)ops[2]["range"];
            Assert.Equal(4.0, range["cut"]);
            Assert.Equal(0.0, range["bottom"]);
            Assert.Equal("keep me", ((Dictionary<string, object>)ops[3]["values"])["nivel"]);   // parameter names are the model's
            Assert.Equal("pdf:abc", ops[4]["origin"]);       // place: provenance marker
        }

        [Fact]
        public void UserValues_AreNeverTranslated_OnlyEnumLikeValuesAre()
        {
            var a = new Dictionary<string, object>
            {
                ["valor"] = "sí", ["sobrescribir"] = "si", ["sistema"] = "agua_fria",
                ["ops"] = new List<object>
                {
                    new Dictionary<string, object> { ["op"] = "set_param", ["ids"] = "5", ["nombre"] = "Comments", ["valor"] = "Sí" },
                    new Dictionary<string, object> { ["op"] = "view_set", ["ids"] = "5", ["plantilla"] = "Plano" },
                },
            };
            LegacyAliases.NormalizeArgs(Wire.OpPlan, a);
            Assert.Equal("sí", a["value"]);
            Assert.Equal("yes", a["overwrite"]);
            Assert.Equal("agua_fria", a["system"]);                    // a system type name outside connectors
            var ops = ((List<object>)a["ops"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal("Sí", ops[0]["value"]);
            Assert.Equal("Plano", ops[1]["template"]);                  // a view template name, not the legacy "plan"
            // Connectors: system= and flujo= are enums of the connector.
            var fam = new Dictionary<string, object> { ["sistema"] = "agua_fria", ["flujo"] = "entrada" };
            LegacyAliases.NormalizeArgs(Wire.OpFam, fam);
            Assert.Equal("cold_water", fam["system"]);
            Assert.Equal("in", fam["flow"]);
            // One-line commands: a legacy "sí" is a flag except where it is the user's text.
            Assert.Equal("Sí", Command.Parse("set value=Sí").Args["value"]);
            Assert.Equal("sí", Command.Parse("set valor=sí").Args["valor"]);
            Assert.Equal(true, Command.Parse("doc save_as overwrite=si").Args["overwrite"]);
        }

        [Fact]
        public void LegacyGeoCategories_Lengths_SetupSwitches_RecipeArgsAndManifests_AreRead()
        {
            var g = Bimbridge.Engine.Geo.GeoSpec.Parse(new Dictionary<string, object> { ["name"] = "montaje", ["against"] = "techos,vigas", ["cat"] = "luminarias" });
            Assert.Equal("mounting", g.Recipe);
            Assert.Equal(new[] { "Roofs", "StructuralFraming" }, g.Against);          // 0.9.3 "techos" meant Revit roofs
            Assert.Equal(new[] { "LightingFixtures" }, g.Categories);
            Assert.Equal(new[] { "Ceilings" }, Bimbridge.Engine.Geo.GeoSpec.ExpandCategories(new[] { "ceilings" }));

            Assert.True(Lengths.TryParse("6 pulg", out var ft, out _));
            Assert.Equal(0.5, ft, 6);

            Assert.Equal("update", LegacyAliases.SetupSwitch("actualizar"));
            Assert.Equal("levels=rpw", LegacyAliases.SetupSwitch("niveles=rpw"));
            Assert.Equal("restore=c:\\x", LegacyAliases.SetupSwitch("restaurar=c:\\x"));
            Assert.Equal("quiet", LegacyAliases.SetupSwitch("quiet"));

            var args = new Dictionary<string, object> { ["vista"] = 12L, ["guardar"] = "si", ["parametro"] = "Comments", ["valor"] = "si", ["antes"] = "guardar" };
            LegacyAliases.NormalizeRecipeArgs(args);
            Assert.Equal(12L, args["view"]);
            Assert.Equal("yes", args["save"]);
            Assert.Equal("si", args["value"]);                                       // the value written to the model is the user's
            Assert.Equal("save", args["before"]);

            var m = Json.ParseObject("{\"tolerancias\":{\"giro\":5},\"elementos\":[{\"ref\":\"L-1\",\"x\":1,\"y\":2,\"giro\":90,\"nivel\":\"L1\",\"tipos\":[\"D\"],\"etiqueta\":true}]}");
            var exp = Bimbridge.Engine.Qc.ManifestQc.Parse(m, out var opt);
            Assert.Equal(5.0, opt.TolRot);
            Assert.Equal(90.0, exp[0].RotDeg ?? 0);
            Assert.Equal("L1", exp[0].Level);
            Assert.Equal(new[] { "D" }, exp[0].Types);
            Assert.True(exp[0].Tag == true);
        }

        [Fact]
        public void EnglishInput_IsLeftUntouched()
        {
            var a = new Dictionary<string, object> { ["code"] = "x", ["mode"] = "trial", ["key"] = "k", ["red"] = 1L };
            var before = Json.Write(a);
            LegacyAliases.NormalizeArgs(Wire.OpExec, a);
            Assert.Equal(before, Json.Write(a));
        }

        [Fact]
        public void PersistedJournal_WithSpanishKeysAndStatuses_IsReadAndRetryIsNotAConflict()
        {
            var dir = NewDir("journal");
            var path = Path.Combine(dir, "operations.jsonl");
            var args = new Dictionary<string, object> { ["code"] = "return 1;", ["mode"] = "run" };
            // What 0.9.3 wrote for the same operation: Spanish keys, Spanish status and the legacy fingerprint.
            var legacyHash = OperationJournal.LegacyHashArgs(Wire.OpExec, args);
            Assert.NotEqual(OperationJournal.HashArgs(Wire.OpExec, args), legacyHash);
            File.WriteAllText(path,
                "{\"ev\":\"inicio\",\"t\":\"2026-01-01T00:00:00Z\",\"clave\":\"k1\",\"doc\":\"d\",\"op\":\"exec\",\"hash\":\"" + legacyHash + "\",\"sesion\":\"s\"}\n" +
                "{\"ev\":\"fin\",\"t\":\"2026-01-01T00:00:01Z\",\"clave\":\"k1\",\"doc\":\"d\",\"estado\":\"ok\",\"resultado\":{\"estado\":\"retenido\",\"datos\":{\"x\":1}}}\n");
            var j = new OperationJournal(path);
            var r = j.Get("k1", "d");
            Assert.NotNull(r);
            Assert.Equal(Status.Ok, r.State);
            var payload = (Dictionary<string, object>)r.Result;
            Assert.Equal(Status.Held, payload["status"]);
            Assert.Equal(1L, ((Dictionary<string, object>)payload["data"])["x"]);
            // The same key and operation after the upgrade is recognized as already done, not a different operation.
            // Real requests carry the control args (key, task): 0.9.4 stripped them under their old names, so they never enter the legacy fingerprint.
            var withKey = new Dictionary<string, object>(args) { [Wire.KeyArg] = "k1", [Wire.TaskArg] = "t1" };
            Assert.Equal(legacyHash, OperationJournal.LegacyHashArgs(Wire.OpExec, withKey));
            var b = j.Begin("k1", "d", Wire.OpExec, OperationJournal.HashArgs(Wire.OpExec, withKey), null, "s2", OperationJournal.ReconcileMode.None,
                legacyHash: OperationJournal.LegacyHashArgs(Wire.OpExec, withKey));
            Assert.Equal(OperationJournal.BeginKind.Completed, b.Kind);
            // A really different operation with the same key still conflicts.
            var other = new Dictionary<string, object> { ["code"] = "return 2;", ["mode"] = "run" };
            var c = j.Begin("k1", "d", Wire.OpExec, OperationJournal.HashArgs(Wire.OpExec, other), null, "s2", OperationJournal.ReconcileMode.None,
                legacyHash: OperationJournal.LegacyHashArgs(Wire.OpExec, other));
            Assert.Equal(OperationJournal.BeginKind.Mismatch, c.Kind);
            // New lines are written in English.
            j.Complete("k2", "d", Status.Ok, null);
            Assert.Contains("\"status\":\"ok\"", File.ReadAllLines(path).Last());
        }

        [Fact]
        public void AuditChain_WrittenWithSpanishKeys_VerifiesOverStoredBytes_AndContinues()
        {
            var dir = NewDir("audit");
            var path = Path.Combine(dir, "audit.jsonl");
            // A two-line chain as 0.9.3 wrote it (Spanish keys); prev = sha256 of the previous stored line.
            var l1 = "{\"seq\":1,\"t\":\"2026-01-01T00:00:00.000Z\",\"prev\":\"\",\"agente\":\"a\",\"op\":\"commit\",\"estado\":\"intencion\",\"intento_id\":\"x\"}";
            var l2 = "{\"seq\":2,\"t\":\"2026-01-01T00:00:01.000Z\",\"prev\":\"" + Home.Sha256Hex(l1) + "\",\"agente\":\"a\",\"op\":\"commit\",\"estado\":\"ok\",\"intento_id\":\"x\"}";
            File.WriteAllText(path, l1 + "\n" + l2 + "\n");
            var v = AuditLog.Verify(path);
            Assert.Empty(v.Errors);
            Assert.Equal(2, v.Lines);
            var log = new AuditLog(path);
            log.Append("b", "commit", Status.Ok, new Dictionary<string, object>());
            var lines = File.ReadAllLines(path);
            Assert.Equal(l1, lines[0]);                       // stored bytes never rewritten
            Assert.Equal(l2, lines[1]);
            Assert.Contains("\"prev\":\"" + Home.Sha256Hex(l2) + "\"", lines[2]);
            Assert.Empty(AuditLog.Verify(path).Errors);
        }

        [Fact]
        public void LegacyGrant_SignedOverSpanishKeys_IsStillValid()
        {
            var dir = NewDir("grants");
            var store = new GrantStore(dir);
            var g = store.Approve("t1", "archivo:c:\\m.rvt", "", new[] { Capability.Modify }, TimeSpan.FromHours(1), "person", DateTime.UtcNow);
            // Rewrite the grant as 0.9.3 stored it: Spanish keys and capability names, signed over that content with the same key.
            var file = Path.Combine(dir, "c_" + g.Id + ".json");
            var legacy = new Dictionary<string, object>
            {
                ["id"] = g.Id, ["tarea"] = g.Task, ["doc"] = g.DocKey, ["huella_doc"] = "", ["capacidades"] = new List<object> { "revit_modificar" },
                ["ambito"] = new List<object>(), ["creada"] = g.Created, ["vence"] = g.Expires, ["aprobada_por"] = g.ApprovedBy, ["revocada"] = "",
            };
            var key = GrantStore.LoadOrCreateKey(LegacyAliases.ResolveExisting(dir, Path.Combine(dir, "key.bin")));
            using (var h = new System.Security.Cryptography.HMACSHA256(key))
                legacy["firma"] = BitConverter.ToString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(OpSemantics.Canonical(legacy)))).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(file, Json.Write(legacy));
            Assert.Equal(Capability.Modify, "revit_modify");
            Assert.Null(store.Check("t1", "archivo:c:\\m.rvt", "", Capability.Modify, DateTime.UtcNow));
            // Tampering is still detected.
            File.WriteAllText(file, Json.Write(legacy).Replace("\"t1\"", "\"t2\""));
            Assert.NotNull(store.Check("t2", "archivo:c:\\m.rvt", "", Capability.Modify, DateTime.UtcNow));
        }

        [Fact]
        public void LegacyTaskFolderAndRecord_AreReadInPlace()
        {
            var home = NewDir("tasks");
            var legacyDir = Path.Combine(home, "tareas", "T1");
            Directory.CreateDirectory(legacyDir);
            var t = new Bimbridge.Engine.Tasks.TaskRecord { Id = "T1", Objective = "goal" };
            var json = Json.Write(t.ToJson());
            // Same record with the 0.9.3 key names.
            foreach (var kv in new Dictionary<string, string> { ["\"status\""] = "\"estado\"", ["\"target\""] = "\"objetivo\"", ["\"" + t.State + "\""] = "\"preparada\"" })
                json = json.Replace(kv.Key, kv.Value);
            File.WriteAllText(Path.Combine(legacyDir, "tarea.json"), json);
            var store = new Bimbridge.Engine.Tasks.TaskStore(LegacyAliases.ResolveExisting(home, Path.Combine(home, "tasks")));
            Assert.True(store.Exists("T1"));
            var back = store.Load("T1");
            Assert.Equal("goal", back.Objective);
            Assert.Equal(t.State, back.State);
        }

        [Fact]
        public void LegacyConfig_KeysAndValues_AreRead_EnglishWins()
        {
            var dir = NewDir("config");
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{\"levels\":\"RPW\",\"runner\":\"consulta\",\"perfil\":\"laboratorio\",\"laboratorio\":{\"carpetas\":[\"C:\\\\lab\"],\"actualizacion_sin_firma\":true}," +
                                    "\"permisos\":[\"leer\",\"modificar_proyecto\"],\"carpetas_familias\":[\"C:\\\\fam\"],\"bloquear_inyeccion_csharp\":true,\"identidad_obligatoria\":false}");
            var c = EngineConfig.Load(path);
            Assert.Equal("query", c.Runner);
            Assert.True(c.Profile.IsLab);
            Assert.Contains(@"C:\fam", c.FamilyFolders);
            Assert.Contains("read", c.PermissionList);
            Assert.Contains("modify_project", c.PermissionList);
            Assert.True(c.BlockDynamicCode);
            Assert.False(c.IdentityRequired);
            // The file itself is never rewritten to migrate it.
            Assert.Contains("\"perfil\"", File.ReadAllText(path));
            // When both spellings are present, English wins.
            File.WriteAllText(path, "{\"runner\":\"apagado\",\"runner_x\":1,\"bloquear_inyeccion_csharp\":true,\"block_csharp_injection\":false}");
            Assert.False(EngineConfig.Load(path).BlockDynamicCode);
        }

        [Fact]
        public void HomeFallback_UsesLegacyFolder_UntilTheEnglishOneExists()
        {
            var home = NewDir("home");
            Directory.CreateDirectory(Path.Combine(home, "operaciones"));
            File.WriteAllText(Path.Combine(home, "operaciones", "operaciones.jsonl"), "");
            Assert.Equal(Path.Combine(home, "operaciones", "operaciones.jsonl"),
                LegacyAliases.ResolveExisting(home, Path.Combine(home, "operations", "operations.jsonl")));
            Directory.CreateDirectory(Path.Combine(home, "operations"));
            Assert.Equal(Path.Combine(home, "operations", "operations.jsonl"),
                LegacyAliases.ResolveExisting(home, Path.Combine(home, "operations", "operations.jsonl")));
        }
    }
}
