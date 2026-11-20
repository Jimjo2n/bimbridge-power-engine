// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Bimbridge.Engine;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// Per-document version registry (section 5.3). Subscribes ONCE per Revit session
    /// to cheap events, instead of 10 events per call like pyRevit (D2).
    /// Attributes each change: transactions with prefix "BIMBRIDGE" → the session that was writing;
    /// any other (operator by hand, Undo, another add-in) → "usuario".
    /// </summary>
    public sealed class DocumentTracker
    {
        public const string TransactionPrefix = Bimbridge.Protocol.Brand.TransactionPrefix;

        private readonly object _lock = new object();
        private readonly Dictionary<string, VersionRegistry> _docs = new Dictionary<string, VersionRegistry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Session that is writing right now (set by the write runner, phase 3).</summary>
        public volatile string CurrentWriter;

        /// <summary>Prefix of temporary changes that undo themselves (capture with highlighting): they do not count as model changes.</summary>
        public const string TemporaryPrefix = TransactionPrefix + " temporal";

        private int _suspended;

        /// <summary>While the block lasts, change events are ignored (temporary changes rolled back in the same slice).</summary>
        public IDisposable Suspend()
        {
            System.Threading.Interlocked.Increment(ref _suspended);
            return new Resume(this);
        }

        private sealed class Resume : IDisposable
        {
            private DocumentTracker _t;
            public Resume(DocumentTracker t) { _t = t; }
            public void Dispose()
            {
                var t = System.Threading.Interlocked.Exchange(ref _t, null);
                if (t != null) System.Threading.Interlocked.Decrement(ref t._suspended);
            }
        }

        public static string KeyOf(Document doc)
        {
            if (doc == null) return null;
            // 0.6.0: durable key (cloud GUID, normalized path, or "nuevo:instancia#n" when unsaved). Never the title.
            return DocRegistry.Instance.IdentityOf(doc)?.Key;
        }

        /// <summary>Revision registry key: the handle of this opening (survives Save As within the same opening).</summary>
        private static string RegKey(Document doc) => doc == null ? null : DocRegistry.Instance.IdOf(doc);

        public VersionRegistry For(Document doc)
        {
            var key = RegKey(doc);
            if (key == null) return null;
            lock (_lock)
            {
                if (!_docs.TryGetValue(key, out var reg))
                {
                    reg = new VersionRegistry();
                    _docs[key] = reg;
                }
                return reg;
            }
        }

        public void Attach(Autodesk.Revit.ApplicationServices.ControlledApplication app)
        {
            app.DocumentChanged += OnChanged;
            app.DocumentClosing += OnClosing;
            app.DocumentSynchronizedWithCentral += OnSynced;
            app.DocumentOpened += OnOpened;
            app.DocumentCreated += OnCreated;
            app.DocumentSaved += OnSaved;
            app.DocumentSavedAs += OnSavedAs;
        }

        private void OnOpened(object sender, DocumentOpenedEventArgs e) { try { DocRegistry.Instance.Touch(e.Document); } catch (Exception) { } }
        private void OnCreated(object sender, DocumentCreatedEventArgs e) { try { DocRegistry.Instance.Touch(e.Document); } catch (Exception) { } }

        /// <summary>Last time each document was saved in this session (doc guardar verifies with this and with the file).</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastSavedUtc =
            new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastSyncedUtc =
            new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private void OnSaved(object sender, DocumentSavedEventArgs e)
        {
            try
            {
                if (e.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded)
                {
                    LastSavedUtc[DocRegistry.Instance.IdOf(e.Document)] = DateTime.UtcNow;
                    Saved?.Invoke(e.Document, false);
                }
            }
            catch (Exception) { }
        }

        /// <summary>Save As changed the durable key (old, new): the journal moves the operations.</summary>
        public event Action<string, string> DocumentRekeyed;

        /// <summary>A document was saved (bool = Save As). Tasks use it as persistence evidence.</summary>
        public event Action<Document, bool> Saved;

        /// <summary>A document was synchronized with central.</summary>
        public event Action<Document> Synced;

        private void OnSavedAs(object sender, DocumentSavedAsEventArgs e)
        {
            try
            {
                if (e.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded)
                {
                    LastSavedUtc[DocRegistry.Instance.IdOf(e.Document)] = DateTime.UtcNow;
                    var change = DocRegistry.Instance.Reidentify(e.Document);
                    if (change != null) DocumentRekeyed?.Invoke(change.Item1, change.Item2);
                }
                DocRegistry.Instance.Touch(e.Document);
                Saved?.Invoke(e.Document, true);
            }
            catch (Exception) { }
        }

        public void Detach(Autodesk.Revit.ApplicationServices.ControlledApplication app)
        {
            app.DocumentChanged -= OnChanged;
            app.DocumentClosing -= OnClosing;
            app.DocumentSynchronizedWithCentral -= OnSynced;
            app.DocumentOpened -= OnOpened;
            app.DocumentCreated -= OnCreated;
            app.DocumentSaved -= OnSaved;
            app.DocumentSavedAs -= OnSavedAs;
        }

        private void OnChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                if (System.Threading.Volatile.Read(ref _suspended) > 0) return;
                var doc = e.GetDocument();
                // Since 0.5.0 families too: their changes count for conflicts and for "what changed".
                if (doc == null) return;
                var names = e.GetTransactionNames();
                // Temporary changes already rolled back (or their rollback): they do not touch rev or sets.
                if (names != null && names.Count > 0 && names.All(Bimbridge.Protocol.Brand.IsTemporaryTransaction)) return;
                var ours = names != null && names.Any(Bimbridge.Protocol.Brand.IsOwnTransaction);
                var author = ours ? (CurrentWriter ?? VersionRegistry.HumanAuthor) : VersionRegistry.HumanAuthor;
                For(doc).Record(
                    e.GetAddedElementIds().Select(Ids.ToLong),
                    e.GetModifiedElementIds().Select(Ids.ToLong),
                    e.GetDeletedElementIds().Select(Ids.ToLong),
                    author);
            }
            catch (Exception)
            {
                // Never let an exception escape inside a Revit event.
            }
        }

        private void OnSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                // A Sync can bring changes from other users without per-element detail: as a precaution,
                // everything known is marked as touched (section 9.4).
                For(e.Document)?.BumpAll(VersionRegistry.HumanAuthor);
                if (e.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded)
                {
                    LastSyncedUtc[DocRegistry.Instance.IdOf(e.Document)] = DateTime.UtcNow;
                    Synced?.Invoke(e.Document);
                }
            }
            catch (Exception) { }
        }

        private void OnClosing(object sender, DocumentClosingEventArgs e)
        {
            try
            {
                var key = KeyOf(e.Document);
                if (key == null) return;
                lock (_lock) _docs.Remove(RegKey(e.Document));
                var handle = DocRegistry.Instance.Forget(e.Document);
                DocumentClosed?.Invoke(key);
                if (handle != null) DocumentHandleClosed?.Invoke(handle);
            }
            catch (Exception) { }
        }

        /// <summary>To invalidate that document's sets and plans.</summary>
        public event Action<string> DocumentClosed;

        /// <summary>Handle dN of the document that closed (to delete its sets in all sessions).</summary>
        public event Action<string> DocumentHandleClosed;
    }
}
