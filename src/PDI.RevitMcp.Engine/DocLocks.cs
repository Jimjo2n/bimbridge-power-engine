// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Coordinación por documento (0.6.0, §4). Una tarea larga o una barrera (guardar, sync, exportar, cargar familia) toma el documento;
    /// mientras lo tenga, las escrituras de otros clientes devuelven documento_ocupado en vez de intercalarse. Las lecturas no se bloquean.
    /// - Todo o nada y en orden (claves ordenadas): un script multidocumento nunca se queda con la mitad, y dos dueños no se bloquean
    ///   mutuamente (no hay interbloqueo).
    /// - Reentrante para el mismo dueño (la tarea puede ejecutar sus barreras).
    /// </summary>
    public sealed class DocLocks
    {
        public sealed class Holder
        {
            public string Owner;
            public string Kind;
            public string Detail;
            public DateTime SinceUtc;
            internal int Depth;

            public string Describe() => Kind + " " + Owner + (string.IsNullOrEmpty(Detail) ? "" : " (" + Detail + ")") +
                                        " desde " + SinceUtc.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Holder> _held = new Dictionary<string, Holder>(StringComparer.Ordinal);
        private readonly Func<DateTime> _now;

        public DocLocks(Func<DateTime> now = null) { _now = now ?? (() => DateTime.UtcNow); }

        /// <summary>Intenta tomar todos los documentos a la vez. Si alguno lo tiene otro dueño, no toma ninguno y devuelve quién lo tiene.</summary>
        public bool TryAcquire(IEnumerable<string> docs, string owner, string kind, string detail, out Holder blocking)
        {
            blocking = null;
            var keys = Normalize(docs);
            lock (_lock)
            {
                foreach (var k in keys)
                    if (_held.TryGetValue(k, out var h) && h.Owner != owner) { blocking = h; return false; }
                foreach (var k in keys)
                {
                    if (_held.TryGetValue(k, out var h)) h.Depth++;
                    else _held[k] = new Holder { Owner = owner, Kind = kind, Detail = detail, SinceUtc = _now(), Depth = 1 };
                }
                return true;
            }
        }

        /// <summary>Espera (acotada) a poder tomar los documentos. Comprueba cada 100 ms; nunca deja una toma a medias.</summary>
        public async Task<Holder> AcquireAsync(IEnumerable<string> docs, string owner, string kind, string detail, TimeSpan wait, CancellationToken ct)
        {
            var keys = Normalize(docs);
            var until = DateTime.UtcNow + wait;
            while (true)
            {
                if (TryAcquire(keys, owner, kind, detail, out var blocking)) return null;
                if (DateTime.UtcNow >= until) return blocking;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        public void Release(IEnumerable<string> docs, string owner)
        {
            lock (_lock)
            {
                foreach (var k in Normalize(docs))
                    if (_held.TryGetValue(k, out var h) && h.Owner == owner && --h.Depth <= 0) _held.Remove(k);
            }
        }

        /// <summary>Quién tiene el documento, o null. Si <paramref name="exceptOwner"/> lo tiene, también null.</summary>
        public Holder HeldByOther(string doc, string exceptOwner)
        {
            if (doc == null) return null;
            lock (_lock) return _held.TryGetValue(doc, out var h) && h.Owner != exceptOwner ? h : null;
        }

        public List<KeyValuePair<string, Holder>> All()
        {
            lock (_lock) return _held.ToList();
        }

        private static List<string> Normalize(IEnumerable<string> docs) =>
            (docs ?? Enumerable.Empty<string>()).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList();
    }
}
