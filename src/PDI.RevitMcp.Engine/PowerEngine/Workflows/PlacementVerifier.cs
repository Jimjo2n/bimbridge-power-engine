// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Engine.Workflow;

namespace BimBridge.PowerEngine.Workflows
{
    public sealed class ExpectedFixture
    {
        public string Key, Code, FamilyType, Host;
        public double X, Y;
        public double? RotDeg, ElevFt;
    }

    public sealed class ObservedFixture
    {
        public long Id;
        public string FamilyType, Host;
        public double X, Y, RotDeg;
        public double? ElevFt;
        public bool? Visible;              // visible en la vista de entrega (null = no comprobado)
        public bool? SupportOk;            // contacto con el soporte comprobado por geo (null = no comprobado)
    }

    public sealed class PlacementVerifyOptions
    {
        public double PositionTolFt = 0.5 / 12.0;   // 1/2"
        public double SearchRadiusFt = 3.0;         // más allá: omitido/adicional; dentro pero fuera de tolerancia: desplazado
        public double RotationTolDeg = 1.0;
        public double ElevTolFt = 1.0 / 96.0;       // 1/8"
        public double DuplicateTolFt = 0.25;
        public bool RotationSymmetric180 = true;    // una tira girada 180° es la misma tira
        public int MaxListed = 50;                  // el informe compacto lista como mucho esto por tipo de diferencia
    }

    /// <summary>
    /// Verificación independiente de una colocación: compara lo esperado (de una fuente INDEPENDIENTE de la que guió la construcción,
    /// comprobado con SHA-256 y linaje por IndependentVerifier.CheckIndependence) con lo observado en el modelo, y clasifica:
    /// omitido, adicional, duplicado, tipo incorrecto, desplazado, giro incorrecto, altura incorrecta, soporte inválido, no visible.
    /// Informe compacto: conteos + como mucho MaxListed casos por clase.
    /// </summary>
    public static class PlacementVerifier
    {
        public static Dictionary<string, object> Verify(SourceRef expectedSource, IList<SourceRef> buildSources, string buildPlanFingerprint,
                                                        IList<ExpectedFixture> expected, IList<ObservedFixture> observed, PlacementVerifyOptions o = null, PowerEngineDispatcher engine = null)
        {
            o = o ?? new PlacementVerifyOptions();
            var ind = new IndependentVerifier.Input
            {
                ExpectedSource = expectedSource, BuildSources = (buildSources ?? new List<SourceRef>()).ToList(), BuildPlanFingerprint = buildPlanFingerprint,
                Expected = expected.Select(e => new IndependentVerifier.Item { Key = e.Key, X = e.X, Y = e.Y, Attrs = { ["tipo"] = e.FamilyType ?? "" } }).ToList(),
            };
            var gate = IndependentVerifier.CheckIndependence(ind, out var proof);
            if (gate.HasValue)
                return new Dictionary<string, object> { ["estado"] = gate.Value.Estado, ["motivo"] = gate.Value.Motivo, ["nota"] = "sin fuente independiente no se emite un veredicto de QA/QC" };

            var diffs = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.Ordinal);
            void Add(string kind, Dictionary<string, object> d) { if (!diffs.TryGetValue(kind, out var l)) diffs[kind] = l = new List<Dictionary<string, object>>(); l.Add(d); }

            // duplicados en lo observado (mismo tipo, muy cerca)
            var opts = new double[observed.Count * 2];
            for (var i = 0; i < observed.Count; i++) { opts[2 * i] = observed[i].X; opts[2 * i + 1] = observed[i].Y; }
            var dg = observed.Count > 1 ? (engine != null ? engine.Duplicates(opts, 2, o.DuplicateTolFt) : GeometryKernels.Duplicates(opts, 2, o.DuplicateTolFt)) : new int[observed.Count];
            var dupOf = new HashSet<int>();
            foreach (var g in Enumerable.Range(0, observed.Count).GroupBy(i => dg[i]).Where(g => g.Count() > 1))
                foreach (var sub in g.GroupBy(i => observed[i].FamilyType ?? "").Where(s => s.Count() > 1))
                {
                    var keep = sub.OrderBy(i => observed[i].Id).First();
                    foreach (var i in sub.Where(i => i != keep)) { dupOf.Add(i); Add("duplicado", new Dictionary<string, object> { ["id"] = observed[i].Id, ["de"] = observed[keep].Id, ["tipo"] = observed[i].FamilyType }); }
                }
            var obsIdx = Enumerable.Range(0, observed.Count).Where(i => !dupOf.Contains(i)).ToList();

