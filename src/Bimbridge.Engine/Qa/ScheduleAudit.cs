// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Qa
{
    /// <summary>Contrasta la población, configuración y celdas de una tabla contra expectativas externas.</summary>
    public sealed class ScheduleAudit
    {
        public sealed class Cell
        {
            public int Row, Column;
            public string Text;
        }

        private readonly HashSet<long> _ids;
        private readonly long? _count;
        private readonly List<object> _filters;
        public readonly List<Cell> Cells = new List<Cell>();
        public bool NeedsPopulation => _ids != null || _count.HasValue;

        public ScheduleAudit(Dictionary<string, object> args)
        {
            if (args.TryGetValue("ids_esperados", out var ids))
            {
                if (!(ids is List<object> list) || list.Any(x => !(x is long id) || id <= 0))
                    throw new QueryException("ids_esperados exige una lista JSON de IDs positivos");
                _ids = new HashSet<long>(list.Cast<long>());
                if (_ids.Count != list.Count) throw new QueryException("ids_esperados contiene duplicados");
            }
            if (args.TryGetValue("cantidad_esperada", out var count))
            {
                if (!(count is long n) || n < 0) throw new QueryException("cantidad_esperada exige un entero no negativo");
                _count = n;
            }
            if (args.TryGetValue("filtros_esperados", out var filters))
            {
                _filters = filters as List<object>;
                if (_filters == null || _filters.Any(f => !(f is Dictionary<string, object>)))
                    throw new QueryException("filtros_esperados exige una lista JSON de contratos de filtro");
            }
            if (args.TryGetValue("celdas_esperadas", out var cells))
            {
                if (!(cells is List<object> list) || list.Count > 200)
                    throw new QueryException("celdas_esperadas exige una lista JSON de hasta 200 celdas");
                var seen = new HashSet<(int, int)>();
                foreach (var item in list)
                {
                    if (!(item is Dictionary<string, object> cell) ||
                        !cell.TryGetValue("fila", out var rawRow) || !(rawRow is long row) || row < 0 || row > int.MaxValue ||
                        !cell.TryGetValue("columna", out var rawColumn) || !(rawColumn is long col) || col < 0 || col > int.MaxValue ||
                        !cell.TryGetValue("texto", out var rawText) || !(rawText is string value))
                        throw new QueryException("cada celda exige fila, columna (desde cero) y texto");
                    if (!seen.Add(((int)row, (int)col))) throw new QueryException("celdas_esperadas contiene coordenadas duplicadas");
                    Cells.Add(new Cell { Row = (int)row, Column = (int)col, Text = value });
                }
            }
        }

        public Dictionary<string, object> Compare(IEnumerable<long> population, List<object> filters, Func<Cell, string> read)
        {
            var differences = new List<object>();
            var ids = population == null ? null : new HashSet<long>(population);
            var checkedCount = 0;
            if (NeedsPopulation && ids == null)
                differences.Add(new Dictionary<string, object> { ["tipo"] = "poblacion_no_evaluable" });
            if (_ids != null && ids != null)
            {
                checkedCount++;
                var missing = _ids.Except(ids).OrderBy(i => i).ToList();
                var extra = ids.Except(_ids).OrderBy(i => i).ToList();
                if (missing.Count + extra.Count > 0)
                    differences.Add(new Dictionary<string, object>
                    {
                        ["tipo"] = "poblacion", ["faltan"] = (long)missing.Count, ["sobran"] = (long)extra.Count,
                        ["muestra_faltan"] = missing.Take(20).Cast<object>().ToList(), ["muestra_sobran"] = extra.Take(20).Cast<object>().ToList(),
                    });
            }
            if (_count.HasValue && ids != null)
            {
                checkedCount++;
                if (ids.Count != _count.Value) differences.Add(new Dictionary<string, object> { ["tipo"] = "cantidad", ["esperado"] = _count.Value, ["observado"] = (long)ids.Count });
            }
            if (_filters != null)
            {
                checkedCount++;
                IEnumerable<string> Canonical(List<object> list) => list.Select(OpSemantics.Canonical).OrderBy(x => x, StringComparer.Ordinal);
                if (!Canonical(_filters).SequenceEqual(Canonical(filters)))
                    differences.Add(new Dictionary<string, object> { ["tipo"] = "filtros", ["esperado"] = _filters, ["observado"] = filters });
            }
            foreach (var cell in Cells)
            {
                checkedCount++;
                var actual = read(cell);
                if (!string.Equals(actual, cell.Text, StringComparison.Ordinal))
                    differences.Add(new Dictionary<string, object>
                    {
                        ["tipo"] = actual == null ? "celda_fuera_de_rango" : "celda", ["fila"] = (long)cell.Row,
                        ["columna"] = (long)cell.Column, ["esperado"] = cell.Text, ["observado"] = actual,
                    });
            }
            return new Dictionary<string, object>
            {
                ["resultado"] = differences.Count > 0 ? (NeedsPopulation && ids == null ? "no_evaluable" : "con_diferencias") :
                    checkedCount == 0 ? "inventario" : "verificado",
                ["comprobaciones"] = (long)checkedCount, ["poblacion"] = ids == null ? null : (object)(long)ids.Count,
                ["filtros"] = filters, ["diferencias"] = differences,
            };
        }
    }
}
