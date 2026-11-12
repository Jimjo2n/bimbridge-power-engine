// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Engine.Workflows;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// 0.9.1 (Bloque F, #24): inventario de SOLO LECTURA para Copy/Monitor: vínculo (cargado, transformación), aparatos de origen en
    /// el documento vinculado y aparatos del anfitrión con su relación de supervisión. No invoca comandos ni abre transacciones.
    /// La comparación la hace Engine.Workflows.CopyMonitorVerifier fuera del hilo de Revit.
    /// </summary>
    public sealed class CopyMonitorProbe
    {
        private readonly string _link;
        private readonly string _category;

        public string Error;
        public long Rev;
        public long LinkInstanceId;
        public string LinkName;
        public string LinkIdentity;
        /// <summary>AUD-20: revisión guardada del documento vinculado (GUID de versión + número de guardados), o null si no se pudo leer.</summary>
        public string LinkRevision;
        public string Category => _category;
        public LinkTransform Transform;
        public readonly List<SourceFixture> Source = new List<SourceFixture>();
        public readonly List<HostFixture> Host = new List<HostFixture>();
        public string DocumentIdentity;
        public string RevitVersion;

        public CopyMonitorProbe(string link, string category)
        {
            _link = (link ?? "").Trim();
            _category = string.IsNullOrWhiteSpace(category) ? "PlumbingFixtures" : category.Trim();
        }

        private static string Key(Element e, out string fam, out string type)
        {
            var fi = e as FamilyInstance;
            fam = fi?.Symbol?.FamilyName ?? "";
            type = fi?.Symbol?.Name ?? e.Name;
            return fam + ": " + type;
        }

        private static (XYZ P, double Rot) Where(Element e)
        {
            if (e.Location is LocationPoint lp) return (lp.Point, lp.Rotation * 180.0 / Math.PI);
            var bb = e.get_BoundingBox(null);
            return (bb == null ? XYZ.Zero : (bb.Min + bb.Max) / 2, 0);
        }

        public bool Step(UIApplication app, Stopwatch sw, int budgetMs, DocumentTracker tracker)
        {
            var doc = DocTarget.Resolve(app, out var docError);
            if (doc == null) { Error = docError; return true; }
            if (doc.IsFamilyDocument) { Error = Status.NoSoportado + ": copy_monitor revisa proyectos, no familias"; return true; }
            if (!QueryRunner.TryCategory(_category, out var bic)) { Error = Status.Invalido + ": no existe la categoría " + _category; return true; }
            Rev = tracker.For(doc)?.Rev ?? 0;
            RevitVersion = app.Application.VersionNumber;
            DocumentIdentity = DocRegistry.Instance.IdentityOf(doc)?.Key;

            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            RevitLinkInstance li = null;
            if (long.TryParse(_link, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lid)) li = links.FirstOrDefault(l => Ids.ToLong(l.Id) == lid);
            if (li == null)
            {
                var hits = links.Where(l => string.Equals(l.Name, _link, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals((doc.GetElement(l.GetTypeId()) as RevitLinkType)?.Name, _link, StringComparison.OrdinalIgnoreCase)
                                            || l.Name.StartsWith(_link + " : ", StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count > 1) { Error = Status.Invalido + ": hay " + hits.Count + " instancias del vínculo '" + _link + "'; use el id"; return true; }
                li = hits.FirstOrDefault();
            }
            if (li == null) { Error = Status.Invalido + ": no existe el vínculo '" + _link + "'. Vínculos: " + string.Join(", ", links.Select(l => Ids.ToLong(l.Id) + " " + l.Name).Take(20)); return true; }
            var ldoc = li.GetLinkDocument();
            if (ldoc == null) { Error = Status.Invalido + ": el vínculo '" + li.Name + "' no está cargado"; return true; }

            LinkInstanceId = Ids.ToLong(li.Id);
            LinkName = li.Name;
            // Identidad estable sin exponer la ruta: hash SHA-256 de la ruta (string.GetHashCode cambia entre procesos en .NET 8).
            LinkIdentity = PDI.RevitMcp.Engine.Home.Sha256Hex(ldoc.PathName ?? ldoc.Title).Substring(0, 16);
            try { var dv = Document.GetDocumentVersion(ldoc); LinkRevision = dv.VersionGUID.ToString("N") + "#" + dv.NumberOfSaves.ToString(CultureInfo.InvariantCulture); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { LinkRevision = null; }
            var t = li.GetTotalTransform();
            // AUD-11: transformación completa (rotación, reflexión, traslación); el verificador rechaza la que no pueda representar.
            Transform = new LinkTransform
            {
                Dx = t.Origin.X, Dy = t.Origin.Y, Dz = t.Origin.Z, RotationDeg = Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180.0 / Math.PI,
                BasisX = new[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z }, BasisY = new[] { t.BasisY.X, t.BasisY.Y, t.BasisY.Z }, BasisZ = new[] { t.BasisZ.X, t.BasisZ.Y, t.BasisZ.Z },
            };

            foreach (var e in new FilteredElementCollector(ldoc).OfCategory(bic).WhereElementIsNotElementType())
            {
                Key(e, out var fam, out var type);
                var (p, rot) = Where(e);
                Source.Add(new SourceFixture
                {
                    UniqueId = e.UniqueId, Category = _category, Family = fam, Type = type,
                    Level = (ldoc.GetElement(e.LevelId) as Level)?.Name, X = p.X, Y = p.Y, Z = p.Z, RotationDeg = rot,
                });
            }
            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                Key(e, out var fam, out var type);
                var (p, rot) = Where(e);
                var h = new HostFixture
                {
                    Id = Ids.ToLong(e.Id), Category = _category, Family = fam, Type = type, Level = (doc.GetElement(e.LevelId) as Level)?.Name,
                    X = p.X, Y = p.Y, Z = p.Z, RotationDeg = rot, Monitoring = e.IsMonitoringLinkElement(),
                };
                if (h.Monitoring) h.MonitoredLinkInstances.AddRange(e.GetMonitoredLinkElementIds().Select(Ids.ToLong));
                Host.Add(h);
            }
            return true;
        }
    }
}
