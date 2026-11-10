// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PDI.RevitMcp.Engine.Workflow
{
    /// <summary>
    /// 0.8.0 (CAP-007/CAP-008, regla del informe WF-11): verificación independiente
    /// <c>fuente independiente → resultado esperado → modelo real → diferencias</c>.
    /// Se NIEGA a dar por buena una verificación cuyo "esperado" sale del mismo inventario o plan que guió la construcción:
    /// eso solo prueba coherencia interna, y fue exactamente lo que ocultó omisiones en la auditoría del riser.
    /// </summary>
    public static class IndependentVerifier
    {
        public sealed class Item
        {
            public string Key;                 // identificador lógico (código + ubicación, id de equipo, nombre de lámina…); null = emparejar por posición
            public long ElementId;             // solo en lo observado
            public double? X, Y;               // pies internos, solo para emparejar por posición
            public Dictionary<string, string> Attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public sealed class Input
        {
            public SourceRef ExpectedSource;   // de dónde sale lo esperado
            public string BuildSourceId;       // fuente/plan que se usó para CONSTRUIR (compatibilidad 0.8.0; equivale a BuildSources con sólo id)
            /// <summary>0.9.0: TODAS las fuentes y planes que guiaron la construcción, con su SHA-256 y linaje.</summary>
            public List<SourceRef> BuildSources = new List<SourceRef>();
            /// <summary>0.9.0: huella (<see cref="Fingerprint"/>) del plan de ejecución; si lo esperado es idéntico, es una copia del plan.</summary>
            public string BuildPlanFingerprint;
            public string RequiredRevision;    // revisión que debe tener la fuente esperada (null = no se exige)
            public List<Item> Expected = new List<Item>();
            public List<Item> Observed = new List<Item>();
            public double PositionTolerance = 0.5 / 12.0;   // 1/2" por defecto, en pies
            public string[] CompareAttrs = new string[0];
            /// <summary>Excepciones justificadas: clave → motivo con fuente. No se cuentan como diferencia, pero se informan.</summary>
            public Dictionary<string, string> Exceptions = new Dictionary<string, string>(StringComparer.Ordinal);
            /// <summary>Claves que pueden repetirse legítimamente (p. ej. un ID de feeder válido en dos tramos).</summary>
            public HashSet<string> AllowedDuplicates = new HashSet<string>(StringComparer.Ordinal);
        }

        public sealed class Result
        {
            public string Estado;              // ok | diferencias | no_independiente | independencia_no_comprobable | revision_incorrecta | invalido
            public string Motivo;
            public List<string> Missing = new List<string>();
            public List<long> Extra = new List<long>();
            public List<string> Duplicates = new List<string>();
            public List<string> Mismatches = new List<string>();
            public List<string> JustifiedExceptions = new List<string>();
            public int Matched;
            /// <summary>0.9.0: prueba de independencia registrada (fuente esperada, fuentes de construcción, método).</summary>
            public Dictionary<string, object> Independence;

            public bool Ok => Estado == "ok";

            public Dictionary<string, object> ToJson(int sample = 20)
            {
                var d = new Dictionary<string, object> { ["estado"] = Estado, ["emparejados"] = (long)Matched };
                if (Motivo != null) d["motivo"] = Motivo;
                if (Independence != null) d["independencia"] = Independence;
                void L<T>(string k, List<T> l) { if (l.Count > 0) { d[k + "_n"] = (long)l.Count; d[k] = l.Take(sample).Select(x => (object)x).ToList(); } }
                L("omitidos", Missing); L("adicionales", Extra); L("duplicados", Duplicates); L("discrepancias", Mismatches); L("excepciones_justificadas", JustifiedExceptions);
                return d;
            }
        }

        /// <summary>
        /// 0.9.0 (P0): independencia COMPROBABLE, no declarada. Exige: (1) fuente esperada identificada por contenido (SHA-256) y de un
        /// tipo que puede ser independiente; (2) al menos una fuente de construcción declarada; (3) ninguna coincidencia de id ni de
        /// contenido con lo que construyó; (4) linajes disjuntos en ambos sentidos; (5) lo esperado no es una copia exacta del plan.
        /// null = independiente; si no, (estado, motivo).
        /// </summary>
        public static (string Estado, string Motivo)? CheckIndependence(Input input, out Dictionary<string, object> proof)
        {
            proof = null;
            var exp = input.ExpectedSource;
            if (SourceKinds.NeverIndependent(exp.Kind))
                return ("no_independiente", "lo esperado es de tipo " + exp.Kind + " (modelo, plan de ejecución o inferencia del agente): nunca es una fuente independiente");
            if (string.IsNullOrWhiteSpace(exp.Sha256) || exp.Sha256.Trim().Length < 32)
                return ("independencia_no_comprobable", "la fuente esperada " + exp.Id + " no trae el SHA-256 de su contenido: no se puede demostrar que no es la misma que guió la construcción");
            var builds = new List<SourceRef>(input.BuildSources ?? new List<SourceRef>());
            if (!string.IsNullOrWhiteSpace(input.BuildSourceId) && !builds.Any(b => string.Equals(b.Id, input.BuildSourceId, StringComparison.OrdinalIgnoreCase)))
                builds.Add(new SourceRef { Id = input.BuildSourceId });
            if (builds.Count == 0)
                return ("independencia_no_comprobable", "no se declaró de qué fuente o plan salió la construcción; sin eso la independencia no se puede comprobar");
            var expIds = new HashSet<string>(exp.Identities(), StringComparer.Ordinal);
            var expLineage = new HashSet<string>(exp.Lineage(), StringComparer.Ordinal);
            foreach (var b in builds)
            {
                var bIds = b.Identities().ToList();
                var same = bIds.FirstOrDefault(expIds.Contains);
                if (same != null)
                    return ("no_independiente", "lo esperado y la construcción comparten " + (same.StartsWith("sha256:", StringComparison.Ordinal) ? "el mismo contenido (" + same.Substring(0, Math.Min(23, same.Length)) + "…)" : "la misma fuente (" + b.Id + ")") + ": solo probaría coherencia interna");
                var up = bIds.FirstOrDefault(expLineage.Contains);
                if (up != null) return ("no_independiente", "la fuente esperada " + exp.Id + " se derivó de " + up + ", que también guió la construcción");
                var down = b.Lineage().FirstOrDefault(expIds.Contains);
                if (down != null) return ("no_independiente", "la construcción (" + b.Id + ") se derivó de la propia fuente esperada " + exp.Id);
                var common = b.Lineage().FirstOrDefault(expLineage.Contains);
                if (common != null) return ("no_independiente", "la fuente esperada y la construcción (" + b.Id + ") se derivan de un mismo origen (" + common + ")");
            }
            if (!string.IsNullOrEmpty(input.BuildPlanFingerprint) && input.Expected.Count > 0 &&
                string.Equals(Fingerprint(input.Expected), input.BuildPlanFingerprint, StringComparison.OrdinalIgnoreCase))
                return ("no_independiente", "lo esperado es idéntico, elemento por elemento, al plan de ejecución: es una copia del plan, no una fuente independiente");
            proof = new Dictionary<string, object>
            {
                ["metodo"] = "sha256+linaje", ["fuente"] = exp.Id, ["fuente_sha256"] = exp.Sha256,
                ["construccion"] = builds.Select(b => (object)(b.Id + (b.Sha256 != null ? "@" + b.Sha256.Substring(0, Math.Min(12, b.Sha256.Length)) : ""))).ToList(),
                ["copia_del_plan"] = input.BuildPlanFingerprint == null ? "no_comprobado" : "no",
            };
            return null;
        }

        /// <summary>Huella estable de un conjunto de elementos (clave, posición a 1/256 ft y atributos), independiente del orden.</summary>
        public static string Fingerprint(IEnumerable<Item> items)
        {
            string R(double? v) => v.HasValue ? Math.Round(v.Value * 256).ToString(CultureInfo.InvariantCulture) : "-";
            var lines = items.Select(i => (i.Key ?? "") + "|" + R(i.X) + "|" + R(i.Y) + "|" +
                                          string.Join(";", i.Attrs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)))
                             .OrderBy(x => x, StringComparer.Ordinal);
            return Home.Sha256Hex(string.Join("\n", lines));
        }

        public static Result Verify(Input input)
        {
            if (input?.ExpectedSource == null || string.IsNullOrWhiteSpace(input.ExpectedSource.Id))
                return new Result { Estado = "invalido", Motivo = "falta la fuente independiente de lo esperado" };
            var ind = CheckIndependence(input, out var proof);
            if (ind != null) return new Result { Estado = ind.Value.Estado, Motivo = ind.Value.Motivo };
            if (input.RequiredRevision != null && !string.Equals(input.RequiredRevision, input.ExpectedSource.Revision, StringComparison.Ordinal))
                return new Result { Estado = "revision_incorrecta", Motivo = "la fuente esperada es la revisión " + (input.ExpectedSource.Revision ?? "(sin revisión)") + " y se exige " + input.RequiredRevision };

            var res = new Result { Independence = proof };
            var usedObserved = new HashSet<int>();

            // 1. duplicados en lo observado (misma clave más de una vez) salvo los permitidos
            foreach (var g in input.Observed.Where(o => o.Key != null).GroupBy(o => o.Key, StringComparer.Ordinal))
            {
                var expectedCount = input.Expected.Count(e => e.Key == g.Key);
                if (g.Count() > Math.Max(1, expectedCount) && !input.AllowedDuplicates.Contains(g.Key))
                    res.Duplicates.Add(g.Key + " ×" + g.Count().ToString(CultureInfo.InvariantCulture) + " (" + string.Join(",", g.Select(o => o.ElementId.ToString(CultureInfo.InvariantCulture))) + ")");
            }

            // 2. emparejar: por clave; si no hay clave, por posición (el más cercano dentro de tolerancia, sin reutilizar)
            for (var i = 0; i < input.Expected.Count; i++)
            {
                var e = input.Expected[i];
                var label = e.Key ?? ("(" + Fmt(e.X) + "," + Fmt(e.Y) + ")");
                var idx = -1;
                if (e.Key != null)
                {
                    for (var j = 0; j < input.Observed.Count; j++)
                        if (!usedObserved.Contains(j) && input.Observed[j].Key == e.Key) { idx = j; break; }
                }
                else if (e.X.HasValue && e.Y.HasValue)
                {
                    var best = double.MaxValue;
                    for (var j = 0; j < input.Observed.Count; j++)
                    {
                        var o = input.Observed[j];
                        if (usedObserved.Contains(j) || !o.X.HasValue || !o.Y.HasValue) continue;
                        var dx = o.X.Value - e.X.Value; var dy = o.Y.Value - e.Y.Value;
                        var dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist <= input.PositionTolerance && dist < best) { best = dist; idx = j; }
                    }
                }
                if (idx < 0)
                {
                    if (e.Key != null && input.Exceptions.TryGetValue(e.Key, out var why)) res.JustifiedExceptions.Add(e.Key + ": " + why);
                    else res.Missing.Add(label);
                    continue;
                }
                usedObserved.Add(idx);
                res.Matched++;
                var obs = input.Observed[idx];
                foreach (var a in input.CompareAttrs)
                {
                    e.Attrs.TryGetValue(a, out var ev); obs.Attrs.TryGetValue(a, out var ov);
                    if (ev == null) continue;   // la fuente no fija ese dato: no se exige (y no se inventa)
                    if (!string.Equals(ev, ov, StringComparison.Ordinal)) res.Mismatches.Add(label + " #" + obs.ElementId.ToString(CultureInfo.InvariantCulture) + " " + a + ": esperado «" + ev + "», modelo «" + (ov ?? "(vacío)") + "»");
                }
            }

            // 3. adicionales: observados sin pareja (los duplicados permitidos y excepciones no cuentan)
            for (var j = 0; j < input.Observed.Count; j++)
            {
                if (usedObserved.Contains(j)) continue;
                var o = input.Observed[j];
                if (o.Key != null && input.AllowedDuplicates.Contains(o.Key)) continue;
                if (o.Key != null && input.Exceptions.ContainsKey(o.Key)) { res.JustifiedExceptions.Add(o.Key + ": " + input.Exceptions[o.Key]); continue; }
                res.Extra.Add(o.ElementId);
            }

            res.Estado = res.Missing.Count + res.Extra.Count + res.Duplicates.Count + res.Mismatches.Count == 0 ? "ok" : "diferencias";
            return res;
        }

        private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "?";
    }
}
