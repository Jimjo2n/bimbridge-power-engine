// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Ejecuta una QuerySpec en el hilo de Revit, por tramos (sección 8):
    ///   tramo 1: colector con filtros rápidos (categoría, vista, ids) → lista de ids;
    ///   tramos siguientes: leer sólo los campos pedidos, hasta agotar el presupuesto de cada visita.
    /// Sólo lee: no abre transacciones ni se suscribe a nada (P2).
    /// Las filas resultantes son valores simples; filtrar, agrupar y paginar se hace fuera del hilo de Revit.
    /// </summary>
    public sealed class QueryRunner
    {
        private readonly QuerySpec _q;
        private readonly IReadOnlyCollection<long> _explicitIds;
        private readonly HashSet<long> _sinceIds;
        private readonly List<string> _fields;
        private List<ElementId> _candidates;
        private readonly ElementFields.NameCache _names = new ElementFields.NameCache();
        private int _next;

        public readonly List<Dictionary<string, object>> Rows = new List<Dictionary<string, object>>();
        public long Rev;
        public string Error;
        public string DocumentTitle;
        /// <summary>Total obtenido con GetElementCount (conteo rápido); null si se leyeron los elementos.</summary>
        public long? FastCount;

        public QueryRunner(QuerySpec q, IReadOnlyCollection<long> explicitIds, HashSet<long> sinceIds)
        {
            _q = q;
            _explicitIds = explicitIds;
            _sinceIds = sinceIds;
            _fields = q.FieldsToRead();
            // Los filtros por nivel, tipo y workset se aplican sobre los campos leídos.
            if (q.Levels.Count > 0 && !_fields.Contains("lvl")) _fields.Add("lvl");
            if (q.Types.Count > 0) { if (!_fields.Contains("fam")) _fields.Add("fam"); if (!_fields.Contains("type")) _fields.Add("type"); }
            if (q.Worksets.Count > 0 && !_fields.Contains("ws")) _fields.Add("ws");
        }

        /// <summary>Un tramo de trabajo. Devuelve true cuando terminó.</summary>
        public bool Step(UIApplication app, Stopwatch sw, int budgetMs, DocumentTracker tracker)
        {
            var doc = DocTarget.Resolve(app, out var docError);
            if (doc == null) { Error = docError; return true; }
            DocumentTitle = doc.Title;

            if (_candidates == null)
            {
                Rev = tracker.For(doc)?.Rev ?? 0;
                _candidates = Collect(doc);
                if (Error != null) return true;
            }

            var deadline = sw.ElapsedMilliseconds + budgetMs;
            while (_next < _candidates.Count)
            {
                var e = doc.GetElement(_candidates[_next++]);
                if (e == null) continue;
                var row = ElementFields.Read(doc, e, _fields, _names);
                if (PassesPostFilters(row)) Rows.Add(row);
                // revisar el reloj cada 32 elementos: barato y suficiente
                if ((_next & 31) == 0 && sw.ElapsedMilliseconds >= deadline) return false;
            }
            return true;
        }

        private List<ElementId> Collect(Document doc)
        {
            IEnumerable<ElementId> ids;

            if (_explicitIds != null)
            {
                ids = _explicitIds.Select(Ids.ToId);
            }
            else
            {
                FilteredElementCollector col;
                if (_q.ViewId.HasValue)
                {
                    var view = doc.GetElement(Ids.ToId(_q.ViewId.Value)) as View;
                    if (view == null) { Error = "view " + _q.ViewId.Value + " no es una vista de este documento"; return null; }
                    col = new FilteredElementCollector(doc, view.Id);
                }
                else
                {
                    col = new FilteredElementCollector(doc);
                }

                if (_q.Categories.Count > 0)
                {
                    var cats = new List<BuiltInCategory>();
                    foreach (var name in _q.Categories)
                    {
                        if (!TryCategory(name, out var bic)) { Error = "categoría desconocida: " + name + " (use nombres como LightingFixtures, ElectricalFixtures)"; return null; }
                        cats.Add(bic);
                    }
                    col = col.WherePasses(new ElementMulticategoryFilter(cats));
                }
                col = col.WhereElementIsNotElementType();
                // Conteo puro: el índice de Revit da el total sin materializar ids ni abrir elementos.
                if (_q.CanFastCount && _sinceIds == null)
                {
                    FastCount = col.GetElementCount();
                    return new List<ElementId>();
                }
                ids = col.ToElementIds();
            }

            if (_sinceIds != null) ids = ids.Where(id => _sinceIds.Contains(Ids.ToLong(id)));
            return ids.ToList();
        }

        private bool PassesPostFilters(Dictionary<string, object> row)
        {
            if (_q.Levels.Count > 0 && !AnyEquals(row, "lvl", _q.Levels)) return false;
            if (_q.Worksets.Count > 0 && !AnyEquals(row, "ws", _q.Worksets)) return false;
            if (_q.Types.Count > 0)
            {
                var fam = row.TryGetValue("fam", out var f) ? f as string : null;
                var type = row.TryGetValue("type", out var t) ? t as string : null;
                var full = (fam ?? "") + ": " + (type ?? "");
                if (!_q.Types.Any(x => full.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            }
            return true;
        }

        private static bool AnyEquals(Dictionary<string, object> row, string key, List<string> values)
        {
            var v = row.TryGetValue(key, out var o) ? o as string : null;
            return v != null && values.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>"LightingFixtures" u "OST_LightingFixtures" → BuiltInCategory.</summary>
        public static bool TryCategory(string name, out BuiltInCategory bic)
        {
            var n = (name ?? "").Trim().Replace(" ", "");
            if (!n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) n = "OST_" + n;
            return Enum.TryParse(n, true, out bic) && bic != BuiltInCategory.INVALID;
        }
    }
}
