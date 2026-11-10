// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace PDI.RevitMcp.Engine
{
    /// <summary>Resultado de buscar un cursor de consulta.</summary>
    public enum SnapshotLookup
    {
        Ok,
        /// <summary>No existe, venció o pertenece a otra sesión (no se distingue para no filtrar cursores ajenos).</summary>
        Unknown,
        /// <summary>El documento cambió de revisión desde que se tomó la instantánea: repetir la consulta.</summary>
        Stale,
        /// <summary>La petición apunta a otro documento distinto del de la instantánea.</summary>
        OtherDocument,
        /// <summary>Era de esta sesión pero se descartó (presupuesto de memoria, vencimiento o cierre del documento): repetir la consulta.</summary>
        Evicted,
    }

    /// <summary>Filas ya filtradas de una consulta, ligadas a sesión, documento y revisión (issue #2).</summary>
    public sealed class QuerySnapshot
    {
        public string Id { get; internal set; }
        public string SessionId { get; internal set; }
        public string DocId { get; internal set; }
        public long Rev { get; internal set; }
        public QuerySpec Spec { get; internal set; }
        public IReadOnlyList<Dictionary<string, object>> Rows { get; internal set; }
        public long Cells { get; internal set; }
        /// <summary>Tamaño ESTIMADO de las filas guardadas (ver <see cref="QuerySnapshotStore.EstimateBytes"/>). No es una medición del heap.</summary>
        public long EstimatedBytes { get; internal set; }
        internal Func<long> LiveRev;
        internal DateTime LastUseUtc;
    }

    /// <summary>
    /// Instantáneas de consultas paginadas (issue #2). La primera página lee el modelo UNA vez en el hilo de Revit; las páginas
    /// siguientes se sirven con "query cursor=… page=N" desde estas filas, sin volver a Revit.
    /// Garantías: un cursor sólo vale para la sesión que lo creó, para el mismo documento y mientras su revisión no cambie
    /// (si cambió, se rechaza con Stale y se descarta: nunca se mezclan filas de dos revisiones). Vencen por inactividad (TTL).
    ///
    /// Presupuesto de memoria (revisión PR #22): se mide en BYTES estimados, no en celdas. Una celda con un texto de 50 000 caracteres
    /// ya no cuenta como una celda de un número. Límites: global, por documento, por sesión (cantidad) y por instantánea. Se guarda una
    /// proyección compacta (sólo las columnas pedidas, valores simples; nunca objetos de la API de Revit). La cifra es una ESTIMACIÓN
    /// conservadora del tamaño gestionado (cabeceras de objeto, cadenas UTF-16, valores en caja, diccionarios); no es una medición del heap
    /// ni una garantía exacta. Un cursor descartado por presupuesto, vencimiento o cierre del documento se informa como tal (Evicted).
    /// </summary>
    public sealed class QuerySnapshotStore
    {
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
        public const long DefaultMaxBytes = 64L * 1024 * 1024;
        public const long DefaultMaxBytesPerDocument = 32L * 1024 * 1024;
        public const int DefaultMaxPerSession = 4;
        private const int EvictedMemory = 512;

        private readonly object _lock = new object();
        private readonly Dictionary<string, QuerySnapshot> _items = new Dictionary<string, QuerySnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, KeyValuePair<string, string>> _evicted = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        private readonly Queue<string> _evictedOrder = new Queue<string>();
        private readonly TimeSpan _ttl;
        private readonly long _maxBytes, _maxDocBytes;
        private readonly int _maxPerSession;
        private readonly Func<DateTime> _clock;
        private long _cells, _bytes;

        public QuerySnapshotStore(TimeSpan? ttl = null, long maxBytes = DefaultMaxBytes, long maxBytesPerDocument = DefaultMaxBytesPerDocument,
            int maxPerSession = DefaultMaxPerSession, Func<DateTime> clock = null)
        {
            _ttl = ttl ?? DefaultTtl;
            _maxBytes = maxBytes;
            _maxDocBytes = Math.Min(maxBytesPerDocument, maxBytes);
            _maxPerSession = Math.Max(1, maxPerSession);
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int Count { get { lock (_lock) return _items.Count; } }
        public long Cells { get { lock (_lock) return _cells; } }
        /// <summary>Suma de tamaños estimados de las instantáneas guardadas.</summary>
        public long EstimatedBytes { get { lock (_lock) return _bytes; } }
        public long MaxBytes => _maxBytes;
        public TimeSpan Ttl => _ttl;

        /// <summary>
        /// Estimación conservadora del tamaño gestionado de filas simples (x64): diccionario (cabecera, tablas) + por entrada (entrada y cubo)
        /// + por valor (cadena = cabecera + 2 bytes por carácter; número/booleano en caja; otros como su texto). Las claves compartidas
        /// (nombres de campo) no se cuentan por fila.
        /// </summary>
        public static long EstimateBytes(IReadOnlyList<Dictionary<string, object>> rows)
        {
            long total = 24 + 8L * rows.Count;                     // la lista y sus referencias
            foreach (var r in rows)
            {
                total += 80 + 32L * r.Count;                         // Dictionary<string,object>: objeto + arrays + entrada/cubo por clave
                foreach (var v in r.Values) total += ValueBytes(v);
            }
            return total;
        }

        private static long ValueBytes(object v)
        {
            switch (v)
            {
                case null: return 0;
                case string s: return 26 + 2L * s.Length;
                case long _: case int _: case double _: case bool _: case float _: case decimal _: return 24;
                default: return 32 + 2L * (Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)?.Length ?? 0);
            }
        }

        /// <summary>Proyección compacta: sólo las columnas que la página devolverá. Filtros y unidades ya se aplicaron.</summary>
        public static List<Dictionary<string, object>> Project(QuerySpec spec, IReadOnlyList<Dictionary<string, object>> rows)
        {
            var list = new List<Dictionary<string, object>>(rows.Count);
            foreach (var r in rows)
            {
                var d = new Dictionary<string, object>(spec.Fields.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var f in spec.Fields)
                {
                    if (r.TryGetValue(f, out var v)) { d[f] = v; continue; }
                    foreach (var kv in r) if (string.Equals(kv.Key, f, StringComparison.OrdinalIgnoreCase)) { d[f] = kv.Value; break; }
                }
                list.Add(d);
            }
            return list;
        }

        /// <summary>
        /// Guarda la instantánea y devuelve su cursor. null si no cabe en el presupuesto, con el motivo en <paramref name="refusal"/>
        /// (la respuesta lo comunica al agente; la paginación clásica sigue disponible). <paramref name="liveRev"/> devuelve la revisión
        /// ACTUAL del documento (hilo seguro).
        /// </summary>
        public string Put(string sessionId, string docId, long rev, QuerySpec spec, IReadOnlyList<Dictionary<string, object>> rows, Func<long> liveRev, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId");
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (liveRev == null) throw new ArgumentNullException(nameof(liveRev));
            var compact = Project(spec, rows);
            var bytes = EstimateBytes(compact);
            if (bytes > _maxDocBytes)
            {
                refusal = "el resultado ocupa ~" + Mb(bytes) + " MB estimados y supera el presupuesto de instantáneas (" + Mb(_maxDocBytes) +
                          " MB por documento): no hay cursor; acote la consulta (f=, where, group_by) o pagine sin cursor";
                return null;
            }
            var snap = new QuerySnapshot
            {
                Id = NewId(), SessionId = sessionId, DocId = docId, Rev = rev, Spec = spec, Rows = compact,
                Cells = (long)compact.Count * Math.Max(1, spec.Fields.Count), EstimatedBytes = bytes, LiveRev = liveRev, LastUseUtc = _clock(),
            };
            lock (_lock)
            {
                PruneExpired();
                // Límite por sesión: se descartan sus instantáneas menos usadas.
                var mine = _items.Values.Where(s => s.SessionId == sessionId).OrderBy(s => s.LastUseUtc).ToList();
                for (var i = 0; i <= mine.Count - _maxPerSession; i++) Remove(mine[i].Id, "límite de instantáneas por sesión");
                // Presupuesto por documento y global: LRU dentro del ámbito que se excede.
                while (_items.Values.Where(s => SameDoc(s.DocId, docId)).Sum(s => s.EstimatedBytes) + bytes > _maxDocBytes)
                    Remove(_items.Values.Where(s => SameDoc(s.DocId, docId)).OrderBy(s => s.LastUseUtc).First().Id, "presupuesto de memoria del documento");
                while (_bytes + bytes > _maxBytes && _items.Count > 0)
                    Remove(_items.Values.OrderBy(s => s.LastUseUtc).First().Id, "presupuesto global de memoria");
                _items[snap.Id] = snap;
                _cells += snap.Cells;
                _bytes += bytes;
            }
            return snap.Id;
        }

        private static bool SameDoc(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string Mb(long b) => (b / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Motivo del descarte de un cursor de esta sesión, si se recuerda (null si no).</summary>
        public string EvictionReason(string id, string sessionId)
        {
            lock (_lock) return id != null && _evicted.TryGetValue(id, out var e) && e.Key == sessionId ? e.Value : null;
        }

        /// <summary>Busca un cursor para <paramref name="sessionId"/>. <paramref name="targetDocId"/> null = sin destino explícito.</summary>
        public SnapshotLookup Get(string id, string sessionId, string targetDocId, out QuerySnapshot snapshot)
        {
            snapshot = null;
            QuerySnapshot s;
            lock (_lock)
            {
                PruneExpired();
                if (string.IsNullOrEmpty(id) || !_items.TryGetValue(id, out s) || s.SessionId != sessionId)
                    return id != null && _evicted.TryGetValue(id, out var ev) && ev.Key == sessionId ? SnapshotLookup.Evicted : SnapshotLookup.Unknown;
                if (targetDocId != null && s.DocId != null && !string.Equals(targetDocId, s.DocId, StringComparison.OrdinalIgnoreCase))
                    return SnapshotLookup.OtherDocument;
            }
            long live;
            try { live = s.LiveRev(); }
            catch (Exception) { live = long.MinValue; }   // no se puede comprobar: fail-closed
            lock (_lock)
            {
                if (live != s.Rev)
                {
                    Remove(s.Id, null);
                    return SnapshotLookup.Stale;
                }
                s.LastUseUtc = _clock();
            }
            snapshot = s;
            return SnapshotLookup.Ok;
        }

        public void DropSession(string sessionId)
        {
            lock (_lock) foreach (var s in _items.Values.Where(x => x.SessionId == sessionId).ToList()) Remove(s.Id, null);
        }

        public void DropDocument(string docId)
        {
            lock (_lock)
                foreach (var s in _items.Values.Where(x => string.Equals(x.DocId, docId, StringComparison.OrdinalIgnoreCase)).ToList()) Remove(s.Id, "el documento se cerró");
        }

        private void PruneExpired()
        {
            var now = _clock();
            foreach (var s in _items.Values.Where(x => now - x.LastUseUtc > _ttl).ToList()) Remove(s.Id, "vencido (" + (int)_ttl.TotalMinutes + " min sin uso)");
        }

        /// <param name="reason">Motivo que se recordará para informar al agente; null = no recordar (revisión cambiada o sesión cerrada).</param>
        private void Remove(string id, string reason)
        {
            if (!_items.TryGetValue(id, out var s)) return;
            _items.Remove(id); _cells -= s.Cells; _bytes -= s.EstimatedBytes;
            if (reason == null) return;
            _evicted[id] = new KeyValuePair<string, string>(s.SessionId, reason);
            _evictedOrder.Enqueue(id);
            while (_evictedOrder.Count > EvictedMemory) _evicted.Remove(_evictedOrder.Dequeue());
        }

        private static string NewId()
        {
            var b = new byte[9];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return "q" + BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }
    }
}