            // emparejamiento 1-a-1 dentro del radio de búsqueda (voraz global determinista; C# o Rust)
            var e = new double[expected.Count * 2];
            for (var i = 0; i < expected.Count; i++) { e[2 * i] = expected[i].X; e[2 * i + 1] = expected[i].Y; }
            var ob = new double[obsIdx.Count * 2];
            for (var k = 0; k < obsIdx.Count; k++) { ob[2 * k] = observed[obsIdx[k]].X; ob[2 * k + 1] = observed[obsIdx[k]].Y; }
            var pair = engine != null ? engine.Match(e, ob, 2, o.SearchRadiusFt) : GeometryKernels.Match(e, ob, 2, o.SearchRadiusFt);
            var matched = new HashSet<int>();
            var okCount = 0;
            for (var i = 0; i < expected.Count; i++)
            {
                var x = expected[i];
                if (pair[i] < 0) { Add("omitido", new Dictionary<string, object> { ["clave"] = x.Key, ["codigo"] = x.Code, ["xy_ft"] = XY(x.X, x.Y) }); continue; }
                var oi = obsIdx[pair[i]]; matched.Add(oi);
                var y = observed[oi];
                var bad = false;
                var d = Math.Sqrt((x.X - y.X) * (x.X - y.X) + (x.Y - y.Y) * (x.Y - y.Y));
                if (d > o.PositionTolFt) { bad = true; Add("desplazado", new Dictionary<string, object> { ["clave"] = x.Key, ["id"] = y.Id, ["distancia_ft"] = Math.Round(d, 4), ["xy_ft"] = XY(x.X, x.Y) }); }
                if (x.FamilyType != null && !string.Equals(x.FamilyType, y.FamilyType, StringComparison.Ordinal))
                { bad = true; Add("tipo_incorrecto", new Dictionary<string, object> { ["clave"] = x.Key, ["id"] = y.Id, ["esperado"] = x.FamilyType, ["observado"] = y.FamilyType }); }
                if (x.RotDeg.HasValue)
                {
                    var dr = AngleDiff(x.RotDeg.Value, y.RotDeg, o.RotationSymmetric180);
                    if (dr > o.RotationTolDeg) { bad = true; Add("giro_incorrecto", new Dictionary<string, object> { ["clave"] = x.Key, ["id"] = y.Id, ["esperado"] = x.RotDeg.Value, ["observado"] = Math.Round(y.RotDeg, 3) }); }
                }
                if (x.ElevFt.HasValue && (!y.ElevFt.HasValue || Math.Abs(x.ElevFt.Value - y.ElevFt.Value) > o.ElevTolFt))
                { bad = true; Add("altura_incorrecta", new Dictionary<string, object> { ["clave"] = x.Key, ["id"] = y.Id, ["esperado_ft"] = x.ElevFt.Value, ["observado_ft"] = y.ElevFt }); }
                if (x.Host != null && !string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase))
                { bad = true; Add("soporte_invalido", new Dictionary<string, object> { ["clave"] = x.Key, ["id"] = y.Id, ["esperado"] = x.Host, ["observado"] = y.Host }); }
                else if (y.SupportOk == false) { bad = true; Add("soporte_invalido", new Dictionary<string, object> { ["clave"] = x.Key, ["id"] = y.Id, ["motivo"] = "sin contacto con el soporte (geo montaje)" }); }
                if (y.Visible == false) { bad = true; Add("no_visible", new Dictionary<string, object> { ["clave"] = x.Key, ["id"] = y.Id }); }
                if (!bad) okCount++;
            }
            foreach (var oi in obsIdx.Where(i => !matched.Contains(i)))
                Add("adicional", new Dictionary<string, object> { ["id"] = observed[oi].Id, ["tipo"] = observed[oi].FamilyType, ["xy_ft"] = XY(observed[oi].X, observed[oi].Y) });

            var notChecked = new List<object>();
            if (observed.Any(y => y.Visible == null)) notChecked.Add("visibilidad (sin dato de vista)");
            if (observed.Any(y => y.SupportOk == null) && expected.All(x => x.Host == null)) notChecked.Add("soporte físico (sin geo montaje)");
            if (expected.All(x => x.ElevFt == null)) notChecked.Add("altura (la fuente no la da)");
            var total = diffs.Values.Sum(l => l.Count);
            return new Dictionary<string, object>
            {
                ["estado"] = total == 0 ? "conforme" : "diferencias",
                ["independencia"] = proof,
                ["resumen"] = new Dictionary<string, object>
                {
                    ["esperados"] = (long)expected.Count, ["observados"] = (long)observed.Count, ["correctos"] = (long)okCount,
                    ["diferencias"] = diffs.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => (object)(long)k.Value.Count),
                },
                ["diferencias"] = diffs.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => (object)k.Value.Take(o.MaxListed).Cast<object>().ToList()),
                ["no_comprobado"] = notChecked,
            };
        }

        public static double AngleDiff(double a, double b, bool sym180)
        {
            var d = Math.Abs(a - b) % 360; if (d > 180) d = 360 - d;
            if (sym180 && d > 90) d = 180 - d;
            return d;
        }

        private static List<object> XY(double x, double y) => new List<object> { Math.Round(x, 4), Math.Round(y, 4) };
    }
}
