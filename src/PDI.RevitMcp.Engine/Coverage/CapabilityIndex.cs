// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PDI.RevitMcp.Engine.Coverage
{
    /// <summary>Filtro de búsqueda de capacidades. Todo es opcional; se combinan con Y.</summary>
    public sealed class CapabilityQuery
    {
        public string Text;          // palabras (es/en, id, subdominio, comando)
        public string Domain;        // F01..F37
        public string Status;        // CoverageStatus
        public string Strategy;      // prefijo: typed, query, doc, exec, engine, postcommand, none…
        public int? Version;         // 2022..2026
        public string Category;      // categoría Revit
        public string DocumentKind;  // project | family | any
        public string Priority;      // P0..P3
        public int Page = 1;
        public int PageSize = 20;
    }

    /// <summary>
    /// 0.9.1: índice inmutable del catálogo de capacidades. Se construye UNA vez por proceso (perezoso) a partir de datos ya
    /// compilados: no hay reflexión ni lectura de ensamblados de Revit, ni recorrido del modelo, y no entra al hilo de Revit.
    /// Índices por id, dominio, estado, estrategia, versión, categoría, tipo de documento y palabras. Las búsquedas devuelven filas
    /// compactas paginadas; el contrato completo sólo se serializa al pedirlo por id.
    /// </summary>
    public sealed class CapabilityIndex
    {
        public const int MaxPageSize = 100;
        private static readonly Lazy<CapabilityIndex> _default = new Lazy<CapabilityIndex>(() => new CapabilityIndex(CapabilityCatalog.All));
        public static CapabilityIndex Default => _default.Value;

        private readonly CapabilityEntry[] _all;
        private readonly Dictionary<string, int> _byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int[]> _byDomain, _byStatus, _byStrategy, _byCategory, _byDocKind, _byPriority;
        private readonly Dictionary<string, int[]> _byToken;
        private readonly string[] _tokenKeys;
        private readonly Lazy<string> _version;
        public string Version => _version.Value;

        public int Count => _all.Length;

        public CapabilityIndex(IEnumerable<CapabilityEntry> entries)
        {
            _all = entries.ToArray();
            for (var i = 0; i < _all.Length; i++)
            {
                if (_byId.ContainsKey(_all[i].Id)) throw new ArgumentException("id de capacidad repetido: " + _all[i].Id);
                _byId[_all[i].Id] = i;
            }
            _byDomain = Group(c => new[] { c.Domain });
            _byStatus = Group(c => new[] { c.Status.ToString() });
            _byStrategy = Group(c => new[] { StrategyKind(c.Strategy) });
            _byCategory = Group(c => c.Categories ?? new string[0]);
            _byDocKind = Group(c => new[] { c.DocumentKind ?? "project" });
            _byPriority = Group(c => new[] { c.Priority ?? "P2" });
            _byToken = Group(c => Tokens(c.Id + " " + c.Name + " " + c.NameEn + " " + c.Subdomain + " " + c.Operation + " " + string.Join(" ", c.PostableCommands ?? new string[0])));
            _tokenKeys = _byToken.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            _version = new Lazy<string>(ComputeVersion);
        }

        /// <summary>
        /// AUD-16: huella del contenido canónico COMPLETO (SHA-256 del contrato íntegro de cada capacidad, en orden de id): cualquier
        /// cambio de nombre, límites, verificador, pruebas, comandos, versiones… cambia la huella. Perezosa: no cuesta en la carga.
        /// </summary>
        private string ComputeVersion()
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                foreach (var c in _all.OrderBy(x => x.Id, StringComparer.Ordinal))
                {
                    var contract = Contract(c);
                    var canon = string.Join("\u001f", contract.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => kv.Key + "=" + (kv.Value is List<object> l ? string.Join("\u001e", l) : Convert.ToString(kv.Value, CultureInfo.InvariantCulture))));
                    canon += "\u001fmin=" + c.MinVersion + "\u001fmax=" + c.MaxVersion + "\u001d";
                    var b = Encoding.UTF8.GetBytes(canon);
                    sha.TransformBlock(b, 0, b.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant().Substring(0, 32);
            }
        }

        public static string StrategyKind(string s)
        {
            if (string.IsNullOrEmpty(s)) return "none";
            var i = s.IndexOf(':');
            return (i > 0 ? s.Substring(0, i) : s).ToLowerInvariant();
        }

        private Dictionary<string, int[]> Group(Func<CapabilityEntry, IEnumerable<string>> keys)
        {
            var tmp = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _all.Length; i++)
                foreach (var k in keys(_all[i]).Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!tmp.TryGetValue(k, out var l)) tmp[k] = l = new List<int>();
                    l.Add(i);
                }
            return tmp.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Palabras normalizadas (minúsculas, sin acentos, partidas por no-alfanuméricos y CamelCase), de 2+ caracteres.</summary>
        public static IEnumerable<string> Tokens(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            var norm = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(norm.Length + 8);
            char prev = ' ';
            foreach (var ch in norm)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsUpper(ch) && char.IsLower(prev)) sb.Append(' ');
                sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
                prev = ch;
            }
            foreach (var t in sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (t.Length >= 2) yield return t;
        }

        public CapabilityEntry Get(string id) => id != null && _byId.TryGetValue(id.Trim(), out var i) ? _all[i] : null;

        /// <summary>Busca y pagina. Intersección de listas ordenadas (sin recorrer el catálogo si hay algún filtro indexado).</summary>
        public (IReadOnlyList<CapabilityEntry> Page, int Total) Search(CapabilityQuery q)
        {
            q = q ?? new CapabilityQuery();
            IEnumerable<int> set = null;
            void And(int[] ids) { set = set == null ? ids : set.Intersect(ids); }
            int[] Lookup(Dictionary<string, int[]> d, string k) => d.TryGetValue(k.Trim(), out var v) ? v : new int[0];
            if (!string.IsNullOrWhiteSpace(q.Domain)) And(Lookup(_byDomain, q.Domain));
            if (!string.IsNullOrWhiteSpace(q.Status)) And(Lookup(_byStatus, q.Status));
            if (!string.IsNullOrWhiteSpace(q.Strategy)) And(Lookup(_byStrategy, StrategyKind(q.Strategy)));
            if (!string.IsNullOrWhiteSpace(q.Category)) And(Lookup(_byCategory, q.Category));
            if (!string.IsNullOrWhiteSpace(q.DocumentKind)) And(Lookup(_byDocKind, q.DocumentKind).Concat(Lookup(_byDocKind, "any")).OrderBy(x => x).ToArray());
            if (!string.IsNullOrWhiteSpace(q.Priority)) And(Lookup(_byPriority, q.Priority));
            if (!string.IsNullOrWhiteSpace(q.Text))
                foreach (var t in Tokens(q.Text).Distinct())
                {
                    // prefijo: "circ" encuentra "circuito"; se acota a las claves del índice de palabras (no al catálogo).
                    // Búsqueda binaria del primer token >= t en las claves ordenadas y recorrido sólo mientras compartan el prefijo.
                    var lo = Array.BinarySearch(_tokenKeys, t, StringComparer.Ordinal);
                    if (lo < 0) lo = ~lo;
                    var hits = new HashSet<int>();
                    for (var j = lo; j < _tokenKeys.Length && _tokenKeys[j].StartsWith(t, StringComparison.Ordinal); j++)
                        foreach (var x in _byToken[_tokenKeys[j]]) hits.Add(x);
                    And(hits.OrderBy(x => x).ToArray());
                }
            var ids = (set ?? Enumerable.Range(0, _all.Length));
            if (q.Version.HasValue) ids = ids.Where(i => _all[i].MinVersion <= q.Version && _all[i].MaxVersion >= q.Version);
            var list = ids.Distinct().OrderBy(i => _all[i].Id, StringComparer.Ordinal).ToList();
            var size = Math.Max(1, Math.Min(MaxPageSize, q.PageSize));
            var page = Math.Max(1, q.Page);
            return (list.Skip((page - 1) * size).Take(size).Select(i => _all[i]).ToList(), list.Count);
        }

        /// <summary>Fila compacta para descubrimiento (lo mínimo para decidir; el contrato completo va aparte).</summary>
        public static List<object> Row(CapabilityEntry c) => new List<object> { c.Id, c.Name, c.Status.ToString(), c.Strategy ?? "none" };
        public static readonly List<object> RowColumns = new List<object> { "id", "capacidad", "estado", "estrategia" };

        /// <summary>Contrato completo (sólo bajo demanda).</summary>
        public static Dictionary<string, object> Contract(CapabilityEntry c) => new Dictionary<string, object>
        {
            ["id"] = c.Id, ["dominio"] = c.Domain, ["subdominio"] = c.Subdomain, ["nombre"] = c.Name, ["nombre_en"] = c.NameEn, ["operacion"] = c.Operation,
            ["resultado_esperado"] = c.Expected, ["categorias"] = (c.Categories ?? new string[0]).Cast<object>().ToList(), ["documento"] = c.DocumentKind,
            ["estrategia"] = c.Strategy, ["ejecutor"] = ExecutorOf(c), ["api"] = (c.ApiSurfaces ?? new string[0]).Cast<object>().ToList(),
            ["versiones"] = c.MinVersion + "–" + c.MaxVersion, ["estado"] = c.Status.ToString(), ["implementacion"] = (c.Implementation ?? new string[0]).Cast<object>().ToList(),
            ["pruebas"] = (c.Tests ?? new string[0]).Cast<object>().ToList(), ["verificador"] = c.Verifier, ["idempotencia"] = c.Idempotency, ["recuperacion"] = c.Recovery,
            ["limitaciones"] = c.Limitations, ["comandos_cinta"] = (c.PostableCommands ?? new string[0]).Cast<object>().ToList(), ["prioridad"] = c.Priority,
            ["verificacion_nativa"] = c.Status == CoverageStatus.NATIVE_VERIFIED ? "si" : "pendiente",
        };

        /// <summary>Ejecutor oficial de la directiva (Capability → Executor) derivado de la estrategia.</summary>
        public static string ExecutorOf(CapabilityEntry c)
        {
            switch (StrategyKind(c.Strategy))
            {
                case "typed": return c.Strategy != null && c.Strategy.Contains("+") ? "TYPED_COMPOSITE" : "DIRECT_REVIT_API";
                case "query": case "doc": case "fam": return "DIRECT_REVIT_API";
                case "exec": return "DYNAMIC_CSHARP";
                case "engine": return c.Implementation != null && c.Implementation.Any(f => f.Contains("PowerEngine/Workflows") || f.Contains("Pdf")) ? "PYTHON_DOCUMENT" : "ENGINE_INTERNAL";   // garantía del motor: no ejecuta nada en Revit
                case "rust": return "RUST_GEOMETRY";
                case "postcommand": return c.Status == CoverageStatus.UNSUPPORTED_BY_PUBLIC_API ? "MANUAL_HANDOFF" : "NATIVE_UI_ASSISTED";
                case "manual": return "MANUAL_HANDOFF";
                default: return "UNAVAILABLE";
            }
        }
    }
}
