// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PDI.RevitMcp.Engine.Qa
{
    /// <summary>
    /// 0.9.1 (Bloque H): reglas de salud del modelo sobre datos ya leídos (sin Revit). El add-in lee avisos, elementos, conectores,
    /// láminas y vistas; aquí se agrupa y se decide. Cada hallazgo lleva ids para poder actuar sobre él.
    /// </summary>
    public static class ModelHealth
    {
        public sealed class Warning { public string Description; public string Severity; public List<long> Ids = new List<long>(); }
        public sealed class Element
        {
            public long Id; public string Category, Family, Type, Level;
            public double X, Y, Z, RotationDeg;
            public bool HasLocation = true;
            public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        public sealed class Connector { public long ElementId; public string Domain; public bool Connected; public string Category; }
        public sealed class Sheet { public long Id; public string Number, Name; public int Viewports; public int Schedules; }
        public sealed class View { public long Id; public string Name, Kind; public bool OnSheet, IsTemplate, IsDependent; }

        public const double DuplicateTolerance = 0.003;   // pies (~1 mm)
        public const int MaxSample = 20;

        private static Dictionary<string, object> Bucket(IEnumerable<long> ids, string extra = null)
        {
            var l = ids.Distinct().OrderBy(x => x).ToList();
            var d = new Dictionary<string, object> { ["n"] = (long)l.Count, ["ids"] = l.Take(MaxSample).Cast<object>().ToList() };
            if (l.Count > MaxSample) d["mas"] = (long)(l.Count - MaxSample);
            if (extra != null) d["detalle"] = extra;
            return d;
        }

        /// <summary>Grupos de elementos coincidentes: misma categoría, familia, tipo y nivel, en la misma posición y giro (dentro de la tolerancia).</summary>
        public static List<List<long>> Duplicates(IEnumerable<Element> elements, double tol = DuplicateTolerance)
        {
            var groups = new List<List<long>>();
            // 0.9.1: coordenadas no finitas o fuera de rango de cuantización se excluyen (Math.Floor(x/tol) desbordaría long).
            const double MaxQuant = 1e9;
            bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < MaxQuant;
            foreach (var g in elements.Where(e => e.HasLocation && Finite(e.X) && Finite(e.Y) && Finite(e.Z) && Finite(e.RotationDeg)).GroupBy(e => (e.Category ?? "") + "|" + e.Family + "|" + e.Type + "|" + e.Level, StringComparer.Ordinal))
            {
                // Rejilla espacial: cada celda de tamaño tol; se comparan vecinos de las celdas adyacentes (O(n) esperado, no O(n²)).
                var cell = new Dictionary<(long, long, long), List<Element>>();
                (long, long, long) Key(Element e) => ((long)Math.Floor(e.X / tol), (long)Math.Floor(e.Y / tol), (long)Math.Floor(e.Z / tol));
                foreach (var e in g) { var k = Key(e); if (!cell.TryGetValue(k, out var l)) cell[k] = l = new List<Element>(); l.Add(e); }
                var seen = new HashSet<long>();
                foreach (var e in g.OrderBy(x => x.Id))
                {
                    if (seen.Contains(e.Id)) continue;
                    var (cx, cy, cz) = Key(e);
                    var grp = new List<long> { e.Id };
                    var inGrp = new HashSet<long> { e.Id };   // pertenencia O(1): en grupos muy densos List.Contains era cuadrático
                    for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
                    {
                        if (!cell.TryGetValue((cx + dx, cy + dy, cz + dz), out var near)) continue;
                        foreach (var o in near)
                        {
                            if (o.Id == e.Id || seen.Contains(o.Id) || inGrp.Contains(o.Id)) continue;
                            var dist = Math.Sqrt((o.X - e.X) * (o.X - e.X) + (o.Y - e.Y) * (o.Y - e.Y) + (o.Z - e.Z) * (o.Z - e.Z));
                            var dr = Math.Abs(((o.RotationDeg - e.RotationDeg) % 360 + 540) % 360 - 180);
                            if (dist <= tol && dr <= 0.5) { grp.Add(o.Id); inGrp.Add(o.Id); }
                        }
                    }
                    if (grp.Count > 1) { foreach (var id in grp) seen.Add(id); groups.Add(grp.OrderBy(x => x).ToList()); }
                }
            }
            return groups.OrderBy(x => x[0]).ToList();
        }

        public static Dictionary<string, object> Report(IList<Warning> warnings, IList<Element> elements, IList<Connector> connectors,
                                                        IList<Sheet> sheets, IList<View> views, IList<string> requiredParams)
        {
            warnings = warnings ?? new List<Warning>(); elements = elements ?? new List<Element>(); connectors = connectors ?? new List<Connector>();
            sheets = sheets ?? new List<Sheet>(); views = views ?? new List<View>(); requiredParams = requiredParams ?? new List<string>();

            var byWarning = warnings.GroupBy(w => w.Description ?? "").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (object)new Dictionary<string, object>
                {
                    ["aviso"] = g.Key, ["veces"] = (long)g.Count(), ["gravedad"] = g.First().Severity ?? "",
                    ["ids"] = g.SelectMany(w => w.Ids).Distinct().OrderBy(x => x).Take(MaxSample).Cast<object>().ToList(),
                }).ToList();

            var dups = Duplicates(elements);
            var missing = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in requiredParams.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var absent = elements.Where(e => !e.Params.ContainsKey(p)).Select(e => e.Id);
                var empty = elements.Where(e => e.Params.TryGetValue(p, out var v) && string.IsNullOrWhiteSpace(v)).Select(e => e.Id);
                missing[p] = new Dictionary<string, object> { ["sin_parametro"] = Bucket(absent), ["vacio"] = Bucket(empty) };
            }
            var disconnected = connectors.Where(c => !c.Connected).GroupBy(c => c.Domain ?? "desconocido").OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (object)Bucket(g.Select(c => c.ElementId), g.Count() + " conectores libres"));

            var emptySheets = sheets.Where(s => s.Viewports == 0 && s.Schedules == 0).Select(s => s.Id);
            var unplaced = views.Where(v => !v.IsTemplate && !v.OnSheet && (v.Kind == "FloorPlan" || v.Kind == "CeilingPlan" || v.Kind == "Section" || v.Kind == "Elevation" || v.Kind == "Detail" || v.Kind == "DraftingView" || v.Kind == "EngineeringPlan" || v.Kind == "AreaPlan"))
                                .Select(v => v.Id);
            var dupViewNames = views.Where(v => !v.IsTemplate).GroupBy(v => (v.Kind ?? "") + "|" + v.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g.Select(v => v.Id));

            var problems = warnings.Count + dups.Sum(g => g.Count - 1) + connectors.Count(c => !c.Connected);
            return new Dictionary<string, object>
            {
                ["resumen"] = new Dictionary<string, object>
                {
                    ["avisos"] = (long)warnings.Count, ["tipos_de_aviso"] = (long)byWarning.Count,
                    ["grupos_duplicados"] = (long)dups.Count, ["elementos_sobrantes"] = (long)dups.Sum(g => g.Count - 1),
                    ["conectores_libres"] = (long)connectors.Count(c => !c.Connected), ["laminas_vacias"] = (long)emptySheets.Count(),
                    ["vistas_sin_lamina"] = (long)unplaced.Count(), ["elementos_revisados"] = (long)elements.Count, ["conectores_revisados"] = (long)connectors.Count,
                    ["problemas"] = (long)problems,
                },
                ["avisos"] = byWarning,
                ["duplicados"] = dups.Take(MaxSample).Select(g => (object)g.Cast<object>().ToList()).ToList(),
                ["parametros_requeridos"] = missing,
                ["conectores_desconectados"] = disconnected,
                ["documentacion"] = new Dictionary<string, object>
                {
                    ["laminas_vacias"] = Bucket(emptySheets), ["vistas_sin_lamina"] = Bucket(unplaced), ["nombres_de_vista_repetidos"] = Bucket(dupViewNames),
                },
                ["nota"] = "Sólo lectura. Duplicado = misma categoría, familia, tipo y nivel en la misma posición y giro (±" + (DuplicateTolerance * 304.8).ToString("0.#", CultureInfo.InvariantCulture) + " mm, ±0,5°).",
            };
        }
    }
}
