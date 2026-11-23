// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Version registry of a document (architecture section 5.3).
    /// Each DocumentChanged event bumps the global revision and marks the touched elements.
    /// Used for: responses with "rev", "since" queries, and detecting conflicts between
    /// the moment a change was planned and the moment of the commit.
    /// </summary>
    public sealed class VersionRegistry
    {
        /// <summary>Author used when the change was not made by any MCP session (operator by hand, Undo, another add-in).</summary>
        public const string HumanAuthor = "user";

        private readonly object _lock = new object();
        private readonly Dictionary<long, Entry> _elements = new Dictionary<long, Entry>();
        private long _rev;
        /// <summary>Additions in order (rev, id): lets us know what a unit created without walking the whole registry.</summary>
        private readonly List<KeyValuePair<long, long>> _added = new List<KeyValuePair<long, long>>();

        private struct Entry
        {
            public long Rev;
            public string Author;
            public bool Deleted;
        }

        public long Rev { get { lock (_lock) return _rev; } }

        /// <summary>Records a change. Returns the new global revision.</summary>
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

        /// <summary>Revision of an element's last change (0 if it has not changed since the document was opened).</summary>
        public long ElementRev(long id)
        {
            lock (_lock) return _elements.TryGetValue(id, out var e) ? e.Rev : 0;
        }

        /// <summary>Snapshot for a plan: id → current rev.</summary>
        public Dictionary<long, long> Snapshot(IEnumerable<long> ids)
        {
            var d = new Dictionary<long, long>();
            lock (_lock)
                foreach (var id in ids)
                    d[id] = _elements.TryGetValue(id, out var e) ? e.Rev : 0;
            return d;
        }

        /// <summary>Elements that changed after a snapshot. Empty = no conflict.</summary>
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

        /// <summary>Ids created after a revision (and not deleted afterwards).</summary>
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

        /// <summary>How many elements were touched after a revision: (changed, deleted).</summary>
        public KeyValuePair<long, long> CountSince(long rev)
        {
            long ch = 0, del = 0;
            lock (_lock)
                foreach (var kv in _elements)
                    if (kv.Value.Rev > rev) { if (kv.Value.Deleted) del++; else ch++; }
            return new KeyValuePair<long, long>(ch, del);
        }

        /// <summary>Ids changed after a revision (for "since" queries).</summary>
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
        /// General invalidation (for example, after a Sync that brings changes from other users
        /// with no per-element detail): bumps the revision and marks everything known as touched.
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

        /// <summary>If &gt; 0, any snapshot older than this revision is stale even if the element is not listed.</summary>
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

        /// <summary>Text for the AI, without mentioning ports or internal sessions.</summary>
        public string Describe(string mySessionId)
        {
            string who;
            if (Author == VersionRegistry.HumanAuthor) who = "the user in Revit";
            else if (Author == mySessionId) who = "this same conversation";
            else who = "another agent";
            return Id + (Deleted ? " was deleted by " : " was changed by ") + who;
        }
    }
}
