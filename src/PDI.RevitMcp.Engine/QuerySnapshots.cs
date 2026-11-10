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
        internal Func<long> LiveRev;
        internal DateTime LastUseUtc;
    }

    /// <summary>
    /// Instantáneas de consultas paginadas (issue #2). La primera página lee el modelo UNA vez en el hilo de Revit; las páginas
    /// siguientes se sirven con "query cursor=… page=N" desde estas filas, sin volver a Revit.
    /// Garantías: un cursor sólo vale para la sesión que lo creó, para el mismo documento y mientras su revisión no cambie
    /// (si cambió, se rechaza con Stale y se descarta: nunca se mezclan filas de dos revisiones). Vencen por inactividad (TTL)
    /// y hay un presupuesto global de memoria en celdas (filas × campos); al pasarlo se descartan las menos usadas.
    /// </summary>
    public sealed class QuerySnapshotStore
    {
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
        public const long DefaultMaxCells = 1000000;
        public const int DefaultMaxPerSession = 4;

        private readonly object _lock = new object();
        private readonly Dictionary<string, QuerySnapshot> _items = new Dictionary<string, QuerySnapshot>(StringComparer.Ordinal);
        private readonly TimeSpan _ttl;
        private readonly long _maxCells;
        private readonly int _maxPerSession;
        private readonly Func<DateTime> _clock;
        private long _cells;

        public QuerySnapshotStore(TimeSpan? ttl = null, long maxCells = DefaultMaxCells, int maxPerSession = DefaultMaxPerSession, Func<DateTime> clock = null)
        {
            _ttl = ttl ?? DefaultTtl;
            _maxCells = maxCells;
            _maxPerSession = Math.Max(1, maxPerSession);
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int Count { get { lock (_lock) return _items.Count; } }
        public long Cells { get { lock (_lock) return _cells; } }
        public TimeSpan Ttl => _ttl;

        /// <summary>
        /// Guarda la instantánea y devuelve su cursor; null si sola no cabe en el presupuesto (el cliente sigue pudiendo paginar
        /// sin cursor, como antes). <paramref name="liveRev"/> devuelve la revisión ACTUAL del documento (hilo seguro).
        /// </summary>
        public string Put(string sessionId, string docId, long rev, QuerySpec spec, IReadOnlyList<Dictionary<string, object>> rows, Func<long> liveRev)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId");
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (liveRev == null) throw new ArgumentNullException(nameof(liveRev));
            var cells = (long)rows.Count * Math.Max(1, spec.Fields.Count);
            if (cells > _maxCells) return null;
            var snap = new QuerySnapshot
            {
                Id = NewId(), SessionId = sessionId, DocId = docId, Rev = rev, Spec = spec, Rows = rows, Cells = cells,
                LiveRev = liveRev, LastUseUtc = _clock(),
            };
            lock (_lock)
            {
                PruneExpired();
                // Límite por sesión: se descartan sus instantáneas menos usadas.
                var mine = _items.Values.Where(s => s.SessionId == sessionId).OrderBy(s => s.LastUseUtc).ToList();
                for (var i = 0; i <= mine.Count - _maxPerSession; i++) Remove(mine[i].Id);
                // Presupuesto global: LRU.
                while (_cells + cells > _maxCells && _items.Count > 0)
                    Remove(_items.Values.OrderBy(s => s.LastUseUtc).First().Id);
                _items[snap.Id] = snap;
                _cells += cells;
            }
            return snap.Id;
        }

        /// <summary>Busca un cursor para <paramref name="sessionId"/>. <paramref name="targetDocId"/> null = sin destino explícito.</summary>
        public SnapshotLookup Get(string id, string sessionId, string targetDocId, out QuerySnapshot snapshot)
        {
            snapshot = null;
            QuerySnapshot s;
            lock (_lock)
            {
                PruneExpired();
                if (string.IsNullOrEmpty(id) || !_items.TryGetValue(id, out s) || s.SessionId != sessionId) return SnapshotLookup.Unknown;
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
                    Remove(s.Id);
                    return SnapshotLookup.Stale;
                }
                s.LastUseUtc = _clock();
            }
            snapshot = s;
            return SnapshotLookup.Ok;
        }

        public void DropSession(string sessionId)
        {
            lock (_lock) foreach (var s in _items.Values.Where(x => x.SessionId == sessionId).ToList()) Remove(s.Id);
        }

        public void DropDocument(string docId)
        {
            lock (_lock)
                foreach (var s in _items.Values.Where(x => string.Equals(x.DocId, docId, StringComparison.OrdinalIgnoreCase)).ToList()) Remove(s.Id);
        }

        private void PruneExpired()
        {
            var now = _clock();
            foreach (var s in _items.Values.Where(x => now - x.LastUseUtc > _ttl).ToList()) Remove(s.Id);
        }

        private void Remove(string id)
        {
            if (_items.TryGetValue(id, out var s)) { _items.Remove(id); _cells -= s.Cells; }
        }

        private static string NewId()
        {
            var b = new byte[9];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return "q" + BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }
    }
}
