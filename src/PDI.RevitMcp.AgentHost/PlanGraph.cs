// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>Grafo de dependencias del plan: orden topológico determinista (empates por id) y detección de ciclos con su recorrido.</summary>
    public static class PlanGraph
    {
        public static List<string> Order(IReadOnlyList<ValidatedOp> ops) => Order(ops.Select(o => (o.Id, (IEnumerable<string>)o.DependsOn)).ToList());
        public static List<string> FindCycle(IReadOnlyList<OpDraft> ops) => FindCycle(ops.Where(o => o.Id != null).Select(o => (o.Id, (IEnumerable<string>)o.DependsOn)).ToList());

        /// <summary>Orden de ejecución (Kahn con cola ordenada). Lanza InvalidOperationException si hay ciclo o dependencia desconocida.</summary>
        public static List<string> Order(IReadOnlyList<(string Id, IEnumerable<string> Deps)> ops)
        {
            var indeg = new Dictionary<string, int>(StringComparer.Ordinal);
            var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var o in ops) { indeg[o.Id] = 0; next[o.Id] = new List<string>(); }
            foreach (var o in ops)
                foreach (var d in o.Deps.Distinct(StringComparer.Ordinal))
                {
                    if (!indeg.ContainsKey(d)) throw new InvalidOperationException(o.Id + ": dependencia desconocida " + d);
                    indeg[o.Id]++;
                    next[d].Add(o.Id);
                }
            var ready = new SortedSet<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
            var res = new List<string>(ops.Count);
            while (ready.Count > 0)
            {
                var id = ready.Min; ready.Remove(id);
                res.Add(id);
                foreach (var n in next[id]) if (--indeg[n] == 0) ready.Add(n);
            }
            if (res.Count != ops.Count) throw new InvalidOperationException("ciclo: " + string.Join(" → ", FindCycle(ops) ?? new List<string>()));
            return res;
        }

        /// <summary>Un ciclo (lista de ids, el primero repetido al final) o null. Iterativo: no desborda la pila con planes largos.</summary>
        public static List<string> FindCycle(IReadOnlyList<(string Id, IEnumerable<string> Deps)> ops)
        {
            var deps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var o in ops) if (!deps.ContainsKey(o.Id)) deps[o.Id] = o.Deps.Where(x => x != null).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var color = new Dictionary<string, int>(StringComparer.Ordinal); // 0 blanco, 1 gris, 2 negro
            foreach (var root in deps.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (color.TryGetValue(root, out var c0) && c0 != 0) continue;
                var stack = new List<(string Id, int Next)> { (root, 0) };
                color[root] = 1;
                while (stack.Count > 0)
                {
                    var (id, i) = stack[stack.Count - 1];
                    var ds = deps[id];
                    if (i >= ds.Count) { color[id] = 2; stack.RemoveAt(stack.Count - 1); continue; }
                    stack[stack.Count - 1] = (id, i + 1);
                    var d = ds[i];
                    if (!deps.ContainsKey(d)) continue;
                    var c = color.TryGetValue(d, out var cc) ? cc : 0;
                    if (c == 1)
                    {
                        var at = stack.FindIndex(s => s.Id == d);
                        return stack.Skip(at).Select(s => s.Id).Concat(new[] { d }).ToList();
                    }
                    if (c == 0) { color[d] = 1; stack.Add((d, 0)); }
                }
            }
            return null;
        }
    }
}
