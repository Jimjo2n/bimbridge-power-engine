// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Identidad de documentos abiertos (R005, R007, A06, A25). Cada proyecto o familia abierto recibe un handle corto dN
    /// que vale mientras siga abierto en este Revit; la instancia se identifica con pid + hora de arranque.
    /// Dos RFA sin guardar con el mismo título tienen handles distintos (se comparan los objetos Document, no el título).
    /// Mantiene además una copia de sólo lectura (handle, clave, título, tipo, activo) para resolver el destino fuera del hilo de Revit.
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
            /// <summary>Huella del modelo (ver <see cref="Engine.DocIdentity"/>).</summary>
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

        /// <summary>pid.horaDeArranque: dos Revit (o el mismo reiniciado) nunca comparten instancia.</summary>
        public readonly string InstanceId;

        private DocRegistry()
        {
            var p = Process.GetCurrentProcess();
            InstanceId = p.Id.ToString(CultureInfo.InvariantCulture) + "." + p.StartTime.ToUniversalTime().ToString("HHmmss", CultureInfo.InvariantCulture);
        }

        public static string KindOf(Document doc) => doc == null ? null : doc.IsFamilyDocument ? "familia" : "proyecto";

        /// <summary>Handle del documento (lo crea si es nuevo). Sólo en el hilo de Revit.</summary>
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

        /// <summary>Identidad duradera del documento (clave, huella, generación). Sólo en el hilo de Revit.</summary>
        public Engine.DocIdentity IdentityOf(Document doc)
        {
            if (doc == null) return null;
            IdOf(doc);
            lock (_lock) return _entries.First(x => Same(x.Doc, doc)).Identity;
        }

        /// <summary>
        /// Recalcula la identidad tras Guardar como (la ruta cambió). Devuelve (anterior, nueva) si cambió la clave, para que el diario
        /// traslade las operaciones del documento anterior al nuevo.
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

        /// <summary>Huella interna del modelo: no cambia al mover el archivo, sí si en esa ruta hay otro modelo.</summary>
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

        /// <summary>Documento por handle, si sigue abierto. Sólo en el hilo de Revit.</summary>
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

        /// <summary>Quita el documento que se cierra. Devuelve su handle (para invalidar conjuntos y planes).</summary>
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

        /// <summary>Recalcula la copia de sólo lectura con los documentos abiertos (hilo de Revit: eventos y ctx).</summary>
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

        /// <summary>Actualiza sólo cuál está activo (ViewActivated): no recorre documentos.</summary>
        public void SetActive(Document doc)
        {
            if (doc == null) return;
            var id = IdOf(doc);
            var snap = _snapshot.Select(i => new Info { Id = i.Id, Key = i.Key, Title = i.Title, Path = i.Path, Kind = i.Kind, Active = i.Id == id, Print = i.Print, Generation = i.Generation }).ToList();
            if (!snap.Any(i => i.Id == id)) snap.Add(MakeInfo(doc, true));
            _snapshot = snap;
        }

        /// <summary>Añade a la copia un documento recién abierto o creado (sin cambiar cuál está activo).</summary>
        public void Touch(Document doc)
        {
            if (doc == null || doc.IsLinked) return;
            var id = IdOf(doc);
            if (_snapshot.Any(i => i.Id == id)) return;
            var snap = new List<Info>(_snapshot) { MakeInfo(doc, false) };
            _snapshot = snap;
        }

        /// <summary>Copia de sólo lectura (cualquier hilo).</summary>
        public List<Info> Snapshot => _snapshot;

        public Info Active => _snapshot.FirstOrDefault(i => i.Active);

        /// <summary>
        /// Resuelve lo que pidió la IA (dN, ruta o título) fuera del hilo de Revit. null + error si no existe o es ambiguo.
        /// </summary>
        public Info Find(string requested, out string error) => Find(requested, out error, out _);

        /// <summary>Como Find; <paramref name="ambiguous"/> = true cuando varias coincidencias impiden elegir (se listan, no se elige la primera).</summary>
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
    /// Destino de una petición: se decide UNA vez al recibirla (el activo en ese momento, o el pedido con doc=) y se mantiene fijo
    /// en todos los tramos (F04). Si el documento se cierra a mitad, la petición falla con destino_no_disponible; nunca se pasa a otro.
    /// Viaja con la petición por AsyncLocal y los ejecutores lo leen con <see cref="Current"/> dentro del hilo de Revit.
    /// </summary>
    public sealed class DocTarget
    {
        public string Id;
        public string Key;
        public string Title;
        public string Kind;
        public string Print;
        public string Generation;
        /// <summary>true si la IA lo pidió con doc=; false = el activo al recibir la petición.</summary>
        public bool Explicit;
        private Document _pinned;

        internal static readonly AsyncLocal<DocTarget> Ambient = new AsyncLocal<DocTarget>();
        [ThreadStatic] private static DocTarget _current;

        /// <summary>El destino de la petición que corre ahora en el hilo de Revit (null = comportamiento anterior: el activo).</summary>
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

        /// <summary>Documento fijado (hilo de Revit). null si ya no está abierto; motivo en <paramref name="error"/>.</summary>
        public Document Get(UIApplication app, out string error)
        {
            error = null;
            if (_pinned != null)
            {
                try { if (_pinned.IsValidObject) return _pinned; } catch (Exception) { }
                _pinned = null;
                error = Status.DestinoNoDisponible + ": el documento " + Id + " (" + Title + ") se cerró durante la petición; no se continúa en otro";
                return null;
            }
            var d = DocRegistry.Instance.Resolve(app, Id);
            if (d == null)
            {
                error = Status.DestinoNoDisponible + ": el documento " + Id + " (" + Title + ") ya no está abierto; use docs para ver los abiertos";
                return null;
            }
            _pinned = d;
            return d;
        }

        /// <summary>
        /// Lo que usan los ejecutores en lugar de app.ActiveUIDocument.Document: el destino fijado si lo hay; si no, el activo.
        /// </summary>
        public static Document Resolve(UIApplication app, out string error)
        {
            error = null;
            var t = _current;
            if (t != null) return t.Get(app, out error);
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) error = "No hay ningún documento activo en Revit.";
            return doc;
        }

        public static Document Resolve(UIApplication app) => Resolve(app, out _);

        /// <summary>UIDocument del destino sólo si es el activo (capturas, selección): la interfaz sólo existe para el activo.</summary>
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
                error = Status.NoSoportado + ": esta orden usa la interfaz de Revit y " + t.Id + " (" + t.Title + ") no es el documento activo; pida al usuario que lo active";
                return null;
            }
            return uidoc;
        }

        public string Describe() => Id + " " + Title;
    }
}
