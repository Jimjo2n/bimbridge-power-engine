// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using PDI.RevitMcp.Engine;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Registro de versiones por documento (sección 5.3). Se suscribe UNA vez por sesión de Revit
    /// a eventos baratos, en vez de 10 eventos por cada llamada como pyRevit (D2).
    /// Atribuye cada cambio: transacciones con prefijo "PDI MCP" → la sesión que escribía;
    /// cualquier otra (operator a mano, Undo, otro add-in) → "usuario".
    /// </summary>
    public sealed class DocumentTracker
    {
        public const string TransactionPrefix = PDI.RevitMcp.Protocol.Brand.TransactionPrefix;

        private readonly object _lock = new object();
        private readonly Dictionary<string, VersionRegistry> _docs = new Dictionary<string, VersionRegistry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sesión que está escribiendo ahora mismo (la fija el ejecutor de escrituras, fase 3).</summary>
        public volatile string CurrentWriter;

        /// <summary>Prefijo de los cambios temporales que se deshacen solos (captura con resaltado): no cuentan como cambios del modelo.</summary>
        public const string TemporaryPrefix = TransactionPrefix + " temporal";

        private int _suspended;

        /// <summary>Mientras dure el bloque, los eventos de cambio se ignoran (cambios temporales que se revierten en el mismo tramo).</summary>
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
            // 0.6.0: clave duradera (cloud GUID, ruta normalizada, o "nuevo:instancia#n" sin guardar). Nunca el título.
            return DocRegistry.Instance.IdentityOf(doc)?.Key;
        }

        /// <summary>Clave del registro de revisiones: el handle de esta apertura (sobrevive a Guardar como dentro de la misma apertura).</summary>
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

        /// <summary>Última vez que se guardó cada documento en esta sesión (doc guardar lo verifica con esto y con el archivo).</summary>
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

        /// <summary>Guardar como cambió la clave duradera (anterior, nueva): el diario traslada las operaciones.</summary>
        public event Action<string, string> DocumentRekeyed;

        /// <summary>Un documento se guardó (bool = Guardar como). Lo usan las tareas como evidencia de persistencia.</summary>
        public event Action<Document, bool> Saved;

        /// <summary>Un documento se sincronizó con el central.</summary>
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
                // Desde 0.5.0 también las familias: sus cambios cuentan para conflictos y para "lo que cambió".
                if (doc == null) return;
                var names = e.GetTransactionNames();
                // Cambios temporales ya revertidos (o su reversión): no tocan rev ni conjuntos.
                if (names != null && names.Count > 0 && names.All(PDI.RevitMcp.Protocol.Brand.IsTemporaryTransaction)) return;
                // También el prefijo heredado "PDI MCP" (Deshacer/Rehacer de cambios hechos con versiones anteriores).
                var ours = names != null && names.Any(PDI.RevitMcp.Protocol.Brand.IsOwnTransaction);
                var author = ours ? (CurrentWriter ?? VersionRegistry.HumanAuthor) : VersionRegistry.HumanAuthor;
                For(doc).Record(
                    e.GetAddedElementIds().Select(Ids.ToLong),
                    e.GetModifiedElementIds().Select(Ids.ToLong),
                    e.GetDeletedElementIds().Select(Ids.ToLong),
                    author);
            }
            catch (Exception)
            {
                // Nunca dejar escapar una excepción dentro de un evento de Revit.
            }
        }

        private void OnSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                // Un Sync puede traer cambios de otros usuarios sin detalle por elemento: por precaución,
                // todo lo conocido se marca como tocado (sección 9.4).
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

        /// <summary>Para invalidar conjuntos y planes de ese documento.</summary>
        public event Action<string> DocumentClosed;

        /// <summary>Handle dN del documento que se cerró (para borrar sus conjuntos en todas las sesiones).</summary>
        public event Action<string> DocumentHandleClosed;
    }
}
