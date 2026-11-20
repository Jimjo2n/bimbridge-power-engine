// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// Identity of open documents (R005, R007, A06, A25). Each open project or family gets a short handle dN
    /// that is valid while it stays open in this Revit; the instance is identified by pid + start time.
    /// Two unsaved RFAs with the same title get different handles (Document objects are compared, not the title).
    /// Also keeps a read-only copy (handle, key, title, kind, active) to resolve the target outside the Revit thread.
    /// </summary>
    public sealed class DocRegistry
    {
        public static readonly DocRegistry Instance = new DocRegistry();

        public sealed class Info
        {
            public string Id;
            public string Key;
            public string Title;
            public string Path;
            public string Kind;
            public bool Active;
            /// <summary>Model fingerprint (see <see cref="Engine.DocIdentity"/>).</summary>
            public string Print;
            public string Generation;
        }

        private sealed class Entry
        {
            public string Id;
            public Document Doc;
            public int Seq;
            public Engine.DocIdentity Identity;
        }

        private readonly object _lock = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private int _next;
        private volatile List<Info> _snapshot = new List<Info>();

        /// <summary>pid.startTime: two Revits (or the same one restarted) never share an instance.</summary>
        public readonly string InstanceId;

        private DocRegistry()
        {
            var p = Process.GetCurrentProcess();
            InstanceId = p.Id.ToString(CultureInfo.InvariantCulture) + "." + p.StartTime.ToUniversalTime().ToString("HHmmss", CultureInfo.InvariantCulture);
        }

        public static string KindOf(Document doc) => doc == null ? null : doc.IsFamilyDocument ? "familia" : "proyecto";

        /// <summary>Document handle (created if new). Revit thread only.</summary>
        public string IdOf(Document doc)
        {
            if (doc == null) return null;
            lock (_lock)
            {
                foreach (var x in _entries)
                    if (Same(x.Doc, doc)) return x.Id;
                var id = "d" + (++_next).ToString(CultureInfo.InvariantCulture);
                var e = new Entry { Id = id, Doc = doc, Seq = _next };
                _entries.Add(e);
                e.Identity = Compute(doc, e.Seq);
                return id;
            }
        }

        /// <summary>Durable document identity (key, fingerprint, generation). Revit thread only.</summary>
        public Engine.DocIdentity IdentityOf(Document doc)
        {
            if (doc == null) return null;
            IdOf(doc);
            lock (_lock) return _entries.First(x => Same(x.Doc, doc)).Identity;
        }

        /// <summary>
        /// Recomputes the identity after Save As (the path changed). Returns (old, new) if the key changed, so the journal
        /// moves the operations from the old document to the new one.
        /// </summary>
        public Tuple<string, string> Reidentify(Document doc)
        {
            if (doc == null) return null;
            IdOf(doc);
            lock (_lock)
            {
                var e = _entries.First(x => Same(x.Doc, doc));
                var old = e.Identity;
                e.Identity = Compute(doc, e.Seq);
                _snapshot = _snapshot.Select(i => i.Id != e.Id ? i : new Info
                {
                    Id = i.Id, Key = e.Identity.Key, Title = doc.Title, Path = doc.PathName, Kind = i.Kind, Active = i.Active,
                    Print = e.Identity.Print, Generation = e.Identity.Generation,
                }).ToList();
                return old == null || old.Key == e.Identity.Key ? null : Tuple.Create(old.Key, e.Identity.Key);
            }
        }

        private Engine.DocIdentity Compute(Document doc, int seq)
        {
            var gen = Engine.DocIdentity.GenerationOf(InstanceId, seq);
            try
            {
                if (doc.IsModelInCloud)
                {
                    var mp = doc.GetCloudModelPath();
                    return Engine.DocIdentity.Cloud(mp.GetProjectGUID(), mp.GetModelGUID(), gen);
                }
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(doc.PathName)) return Engine.DocIdentity.Unsaved(InstanceId, seq, gen);
            return Engine.DocIdentity.File(doc.PathName, PrintOf(doc), gen);
        }

        /// <summary>Internal model fingerprint: does not change when the file is moved, does if another model is at that path.</summary>
        private static string PrintOf(Document doc)
        {
            try
            {
                if (doc.IsFamilyDocument)
                {
                    var f = doc.OwnerFamily;
                    return f == null ? null : "fam:" + f.UniqueId;
                }
                var pi = doc.ProjectInformation;
                var central = "";
                try { if (doc.IsWorkshared) central = ":" + doc.WorksharingCentralGUID.ToString("N"); } catch (Exception) { }
                return pi == null ? null : "rvt:" + pi.UniqueId + central;
            }
            catch (Exception) { return null; }
        }

        private Info MakeInfo(Document d, bool active)
        {
            var id = IdOf(d);
            var ident = IdentityOf(d);
            return new Info
            {
                Id = id, Key = ident.Key, Title = d.Title, Path = d.PathName, Kind = KindOf(d), Active = active,
                Print = ident.Print, Generation = ident.Generation,
            };
        }

        private static bool Same(Document a, Document b)
        {
            if (ReferenceEquals(a, b)) return true;
            try { return a != null && b != null && a.IsValidObject && b.IsValidObject && a.Equals(b); }
            catch (Exception) { return false; }
        }

        /// <summary>Document by handle, if still open. Revit thread only.</summary>
        public Document Resolve(UIApplication app, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Entry entry;
            lock (_lock) entry = _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            foreach (Document d in app.Application.Documents)
                if (Same(entry.Doc, d)) return d;
            return null;
        }

        /// <summary>Removes the closing document. Returns its handle (to invalidate sets and plans).</summary>
        public string Forget(Document doc)
        {
            lock (_lock)
            {
                var e = _entries.FirstOrDefault(x => Same(x.Doc, doc));
                if (e == null) return null;
                _entries.Remove(e);
                var snap = _snapshot.Where(i => i.Id != e.Id).ToList();
                _snapshot = snap;
                return e.Id;
            }
        }

        /// <summary>Recomputes the read-only copy with the open documents (Revit thread: events and ctx).</summary>
        public List<Info> Refresh(UIApplication app, Document activeHint = null)
        {
            var list = new List<Info>();
            Document active = activeHint;
            try { active = active ?? app?.ActiveUIDocument?.Document; } catch (Exception) { }
            if (app != null)
            {
                foreach (Document d in app.Application.Documents)
                {
                    if (d.IsLinked) continue;
                    list.Add(MakeInfo(d, active != null && Same(active, d)));
                }
            }
            _snapshot = list;
            return list;
        }

        /// <summary>Updates only which one is active (ViewActivated): does not scan documents.</summary>
        public void SetActive(Document doc)
        {
            if (doc == null) return;
            var id = IdOf(doc);
            var snap = _snapshot.Select(i => new Info { Id = i.Id, Key = i.Key, Title = i.Title, Path = i.Path, Kind = i.Kind, Active = i.Id == id, Print = i.Print, Generation = i.Generation }).ToList();
            if (!snap.Any(i => i.Id == id)) snap.Add(MakeInfo(doc, true));
            _snapshot = snap;
        }

        /// <summary>Adds a newly opened or created document to the copy (without changing which one is active).</summary>
        public void Touch(Document doc)
        {
            if (doc == null || doc.IsLinked) return;
            var id = IdOf(doc);
            if (_snapshot.Any(i => i.Id == id)) return;
            var snap = new List<Info>(_snapshot) { MakeInfo(doc, false) };
            _snapshot = snap;
        }

        /// <summary>Read-only copy (any thread).</summary>
        public List<Info> Snapshot => _snapshot;

        public Info Active => _snapshot.FirstOrDefault(i => i.Active);

        /// <summary>
        /// Resolves what the AI asked for (dN, path or title) outside the Revit thread. null + error if it does not exist or is ambiguous.
        /// </summary>
        public Info Find(string requested, out string error) => Find(requested, out error, out _);

        /// <summary>Like Find; <paramref name="ambiguous"/> = true when several matches prevent choosing (they are listed, the first is not picked).</summary>
        public Info Find(string requested, out string error, out bool ambiguous)
        {
            var snap = _snapshot;
            var cands = snap.Select(i => new Engine.DocResolver.Candidate { Id = i.Id, Key = i.Key, Title = i.Title, Path = i.Path, Active = i.Active }).ToList();
            var k = Engine.DocResolver.Find(cands, requested, out error, out ambiguous);
            return k >= 0 ? snap[k] : null;
        }

        public static bool TitleMatches(string title, string wanted) => Engine.DocResolver.TitleMatches(title, wanted);

        public int Count => _snapshot.Count;
    }

    /// <summary>
    /// Target of a request: decided ONCE when it is received (the active one at that moment, or the one asked for with doc=) and kept fixed
    /// across all slices (F04). If the document closes midway, the request fails with destino_no_disponible; it never moves to another one.
    /// It travels with the request via AsyncLocal and runners read it with <see cref="Current"/> inside the Revit thread.
    /// </summary>
    public sealed class DocTarget
    {
        public string Id;
        public string Key;
        public string Title;
        public string Kind;
        public string Print;
        public string Generation;
        /// <summary>true if the AI asked for it with doc=; false = the active one when the request was received.</summary>
        public bool Explicit;
        private Document _pinned;

        internal static readonly AsyncLocal<DocTarget> Ambient = new AsyncLocal<DocTarget>();
        [ThreadStatic] private static DocTarget _current;

        /// <summary>The target of the request now running on the Revit thread (null = previous behavior: the active one).</summary>
        public static DocTarget Current => _current;

        internal static IDisposable Enter(DocTarget t)
        {
            var prev = _current;
            _current = t;
            return new Restore(prev);
        }

        private sealed class Restore : IDisposable
        {
            private readonly DocTarget _prev;
            public Restore(DocTarget prev) { _prev = prev; }
            public void Dispose() { _current = _prev; }
        }

        public static DocTarget From(DocRegistry.Info i, bool explicitly) =>
            i == null ? null : new DocTarget { Id = i.Id, Key = i.Key, Title = i.Title, Kind = i.Kind, Print = i.Print, Generation = i.Generation, Explicit = explicitly };

        /// <summary>Pinned document (Revit thread). null if it is no longer open; reason in <paramref name="error"/>.</summary>
        public Document Get(UIApplication app, out string error)
        {
            error = null;
            if (_pinned != null)
            {
                try { if (_pinned.IsValidObject) return _pinned; } catch (Exception) { }
                _pinned = null;
                error = Status.DestinoNoDisponible + ": document " + Id + " (" + Title + ") was closed during the request; not continuing on another one";
                return null;
            }
            var d = DocRegistry.Instance.Resolve(app, Id);
            if (d == null)
            {
                error = Status.DestinoNoDisponible + ": document " + Id + " (" + Title + ") is no longer open; use docs to see the open ones";
                return null;
            }
            _pinned = d;
            return d;
        }

        /// <summary>
        /// What runners use instead of app.ActiveUIDocument.Document: the pinned target if any; otherwise, the active one.
        /// </summary>
        public static Document Resolve(UIApplication app, out string error)
        {
            error = null;
            var t = _current;
            if (t != null) return t.Get(app, out error);
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) error = "There is no active document in Revit.";
            return doc;
        }

        public static Document Resolve(UIApplication app) => Resolve(app, out _);

        /// <summary>UIDocument of the target only if it is the active one (captures, selection): the UI only exists for the active one.</summary>
        public static UIDocument ActiveUi(UIApplication app, out string error)
        {
            error = null;
            var uidoc = app.ActiveUIDocument;
            var t = _current;
            if (t == null || !t.Explicit) return uidoc;
            var d = t.Get(app, out error);
            if (d == null) return null;
            if (uidoc == null || !uidoc.Document.Equals(d))
            {
                error = Status.NoSoportado + ": this command uses the Revit UI and " + t.Id + " (" + t.Title + ") is not the active document; ask the user to activate it";
                return null;
            }
            return uidoc;
        }

        public string Describe() => Id + " " + Title;
    }
}
