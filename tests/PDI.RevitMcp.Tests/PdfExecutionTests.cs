// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using BimBridge.PowerEngine.Workflows;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// 0.9.1 (Bloque I): puente ejecutable PDF→Revit. El plan de colocación sólo se convierte en planes tipados cuando es seguro:
    /// registro válido, operaciones que pasan la validación offline, clave de idempotencia estable y lo ambiguo fuera.
    /// </summary>
    public class PdfExecutionTests
    {
        private static Dictionary<string, object> Batch(string type, params double[][] pts) => new Dictionary<string, object>
        {
            ["operacion"] = new Dictionary<string, object>
            {
                ["op"] = "place", ["type"] = type, ["level"] = "LEVEL 02", ["elev"] = 9.25,
                ["puntos"] = pts.Select(p => (object)new List<object> { p[0], p[1] }).ToList(),
            },
            ["items"] = new List<object>(),
        };

        private static PlacementPlan Plan(params Dictionary<string, object>[] batches)
        {
            var p = new PlacementPlan { Registration = new RegistrationResult(), Fingerprint = "huella-1" };
            p.Source["id"] = "E-201@B#p1";
            p.Batches.AddRange(batches);
            foreach (var b in batches)
                foreach (var _ in (List<object>)((Dictionary<string, object>)b["operacion"])["puntos"]) p.Place.Add(new PlacementItem());
            return p;
        }

        [Fact]
        public void ReadyPlan_BecomesTypedPlansWithStableIdempotencyKeys()
        {
            var plan = Plan(Batch("F: A", new[] { 1.0, 2 }, new[] { 3.0, 4 }), Batch("F: B", new[] { 5.0, 6 }));
            var e = PdfToPlan.Execution(plan);
            Assert.Equal(true, e["ejecutable"]);
            var one = (Dictionary<string, object>)((List<object>)e["planes"]).Single();
            Assert.Equal(3L, one["elementos"]);
            // Lo devuelto es directamente aceptable por la herramienta plan (validación tipada offline).
            var parsed = OpSpec.ParseAll(new Dictionary<string, object> { ["ops"] = one["ops"] });
            Assert.Equal(new[] { "place", "place" }, parsed.Select(o => o.Kind).ToArray());
            // Misma entrada → misma clave (repetir no coloca dos veces); otro contenido u otra huella → otra clave.
            var key = (string)one[Wire.KeyArg];
            Assert.StartsWith("pdf-", key);
            Assert.Equal(key, ((Dictionary<string, object>)((List<object>)PdfToPlan.Execution(Plan(Batch("F: A", new[] { 1.0, 2 }, new[] { 3.0, 4 }), Batch("F: B", new[] { 5.0, 6 })))["planes"])[0])[Wire.KeyArg]);
            var moved = (Dictionary<string, object>)((List<object>)PdfToPlan.Execution(Plan(Batch("F: A", new[] { 1.0, 2 }, new[] { 3.0, 4.5 }), Batch("F: B", new[] { 5.0, 6 })))["planes"])[0];
            Assert.NotEqual(key, moved[Wire.KeyArg]);
            var other = Plan(Batch("F: A", new[] { 1.0, 2 }, new[] { 3.0, 4 }), Batch("F: B", new[] { 5.0, 6 })); other.Fingerprint = "huella-2";
            Assert.NotEqual(key, ((Dictionary<string, object>)((List<object>)PdfToPlan.Execution(other)["planes"])[0])[Wire.KeyArg]);
        }

        [Fact]
        public void UnsafePlans_AreNotExecutable_AndSayWhy()
        {
            Assert.Equal(false, PdfToPlan.Execution(null)["ejecutable"]);
            var noReg = Plan(Batch("F: A", new[] { 1.0, 2 })); noReg.Registration = new RegistrationResult { Estado = "pocas_anclas" };
            Assert.Contains("registro", string.Join(" ", (List<object>)PdfToPlan.Execution(noReg)["motivos"]));
            var state = Plan(Batch("F: A", new[] { 1.0, 2 })); state.Estado = "sin_nivel";
            Assert.Equal(false, PdfToPlan.Execution(state)["ejecutable"]);
            var onlyAmb = Plan(); onlyAmb.Estado = "solo_ambiguedades"; onlyAmb.Ambiguous.Add(new PlacementItem());
            var ea = PdfToPlan.Execution(onlyAmb);
            Assert.Equal(false, ea["ejecutable"]); Assert.Equal(1L, ea["pendiente_de_persona"]);
            Assert.False(ea.ContainsKey("planes"));
            // Una operación que la validación tipada rechazaría (sin puntos, o que no es place) bloquea todo el plan.
            var bad = Plan(Batch("F: A")); bad.Place.Add(new PlacementItem());
            Assert.Equal(false, PdfToPlan.Execution(bad)["ejecutable"]);
            var notPlace = Plan(Batch("F: A", new[] { 1.0, 2 }));
            ((Dictionary<string, object>)notPlace.Batches[0]["operacion"])["op"] = "delete";
            Assert.Contains("no es una operación place", string.Join(" ", (List<object>)PdfToPlan.Execution(notPlace)["motivos"]));
            var noPrint = Plan(Batch("F: A", new[] { 1.0, 2 })); noPrint.Fingerprint = null;
            Assert.Equal(false, PdfToPlan.Execution(noPrint)["ejecutable"]);
        }

        [Fact]
        public void AmbiguousSymbolsAreLeftOut_AndLargePlansAreSplit()
        {
            var plan = Plan(Enumerable.Range(0, OpSpec.MaxOps + 5).Select(i => Batch("F: T" + i, new[] { (double)i, 0 })).ToArray());
            plan.Estado = "listo_con_ambiguedades";
            plan.Ambiguous.Add(new PlacementItem()); plan.Ambiguous.Add(new PlacementItem());
            var e = PdfToPlan.Execution(plan);
            Assert.Equal(true, e["ejecutable"]);
            Assert.Equal(2L, e["pendiente_de_persona"]);
            Assert.Contains("NO se colocan", (string)e["aviso"]);
            var plans = ((List<object>)e["planes"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(2, plans.Count);
            Assert.Equal(OpSpec.MaxOps, ((List<object>)plans[0]["ops"]).Count);
            Assert.Equal(5, ((List<object>)plans[1]["ops"]).Count);
            Assert.NotEqual(plans[0][Wire.KeyArg], plans[1][Wire.KeyArg]);
            foreach (var p in plans) OpSpec.ParseAll(new Dictionary<string, object> { ["ops"] = p["ops"] });
        }
    }
}
