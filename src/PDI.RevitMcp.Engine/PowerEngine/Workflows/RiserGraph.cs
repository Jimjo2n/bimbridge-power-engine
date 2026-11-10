// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BimBridge.PowerEngine.Workflows
{
    public sealed class RiserNode
    {
        public string Id, Label, Kind;   // Kind: panel | dispositivo | union | terminal
        public string Level; public double LevelElevFt;
        public bool Terminal => Kind == "terminal" || Kind == "dispositivo";
    }

    public sealed class RiserEdge
    {
        public string From, To, Label;
        /// <summary>"fisico" = respaldado por un conduit/cable del modelo (PhysicalId); "diagrama" = sólo una línea 2D del riser.</summary>
        public string Kind = "diagrama";
        public string PhysicalId;
    }

    /// <summary>
    /// Grafo de montantes (riser): niveles, conexiones verticales, extremos abiertos, IDs repetidos, ramas esperadas que faltan,
    /// y un dibujo 2D (columna por montante, fila por nivel) del que se cuentan cruces de líneas y rótulos atravesados.
    /// Regla: una conexión de diagrama no es un conduit físico; se informa aparte y nunca cuenta como continuidad física.
    /// </summary>
    public static class RiserGraph
    {
        public static Dictionary<string, object> Analyze(IList<RiserNode> nodes, IList<RiserEdge> edges, IEnumerable<string> allowedDuplicateIds = null,
                                                         IEnumerable<(string From, string To)> expectedBranches = null, double columnWidthIn = 1.5, double labelWidthIn = 0.9,
                                                         PowerEngineDispatcher engine = null)
        {
            var allowed = new HashSet<string>(allowedDuplicateIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var dupIds = nodes.GroupBy(x => x.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            var dupInvalid = dupIds.Where(d => !allowed.Contains(d)).ToList();
            var byId = nodes.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var idx = byId.Keys.Select((k, i) => (k, i)).ToDictionary(t => t.k, t => t.i, StringComparer.Ordinal);
            var dangling = edges.Where(e => !idx.ContainsKey(e.From) || !idx.ContainsKey(e.To)).Select(e => (object)(e.From + "→" + e.To)).ToList();
            var good = edges.Where(e => idx.ContainsKey(e.From) && idx.ContainsKey(e.To)).ToList();

            // componentes: diagrama completo frente a sólo físico
            int[] Flat(IEnumerable<RiserEdge> es) => es.SelectMany(e => new[] { idx[e.From], idx[e.To] }).ToArray();
            (int[] Component, int[] Degree) C(int[] e) => engine != null ? engine.Components(idx.Count, e) : GeometryKernels.Components(idx.Count, e);
            var all = C(Flat(good));
            var phys = C(Flat(good.Where(e => e.Kind == "fisico" && !string.IsNullOrEmpty(e.PhysicalId))));
            var diagramOnly = good.Where(e => e.Kind != "fisico" || string.IsNullOrEmpty(e.PhysicalId)).Select(e => (object)new Dictionary<string, object> { ["de"] = e.From, ["a"] = e.To, ["rotulo"] = e.Label }).ToList();
            var open = byId.Values.Where(v => all.Degree[idx[v.Id]] <= 1 && !v.Terminal && v.Kind != "panel").Select(v => (object)v.Id).ToList();
            var isolated = byId.Values.Where(v => all.Degree[idx[v.Id]] == 0).Select(v => (object)v.Id).ToList();
            var missing = (expectedBranches ?? Enumerable.Empty<(string, string)>())
                .Where(b => !good.Any(e => (e.From == b.Item1 && e.To == b.Item2) || (e.From == b.Item2 && e.To == b.Item1)))
                .Select(b => (object)(b.Item1 + "→" + b.Item2)).ToList();
            var vertical = good.Count(e => byId[e.From].Level != byId[e.To].Level);

            // dibujo: columna = componente físico (un montante), fila = elevación del nivel
            var levels = byId.Values.Select(v => (v.Level, v.LevelElevFt)).Distinct().OrderBy(l => l.LevelElevFt).ToList();
            var row = levels.Select((l, i) => (l.Level, i)).ToDictionary(t => t.Level, t => t.i, StringComparer.Ordinal);
            var cols = new Dictionary<int, int>();
            foreach (var v in byId.Values.OrderBy(v => v.Id, StringComparer.Ordinal)) { var c = phys.Component[idx[v.Id]]; if (!cols.ContainsKey(c)) cols[c] = cols.Count; }
            var slot = new Dictionary<(int, int), int>();
            var pos = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
            foreach (var v in byId.Values.OrderBy(v => v.Id, StringComparer.Ordinal))
            {
                var c = cols[phys.Component[idx[v.Id]]]; var r = row[v.Level];
                slot.TryGetValue((c, r), out var s); slot[(c, r)] = s + 1;
                pos[v.Id] = (c * columnWidthIn + s * 0.3, r * 1.0);
            }
            var segs = new List<double>();
            foreach (var e in good) { var a = pos[e.From]; var b = pos[e.To]; segs.AddRange(new[] { a.X, a.Y, b.X, b.Y }); }
            var contacts = good.Count > 1 ? (engine != null ? engine.SegmentContacts(segs.ToArray()) : GeometryKernels.SegmentContacts(segs.ToArray())) : new List<(int I, int J, int Kind)>();
            var crossings = contacts.Count(c => c.Kind == GeometryKernels.ContactCross);
            var overlaps = contacts.Count(c => c.Kind == GeometryKernels.ContactOverlap);
            // rótulo de nodo atravesado por una línea que no le pertenece
            var labelHits = new List<object>();
            foreach (var v in byId.Values)
            {
                var p = pos[v.Id];
                double x0 = p.X + 0.08, x1 = p.X + 0.08 + labelWidthIn, y0 = p.Y - 0.06, y1 = p.Y + 0.06;
                for (var k = 0; k < good.Count; k++)
                {
                    if (good[k].From == v.Id || good[k].To == v.Id) continue;
                    if (SegHitsBox(segs[4 * k], segs[4 * k + 1], segs[4 * k + 2], segs[4 * k + 3], x0, y0, x1, y1)) { labelHits.Add(v.Id + " ⟂ " + good[k].From + "→" + good[k].To); break; }
                }
            }
            return new Dictionary<string, object>
            {
                ["nodos"] = (long)byId.Count, ["aristas"] = (long)good.Count, ["niveles"] = levels.Select(l => (object)l.Level).ToList(), ["verticales"] = (long)vertical,
                ["componentes_diagrama"] = (long)all.Component.Distinct().Count(), ["componentes_fisicos"] = (long)phys.Component.Distinct().Count(),
                ["solo_diagrama"] = diagramOnly, ["extremos_abiertos"] = open, ["aislados"] = isolated, ["ramas_faltantes"] = missing,
                ["ids_repetidos_invalidos"] = dupInvalid.Cast<object>().ToList(), ["ids_repetidos_validos"] = dupIds.Where(allowed.Contains).Cast<object>().ToList(),
                ["aristas_sin_nodo"] = dangling,
                ["dibujo"] = new Dictionary<string, object> { ["cruces"] = (long)crossings, ["superposiciones"] = (long)overlaps, ["rotulos_atravesados"] = labelHits, ["columnas"] = (long)cols.Count },
                ["veredicto"] = dupInvalid.Count + missing.Count + open.Count + dangling.Count + labelHits.Count == 0 ? "coherente" : "revisar",
                ["nota"] = "Una línea de diagrama no prueba un conduit: 'solo_diagrama' lista las conexiones sin elemento físico.",
            };
        }

        private static bool SegHitsBox(double x0, double y0, double x1, double y1, double bx0, double by0, double bx1, double by1)
        {
            // Liang–Barsky
            double t0 = 0, t1 = 1, dx = x1 - x0, dy = y1 - y0;
            foreach (var (p, q) in new[] { (-dx, x0 - bx0), (dx, bx1 - x0), (-dy, y0 - by0), (dy, by1 - y0) })
            {
                if (Math.Abs(p) < 1e-12) { if (q < 0) return false; continue; }
                var r = q / p;
                if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; } else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            return true;
        }
    }
}
