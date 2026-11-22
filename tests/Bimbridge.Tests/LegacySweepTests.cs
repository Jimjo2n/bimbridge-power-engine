// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Engine.Tasks;
using Bimbridge.Engine.Workflow;
using Bimbridge.Protocol;
using BimBridge.PowerEngine;
using BimBridge.PowerEngine.Workflows;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// Regressions found by the 0.9.4 legacy sweep. The Spanish strings are intentional test data: what 0.9.3/0.9.4 sent or stored.
    /// </summary>
    public class LegacySweepTests
    {
        private static string NewDir(string tag) { var d = Path.Combine(Path.GetTempPath(), "bb-sweep-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }

        // ---- persisted records: values, not only keys, are read in English ----

        [Fact]
        public void AuditLog_OldQueueApplyIntentAndQuarantine_AreFound()
        {
            var path = Path.Combine(NewDir("audit"), "audit.jsonl");
            File.WriteAllLines(path, new[]
            {
                "{\"seq\":1,\"t\":\"2026-01-01T00:00:00Z\",\"op\":\"aplicar_cola\",\"estado\":\"intencion\",\"intencion\":\"I1\",\"intento_id\":\"A1\",\"objetivo\":\"D\",\"huella_doc\":\"P\",\"intento_sesion\":\"S\",\"plan_id\":\"p1\"}",
                "{\"seq\":2,\"t\":\"2026-01-01T00:00:01Z\",\"op\":\"aplicar_cola\",\"estado\":\"ok\",\"intencion\":\"I1\",\"fase\":\"resultado\",\"rev\":5}",
                "{\"seq\":3,\"t\":\"2026-01-01T00:00:02Z\",\"op\":\"cuarentena\",\"estado\":\"cadena_nueva\",\"apartado\":\"x\",\"intenciones_pendientes\":[{\"intencion\":\"I9\",\"estado\":\"intencion\"}]}",
            });
            var receipt = AuditLog.FindCommitReceipt(path, "A1", "S", null, "D", "P");
            Assert.NotNull(receipt);
            Assert.Equal("ok", receipt["status"]);
            Assert.Equal("p1", receipt["plan_id"]);
            Assert.Equal("I9", Arg.GetString(Assert.Single(AuditLog.PendingIntents(path)), "intent"));
        }

        [Fact]
        public void TaskPolicy_StoredWithLegacyValues_IsReadInEnglish()
        {
            var t = TaskRecord.FromJson(LegacyAliases.NormalizeRecord(Json.ParseObject(
                "{\"id\":\"T1\",\"politica\":{\"en_error\":\"continuar\",\"al_final\":\"guardar,sync\",\"tx\":\"ninguna\",\"avisos\":\"dejar\"}}")));
            Assert.Equal("continue", t.PolicyString("on_error"));
            Assert.Equal("save,sync", t.PolicyString("at_end"));
            Assert.Equal("none", t.PolicyString("tx"));
            Assert.Equal("leave", t.PolicyString("warnings"));
        }

        [Fact]
        public void HeldPlanEntries_ParameterNamesStay_LegacyEngineValuesAreMapped()
        {
            PlanEntry E(string json) => PlanEntry.FromJson(LegacyAliases.NormalizeRecord(Json.ParseObject(json)));
            // A parameter name is the user's: never translated, even when it is a legacy token.
            Assert.Equal("tipo", E("{\"id\":1,\"op\":\"set_param\",\"key\":\"tipo\",\"before\":\"a\",\"after\":\"b\"}").Key);
            Assert.Equal("zona", E("{\"id\":1,\"op\":\"set_type_param\",\"key\":\"zona\"}").Key);
            Assert.Equal("altura", E("{\"id\":1,\"op\":\"set_values\",\"key\":\"altura\"}").Key);
            Assert.Equal("type", E("{\"id\":1,\"op\":\"set_values\",\"key\":\"tipo\"}").Key);           // the 0.9.3 type entry
            // view_set values stored by 0.9.3.
            var detail = E("{\"id\":1,\"op\":\"view_set\",\"key\":\"detalle\",\"before\":\"medio\",\"after\":\"fino\",\"target\":\"bajo\"}");
            Assert.Equal("detail", detail.Key);
            Assert.Equal("medium", detail.Before);
            Assert.Equal("fine", detail.After);
            Assert.Equal("coarse", detail.Target);
            var template = E("{\"id\":1,\"op\":\"view_set\",\"key\":\"plantilla\",\"before\":\"ninguna\",\"after\":\"My Template\",\"target\":5}");
            Assert.Equal("none", template.Before);
            Assert.Equal("My Template", template.After);
        }

        [Fact]
        public void GroupSnapshot_FromOldRun_KeepsItsSignatures()
        {
            var before = new List<object> { Json.ParseObject("{\"id\":1,\"uid\":\"u1\",\"firma\":\"S1\"}") };
            var after = new List<object> { Json.ParseObject("{\"id\":2,\"uid\":\"u1\",\"signature\":\"S1\"}") };
            Assert.NotNull(GroupMemberMap.Compare(before, after));
            Assert.Equal("S1", ((Dictionary<string, object>)before[0])["signature"]);
        }

        [Fact]
        public void LegacyCalibration_And_PendingLabProfile_And_OldLeftovers_AreRead()
        {
            var dir = NewDir("cal");
            Directory.CreateDirectory(Path.Combine(dir, "power_engine"));
            File.WriteAllText(Path.Combine(dir, "power_engine", "calibracion.json"), "{\"fuente\":\"medido\",\"rust_ffi_desde\":{\"nearest\":7}}");
            var cal = PowerCommands.OptionsFrom(new EngineConfig(), dir).Calibration;
            Assert.Equal("medido", cal.Source);
            Assert.Equal(7, cal.For("nearest"));

            var p = Bimbridge.Setup.PendingUpdate.FromJson(LegacyAliases.NormalizeRecord(Json.ParseObject("{\"id\":\"x\",\"perfil\":\"laboratorio\"}")));
            Assert.Equal(ProfilePolicy.Lab, p.Profile);

            Assert.True(Bimbridge.Setup.Updates.IsLeftover("Bimbridge.Engine.dll.viejo-638000000000000000"));
            Assert.True(Bimbridge.Setup.Updates.IsLeftover("Bimbridge.Engine.dll.old-1"));
            Assert.False(Bimbridge.Setup.Updates.IsLeftover("Bimbridge.Engine.dll"));
        }

        // ---- input: enum-like values of renamed keys ----

        [Fact]
        public void LegacyEnumValues_OfRenamedKeys_AreMapped()
        {
            var a = new Dictionary<string, object>
            {
                ["tags"] = "ninguno", ["precedencia"] = "acuerdo", ["avisos"] = "dejar",
                ["ops"] = new List<object>
                {
                    new Dictionary<string, object> { ["op"] = "place", ["montaje"] = "losa" },
                    new Dictionary<string, object> { ["op"] = "set_level", ["mantener"] = "posicion" },
                    new Dictionary<string, object> { ["op"] = "group_variant", ["reglas"] = new Dictionary<string, object> { ["doble"] = "LED 2x4" } },
                },
            };
            LegacyAliases.NormalizeArgs(Wire.OpPlan, a);
            Assert.Equal("none", a["tags"]);
            Assert.Equal("agreement", a["precedence"]);
            Assert.Equal("leave", a["warnings"]);
            var ops = ((List<object>)a["ops"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal("slab", ops[0]["mounting"]);
            Assert.Equal("position", ops[1]["keep"]);
            // group_variant rules are keyed by the user's fixture type names: never translated.
            Assert.Equal("LED 2x4", ((Dictionary<string, object>)ops[2]["rules"])["doble"]);

            Assert.NotNull(LightingAudit.Summarize(new List<LightingAudit.Fixture>(), new List<LightingAudit.Tag>(), new List<LightingAudit.ViewCount>(), out _, tagRule: "ninguno"));

            var g = Bimbridge.Engine.Geo.GeoSpec.Parse(new Dictionary<string, object> { ["name"] = "mounting", ["source"] = "cuartos", ["solo"] = "todos" });
            Assert.Equal("rooms", g.Source);
            Assert.Equal("all", g.Only);
        }

        [Fact]
        public void PowerNestedRows_LegacyKeysRenamed_UserValuesUntouched()
        {
            var a = Json.ParseObject("{\"reglas\":[{\"ambito\":\"A\",\"codigo\":\"P1\",\"clase\":\"tablero\",\"tipo\":\"Tablero Tipo\"}]," +
                                     "\"anclas\":[{\"nombre\":\"A/1\",\"pdf\":[0,0],\"modelo\":[0,0]}],\"columnas\":{\"id\":\"ID\",\"origen\":\"Origen\",\"destino\":\"Destino\"}," +
                                     "\"decisiones\":[{\"id_simbolo\":\"s1\",\"decision\":\"incluir\"}],\"include\":[\"texto\"]}");
            LegacyAliases.NormalizeArgs(null, a);
            foreach (var k in new[] { "anchors", "items", "grids", "existing", "pdf_edges", "columns", "expected" }) LegacyAliases.NormalizeRows(a, k);
            LegacyAliases.NormalizeRows(a, "rules", "class", "mounting");
            LegacyAliases.NormalizeRows(a, "decisions", "decision");
            LegacyAliases.NormalizeTokenList(a, "include");
            var rule = (Dictionary<string, object>)((List<object>)a["rules"])[0];
            Assert.Equal("A", rule["scope"]);
            Assert.Equal("P1", rule["code"]);
            Assert.Equal("panel", rule["class"]);
            Assert.Equal("Tablero Tipo", rule["type"]);                       // a family type name is the user's
            Assert.Equal("A/1", ((Dictionary<string, object>)((List<object>)a["anchors"])[0])["name"]);
            var columns = (Dictionary<string, object>)a["columns"];
            Assert.Equal("Origen", columns["origin"]);                        // header names are the user's
            Assert.Equal("Destino", columns["target"]);
            var decision = (Dictionary<string, object>)((List<object>)a["decisions"])[0];
            Assert.Equal("s1", decision["symbol_id"]);
            Assert.Equal("include", decision["decision"]);
            Assert.Equal("text", ((List<object>)a["include"])[0]);
        }

        // ---- hashes over renamed literals keep their 0.9.4 value ----

        [Fact]
        public void SourceBoundKey_IsTheOneComputedBy094_ForTheSameBatch()
        {
            var token = SourceBinding.Token("pdf", "a.pdf#p1", "sha", null, "", "fp", 1);
            List<object> Pts() => new List<object> { new List<object> { 1.5, 2.0 } };
            // What 0.9.4 emitted and hashed.
            var legacy = new Dictionary<string, object>
            {
                ["op"] = "place", ["type"] = "T", ["level"] = "L1", ["puntos"] = Pts(), ["items_fuente"] = new List<object> { "s1" }, ["montaje"] = "losa", ["origen"] = token,
            };
            var expected = "src-" + Home.Sha256Hex(token + "\n" + OpSemantics.Canonical(legacy), 32).Substring(0, 24);
            var english = new Dictionary<string, object>
            {
                ["op"] = "place", ["type"] = "T", ["level"] = "L1", ["points"] = Pts(), ["source_items"] = new List<object> { "s1" }, ["mounting"] = "slab", [SourceBinding.OpField] = token,
            };
            Assert.Equal(expected, SourceBinding.Resolve(new List<object> { english }).Key);
            var oldOp = new Dictionary<string, object>(legacy);
            LegacyAliases.NormalizeOp(oldOp);
            Assert.Equal(expected, SourceBinding.Resolve(new List<object> { oldOp }).Key);

            // The plan fingerprint (part of the token) keeps the 0.9.4 attribute name, and the verifier agrees with it.
            Assert.Equal(IndependentVerifier.Fingerprint(new[] { new IndependentVerifier.Item { Key = "a", X = 1, Y = 2, Attrs = { ["tipo"] = "T" } } }),
                PlacementPlanner.Fingerprint(new[] { new PlacementItem { Id = "a", X = 1, Y = 2, FamilyType = "T" } }));
        }
    }
}
