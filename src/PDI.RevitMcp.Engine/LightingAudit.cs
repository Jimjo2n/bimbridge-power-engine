// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Receta del lado del servidor "lighting_audit" (sección 7, herramienta check).
    /// Lo que antes eran 5–10 idas y vueltas con scripts (inventario, tags, huérfanos, duplicados,
    /// worksets, visibilidad por RCP) se responde en una llamada.
    /// Aquí sólo se resume; los datos crudos los junta el adaptador de Revit.
    /// La regla de etiquetas es un perfil de la tarea (F12, R044, A15), no algo fijo en el código:
    ///   tags=uno      exactamente un tag por luminaria y vista, ningún tag sin luminaria (Non-Unit Lighting de SAMPLE PROJECT);
    ///   tags=ninguno  las luminarias no llevan tags (p. ej. Unit Grouping): cualquier tag de luminaria es una diferencia;
    ///   tags=ignorar  sólo cuenta, no juzga.
    /// </summary>
    public static class LightingAudit
    {
        public sealed class Fixture
        {
            public long Id;
            public string Level;
            public string Workset;
            public string Type;
        }

        public sealed class Tag
        {
            public long Id;
            public long ViewId;
            public string ViewName;
            /// <summary>Nivel asociado a la vista del tag (para filtrar huérfanos por nivel).</summary>
            public string ViewLevel;
            /// <summary>Elementos etiquetados que existen en el documento (vacío = huérfano).</summary>
            public long[] Tagged = new long[0];
        }

        /// <summary>
        /// Una vista a revisar. Sin <see cref="Visible"/> se estima por nivel asociado (sin regenerar nada en Revit);
        /// con <see cref="Visible"/> son las luminarias realmente visibles (modo exacto, regenera la vista).
        /// </summary>
        public sealed class ViewCount
        {
            public long ViewId;
            public string Name;
            public string Level;
            public long[] Visible;
        }

        /// <summary>Máximo de vistas en modo exacto: cada una obliga a Revit a regenerarla (~2 s en SAMPLE PROJECT).</summary>
        public const int MaxExactViews = 200;
        /// <summary>Máximo de filas de vistas en la respuesta.</summary>
        public const int MaxViewRows = 60;

        public const int SampleSize = 20;

        /// <summary>Devuelve el resumen y, aparte, los conjuntos que conviene guardar para la siguiente llamada.</summary>
        /// <param name="allFixtureIds">Todas las luminarias del modelo, aunque se filtre por nivel. Sin esto, con lvl= los tags
        /// de luminarias de otros niveles parecían huérfanos (877 falsos en LEVEL 02, prueba 0.2.2 del 2026-10-15).</param>
        /// <param name="levels">Niveles pedidos (vacío = todos): los tags de otros niveles no cuentan.</param>
        public static Dictionary<string, object> Summarize(
            IList<Fixture> fixtures, IList<Tag> tags, IList<ViewCount> views,
            out Dictionary<string, long[]> setsToSave,
            ICollection<long> allFixtureIds = null, IList<string> levels = null, string tagRule = "uno")
        {
            tagRule = (tagRule ?? "uno").Trim().ToLowerInvariant();
            if (tagRule != "uno" && tagRule != "ninguno" && tagRule != "ignorar")
                throw new ArgumentException("tags debe ser uno, ninguno o ignorar");
            setsToSave = new Dictionary<string, long[]>(StringComparer.Ordinal);
            var fixtureIds = new HashSet<long>(fixtures.Select(f => f.Id));
            var all = allFixtureIds != null ? new HashSet<long>(allFixtureIds) : fixtureIds;
            all.UnionWith(fixtureIds);
            var filtered = levels != null && levels.Count > 0;
            bool IsOrphan(Tag t) => t.Tagged == null || !t.Tagged.Any(all.Contains);
            if (filtered)
            {
                var lv = new HashSet<string>(levels, StringComparer.OrdinalIgnoreCase);
                // Sólo los tags de este alcance: los que etiquetan luminarias de esos niveles, o huérfanos en vistas de esos niveles.
                tags = tags.Where(t => (t.Tagged != null && t.Tagged.Any(fixtureIds.Contains)) || (IsOrphan(t) && t.ViewLevel != null && lv.Contains(t.ViewLevel))).ToList();
            }

            // Tags huérfanos: no etiquetan ninguna luminaria existente en el modelo.
            var orphan = tags.Where(IsOrphan).Select(t => t.Id).ToArray();

            // Tags por luminaria y vista.
            var perFixtureView = new Dictionary<(long fixture, long view), List<long>>();
            var tagged = new HashSet<long>();
            foreach (var t in tags)
            {
                if (t.Tagged == null) continue;
                foreach (var fid in t.Tagged.Where(fixtureIds.Contains).Distinct())
                {
                    tagged.Add(fid);
                    var key = (fid, t.ViewId);
                    if (!perFixtureView.TryGetValue(key, out var list)) perFixtureView[key] = list = new List<long>();
                    list.Add(t.Id);
                }
            }
            var untagged = fixtures.Where(f => !tagged.Contains(f.Id)).Select(f => f.Id).ToArray();
            // Duplicados: más de un tag de la misma luminaria en la misma vista. Se proponen los sobrantes.
            var dupGroups = perFixtureView.Where(kv => kv.Value.Count > 1).ToList();
            var extraTags = dupGroups.SelectMany(kv => kv.Value.OrderBy(x => x).Skip(1)).ToArray();

            var result = new Dictionary<string, object>
            {
                ["luminarias"] = (long)fixtures.Count,
                ["tags"] = (long)tags.Count,
                ["por_nivel"] = CountBy(fixtures, f => f.Level),
                ["por_workset"] = CountBy(fixtures, f => f.Workset),
                ["por_tipo"] = CountBy(fixtures, f => f.Type),
                ["sin_tag"] = Block(untagged),
                ["tags_huerfanos"] = Block(orphan),
                ["tags_duplicados"] = new Dictionary<string, object>
                {
                    ["n"] = (long)dupGroups.Count,
                    ["sobrantes"] = (long)extraTags.Length,
                    ["muestra"] = dupGroups.Take(SampleSize)
                        .Select(kv => (object)new object[] { kv.Key.fixture, kv.Key.view, kv.Value.Cast<object>().ToList() })
                        .ToList(),
                    ["cols_muestra"] = new List<object> { "luminaria", "vista", "tags" },
                },
            };

            if (views != null && views.Count > 0)
            {
                var exact = views.All(v => v.Visible != null);
                var tagsByView = new Dictionary<long, HashSet<long>>();
                var tagCountByView = new Dictionary<long, long>();
                foreach (var t in tags)
                {
                    tagCountByView[t.ViewId] = (tagCountByView.TryGetValue(t.ViewId, out var c) ? c : 0) + 1;
                    if (!tagsByView.TryGetValue(t.ViewId, out var hs)) tagsByView[t.ViewId] = hs = new HashSet<long>();
                    if (t.Tagged != null) foreach (var f in t.Tagged) hs.Add(f);
                }
                var byLevel = fixtures.GroupBy(f => f.Level ?? "", StringComparer.OrdinalIgnoreCase)
                                      .ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
                var rows = new List<object>();
                foreach (var v in views)
                {
                    var inView = v.Visible ?? (byLevel.TryGetValue(v.Level ?? "", out var ids) ? ids : new long[0]);
                    tagsByView.TryGetValue(v.ViewId, out var taggedHere);
                    var missing = inView.Count(id => taggedHere == null || !taggedHere.Contains(id));
                    rows.Add(new object[] { v.ViewId, v.Name, v.Level ?? "", (long)inView.Length, tagCountByView.TryGetValue(v.ViewId, out var n) ? n : 0L, (long)missing });
                }
                result["vistas_cols"] = new List<object> { "id", "nombre", "nivel", "luminarias", "tags", "sin_tag_en_vista" };
                result["vistas"] = rows.Take(MaxViewRows).ToList();
                if (rows.Count > MaxViewRows) result["vistas_omitidas"] = (long)(rows.Count - MaxViewRows);
                result["vistas_metodo"] = exact
                    ? "visible: luminarias que Revit muestra en cada vista"
                    : "nivel: luminarias del nivel asociado a la vista (no considera recorte, rango ni filtros; use exacto=true con pocas vistas para confirmar)";
            }

            result["regla_tags"] = tagRule;
            if (tagRule == "uno")
            {
                var ok = untagged.Length == 0 && orphan.Length == 0 && extraTags.Length == 0;
                result["cumple_regla"] = ok;
                if (untagged.Length > 0) setsToSave["$sin_tag"] = untagged;
                if (extraTags.Length > 0) setsToSave["$tags_sobrantes"] = extraTags;
            }
            else if (tagRule == "ninguno")
            {
                // Las luminarias no deben llevar tags: los tags de luminarias son la diferencia; "sin tag" es lo esperado.
                var fixtureTags = tags.Where(t => t.Tagged != null && t.Tagged.Any(fixtureIds.Contains)).Select(t => t.Id).ToArray();
                result.Remove("sin_tag");
                result["tags_de_luminarias"] = Block(fixtureTags);
                result["cumple_regla"] = fixtureTags.Length == 0 && orphan.Length == 0;
                if (fixtureTags.Length > 0) setsToSave["$tags_de_luminarias"] = fixtureTags;
            }
            if (orphan.Length > 0) setsToSave["$tags_huerfanos"] = orphan;
            return result;
        }

        private static Dictionary<string, object> Block(long[] ids) => new Dictionary<string, object>
        {
            ["n"] = (long)ids.Length,
            ["muestra"] = ids.Take(SampleSize).Cast<object>().ToList(),
        };

        private static Dictionary<string, object> CountBy(IEnumerable<Fixture> items, Func<Fixture, string> key)
        {
            var d = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in items)
            {
                var k = key(i) ?? "(sin dato)";
                d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;
            }
            return d.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }
    }
}
