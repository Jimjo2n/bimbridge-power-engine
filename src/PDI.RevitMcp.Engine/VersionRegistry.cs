// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Registro de versiones de un documento (sección 5.3 de la arquitectura).
    /// Cada evento DocumentChanged sube la revisión global y marca los elementos tocados.
    /// Sirve para: respuestas con "rev", consultas "since", y detectar conflictos entre
    /// el momento en que se planificó un cambio y el momento del commit.
    /// </summary>
    public sealed class VersionRegistry
    {
        /// <summary>Autor usado cuando el cambio no lo hizo ninguna sesión del MCP (operator a mano, Undo, otro add-in).</summary>
        public const string HumanAuthor = "usuario";

        private readonly object _lock = new object();
        private readonly Dictionary<long, Entry> _elements = new Dictionary<long, Entry>();
        private long _rev;
        /// <summary>Altas en orden (rev, id): permite saber qué creó una unidad sin recorrer todo el registro.</summary>
        private readonly List<KeyValuePair<long, long>> _added = new List<KeyValuePair<long, long>>();

        private struct Entry
        {
            public long Rev;
            public string Author;
            public bool Deleted;
        }

        public long Rev { get { lock (_lock) return _rev; } }

        /// <summary>Registra un cambio. Devuelve la nueva revisión global.</summary>
        public long Record(IEnumerable<long> added, IEnumerable<long> modified, IEnumerable<long> deleted, string author)
        {
            lock (_lock)
            {
                _rev++;
                var who = string.IsNullOrEmpty(author) ? HumanAuthor : author;
                if (added != null) foreach (var id in added) { _elements[id] = new Entry { Rev = _rev, Author = who }; _added.Add(new KeyValuePair<long, long>(_rev, id)); }
                if (modified != null) foreach (var id in modified) _elements[id] = new Entry { Rev = _rev, Author = who };
                if (deleted != null) foreach (var id in deleted) _elements[id] = new Entry { Rev = _rev, Author = who, Deleted = true };
                return _rev;
            }
        }

        /// <summary>Revisión del último cambio de un elemento (0 si no cambió desde que se abrió el documento).</summary>
        public long ElementRev(long id)
        {
            lock (_lock) return _elements.TryGetValue(id, out var e) ? e.Rev : 0;
        }

        /// <summary>Instantánea para un plan: id → rev actual.</summary>
        public Dictionary<long, long> Snapshot(IEnumerable<long> ids)
        {
            var d = new Dictionary<long, long>();
            lock (_lock)
                foreach (var id in ids)
                    d[id] = _elements.TryGetValue(id, out var e) ? e.Rev : 0;
            return d;
        }

        /// <summary>Elementos que cambiaron después de una instantánea. Vacío = sin conflicto.</summary>
        public List<Conflict> FindConflicts(IDictionary<long, long> snapshot)
        {
            var list = new List<Conflict>();
            lock (_lock)
            {
                foreach (var kv in snapshot)
                {
                    if (_elements.TryGetValue(kv.Key, out var e) && e.Rev != kv.Value)
                        list.Add(new Conflict(kv.Key, e.Author, e.Rev, e.Deleted));
                }
            }
            return list;
        }

        /// <summary>Ids creados después de una revisión (y que no se borraron después).</summary>
        public List<long> AddedSince(long rev)
        {
            var list = new List<long>();
            lock (_lock)
            {
                for (var i = _added.Count - 1; i >= 0 && _added[i].Key > rev; i--)
                {
                    var id = _added[i].Value;
                    if (_elements.TryGetValue(id, out var e) && !e.Deleted) list.Add(id);
                }
            }
            list.Reverse();
            return list;
        }

        /// <summary>Cuántos elementos se tocaron después de una revisión: (cambiados, borrados).</summary>
        public KeyValuePair<long, long> CountSince(long rev)
        {
            long ch = 0, del = 0;
            lock (_lock)
                foreach (var kv in _elements)
                    if (kv.Value.Rev > rev) { if (kv.Value.Deleted) del++; else ch++; }
            return new KeyValuePair<long, long>(ch, del);
        }

        /// <summary>Ids cambiados después de una revisión (para consultas "since").</summary>
        public HashSet<long> ChangedSince(long rev, out HashSet<long> deleted)
        {
            var changed = new HashSet<long>();
            deleted = new HashSet<long>();
            lock (_lock)
                foreach (var kv in _elements)
                    if (kv.Value.Rev > rev)
                    {
                        if (kv.Value.Deleted) deleted.Add(kv.Key);
                        else changed.Add(kv.Key);
                    }
            return changed;
        }

        /// <summary>
        /// Invalidación general (por ejemplo, tras un Sync que trae cambios de otros usuarios
        /// sin detalle por elemento): sube la revisión y marca todo lo conocido como tocado.
        /// </summary>
        public long BumpAll(string author)
        {
            lock (_lock)
            {
                _rev++;
                var keys = new List<long>(_elements.Keys);
                foreach (var k in keys)
                {
                    var e = _elements[k];
                    e.Rev = _rev;
                    e.Author = author;
                    _elements[k] = e;
                }
                AllBumpedAt = _rev;
                return _rev;
            }
        }

        /// <summary>Si &gt; 0, cualquier instantánea anterior a esta revisión está vencida aunque el elemento no figure.</summary>
        public long AllBumpedAt { get; private set; }
    }

    public sealed class Conflict
    {
        public Conflict(long id, string author, long rev, bool deleted)
        {
            Id = id; Author = author; Rev = rev; Deleted = deleted;
        }

        public long Id { get; }
        public string Author { get; }
        public long Rev { get; }
        public bool Deleted { get; }

        /// <summary>Texto para la IA, sin hablar de puertos ni sesiones internas.</summary>
        public string Describe(string mySessionId)
        {
            string who;
            if (Author == VersionRegistry.HumanAuthor) who = "el usuario en Revit";
            else if (Author == mySessionId) who = "esta misma conversación";
            else who = "otro agente";
            return Id + (Deleted ? " fue borrado por " : " fue cambiado por ") + who;
        }
    }
}
