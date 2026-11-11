// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit.Families
{
    /// <summary>
    /// 0.9.1 (Bloque G): exportaciones tipadas y verificadas (doc accion=ifc | tabla_csv). Escriben archivos fuera del modelo
    /// (concesión Exportar) y no cambian el modelo: la exportación IFC, que Revit exige dentro de una transacción, se revierte siempre.
    /// La verificación (archivo nuevo, tamaño, hash; filas de la tabla) se hace después, como en doc pdf.
    /// </summary>
    public static class ExportOps
    {
        private static string Folder(IDictionary<string, object> a)
        {
            var folder = Environment.ExpandEnvironmentVariables(Arg.GetString(a, "carpeta") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            if (!Path.IsPathRooted(folder)) throw new OpFail(Status.Invalido, "carpeta= necesita una ruta completa");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string FileName(IDictionary<string, object> a, string fallback, string ext)
        {
            var name = Arg.GetString(a, "nombre");
            if (string.IsNullOrWhiteSpace(name)) name = fallback + "_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            name = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ext.Length);
            if (name.Length == 0) throw new OpFail(Status.Invalido, "nombre= no válido");
            return name;
        }

        private static void NotExisting(string target, IDictionary<string, object> a)
        {
            if (File.Exists(target) && !(Arg.GetBool(a, "sobrescribir") ?? false))
                throw new OpFail(Status.NecesitaUsuario, "ya existe " + target + "; pase sobrescribir=si o otro nombre=");
        }

        /// <summary>Versiones IFC admitidas por nombre corto (las que existen en IFCVersion en 2022–2026).</summary>
        private static IFCVersion IfcVersion(string v)
        {
            switch ((v ?? "ifc2x3").Trim().ToLowerInvariant())
            {
                case "ifc2x3": case "2x3": return IFCVersion.IFC2x3CV2;
                case "ifc4": case "ifc4rv": case "4": return IFCVersion.IFC4RV;
                case "ifc4dtv": return IFCVersion.IFC4DTV;
                default: throw new OpFail(Status.Invalido, "version: ifc2x3 (por defecto), ifc4 (reference view) o ifc4dtv");
            }
        }

        public static Dictionary<string, object> Ifc(Document doc, IDictionary<string, object> a, DocOps.Pending pend, List<string> diffs)
        {
            if (doc.IsFamilyDocument) throw new OpFail(Status.NoSoportado, "doc ifc exporta proyectos");
            var folder = Folder(a);
            var name = FileName(a, doc.Title, ".ifc");
            var target = Path.Combine(folder, name + ".ifc");
            NotExisting(target, a);
            var o = new IFCExportOptions { FileVersion = IfcVersion(Arg.GetString(a, "version")), ExportBaseQuantities = Arg.GetBool(a, "cantidades") ?? false };
            var viewArg = Arg.GetString(a, "vista");
            View view = null;
            if (viewArg != null)
            {
                view = long.TryParse(viewArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vid) ? doc.GetElement(Ids.ToId(vid)) as View
                       : new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewArg, StringComparison.OrdinalIgnoreCase));
                if (view == null) throw new OpFail(Status.Invalido, "no existe la vista '" + viewArg + "'");
                o.FilterViewId = view.Id;
            }
            pend.StartUtc = DateTime.UtcNow;
            bool ok;
            // Revit exige una transacción abierta para exportar IFC; se revierte siempre: el modelo no cambia.
            using (var t = new Transaction(doc, "BIMBRIDGE exportar IFC (se revierte)"))
            {
                t.Start();
                try { ok = doc.Export(folder, name, o); }
                finally { if (t.GetStatus() == TransactionStatus.Started) t.RollBack(); }
            }
            if (!ok) diffs.Add("Revit devolvió false en Export IFC");
            pend.Files.Add(target);
            return new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc), ["accion"] = "ifc", ["archivo"] = target, ["version"] = o.FileVersion.ToString(),
                ["vista"] = view?.Name ?? "modelo completo", ["cantidades_base"] = o.ExportBaseQuantities, ["modelo_modificado"] = false,
            };
        }

        public static Dictionary<string, object> ScheduleCsv(Document doc, IDictionary<string, object> a, DocOps.Pending pend, List<string> diffs)
        {
            var spec = Arg.GetString(a, "tabla") ?? throw new OpFail(Status.Invalido, "doc tabla_csv necesita tabla= (nombre o id de la tabla)");
            var vs = long.TryParse(spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? doc.GetElement(Ids.ToId(id)) as ViewSchedule
                     : new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, spec, StringComparison.OrdinalIgnoreCase));
            if (vs == null) throw new OpFail(Status.Invalido, "no existe la tabla '" + spec + "'");
            var folder = Folder(a);
            var name = FileName(a, vs.Name, ".csv");
            var target = Path.Combine(folder, name + ".csv");
            NotExisting(target, a);
            var o = new ViewScheduleExportOptions
            {
                FieldDelimiter = ",",
                TextQualifier = ExportTextQualifier.DoubleQuote,
                Title = false,
                HeadersFootersBlanks = Arg.GetBool(a, "cabeceras") ?? true,
            };
            var body = vs.GetTableData().GetSectionData(SectionType.Body);
            var bodyRows = body?.NumberOfRows ?? 0;
            pend.StartUtc = DateTime.UtcNow;
            vs.Export(folder, name + ".csv", o);
            pend.Files.Add(target);
            if (File.Exists(target))
            {
                var lines = File.ReadAllLines(target).Count(l => l.Trim().Length > 0);
                if (lines < bodyRows - 1) diffs.Add("el CSV tiene " + lines + " líneas y la tabla " + bodyRows + " filas de cuerpo");
            }
            return new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc), ["accion"] = "tabla_csv", ["tabla"] = vs.Name, ["archivo"] = target,
                ["filas_cuerpo"] = (long)bodyRows, ["columnas"] = (long)(body?.NumberOfColumns ?? 0),
            };
        }
    }
}
