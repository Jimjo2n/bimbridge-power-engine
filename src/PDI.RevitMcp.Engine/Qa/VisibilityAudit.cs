// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Qa
{
    public sealed class VisibilityAudit
    {
        private readonly List<Dictionary<string, object>> _expected = new List<Dictionary<string, object>>();

        public VisibilityAudit(Dictionary<string, object> args, long[] views, long[] elements)
        {
            if (!args.TryGetValue("expectativas", out var value)) return;
            if (!(value is List<object> rows) || rows.Count == 0 || rows.Count > 100)
                throw new QueryException("expectativas exige 1 a 100 objetos de vista y elemento");
            var pairs = new HashSet<(long, long)>();
            foreach (var item in rows)
            {
                if (!(item is Dictionary<string, object> row) ||
                    !row.TryGetValue("vista", out var viewValue) || !(viewValue is long view) || !views.Contains(view) ||
                    !row.TryGetValue("id", out var idValue) || !(idValue is long id) || !elements.Contains(id) ||
                    row.Count < 3 || !pairs.Add((view, id)))
                    throw new QueryException("expectativas: indique un par vista e id único del alcance y sus comprobaciones");
                var hasFilter = row.ContainsKey("filtro_id");
                foreach (var entry in row)
                {
                    var valid = entry.Key switch
                    {
                        "vista" or "id" => true,
                        "plantilla_id" => entry.Value is long template && (template == -1 || template > 0),
                        "en_colector_de_vista" => entry.Value is bool,
                        "filtro_id" => entry.Value is long filter && filter > 0,
                        "filtro_activo" or "filtro_visible" => hasFilter && entry.Value is bool,
                        "linea_proyeccion" => entry.Value is List<object> rgb && rgb.Count == 3 && rgb.All(c => c is long n && n >= 0 && n <= 255),
                        _ => false,
                    };
                    if (!valid) throw new QueryException("expectativas: campo o valor inválido para " + entry.Key);
                }
                _expected.Add(row);
            }
        }

        public List<object> Compare(List<object> observed)
        {
            var differences = new List<object>();
            foreach (var expected in _expected)
            {
                var viewId = (long)expected["vista"];
                var id = (long)expected["id"];
                var view = Find(observed, viewId);
                var element = Find(Field(view, "elementos"), id);
                if (element == null || Arg.GetString(element, "estado") == "no_encontrado")
                {
                    Add("elemento", true, false);
                    continue;
                }
                Check("plantilla_id", Field(view, "plantilla_id"));
                Check("en_colector_de_vista", Field(element, "en_colector_de_vista"));
                var graphics = Field(element, "graficos_elemento") as Dictionary<string, object>;
                if (expected.TryGetValue("filtro_id", out var filterId))
                {
                    var filter = Find(Field(element, "filtros_coincidentes"), (long)filterId);
                    if (filter == null) { Add("filtro_id", filterId, null); continue; }
                    Check("filtro_activo", Field(filter, "activo"));
                    Check("filtro_visible", Field(filter, "visible"));
                    graphics = Field(filter, "graficos") as Dictionary<string, object>;
                }
                Check("linea_proyeccion", Field(graphics, "linea_proyeccion"));

                void Check(string field, object actual)
                {
                    if (expected.TryGetValue(field, out var wanted) && Json.Write(wanted) != Json.Write(actual)) Add(field, wanted, actual);
                }

                void Add(string field, object wanted, object actual) => differences.Add(new Dictionary<string, object>
                {
                    ["vista"] = viewId, ["id"] = id, ["campo"] = field, ["esperado"] = wanted, ["observado"] = actual,
                });
            }
            return differences;
        }

        private static Dictionary<string, object> Find(object rows, long id) =>
            (rows as IEnumerable<object>)?.OfType<Dictionary<string, object>>()
                .FirstOrDefault(row => Arg.GetLong(row, "id") == id);

        private static object Field(Dictionary<string, object> row, string key) =>
            row != null && row.TryGetValue(key, out var value) ? value : null;
    }
}
