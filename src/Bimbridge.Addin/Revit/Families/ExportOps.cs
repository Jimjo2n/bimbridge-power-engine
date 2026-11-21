// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Families
{
    /// <summary>
    /// 0.9.1 (Block G): typed, verified exports (doc accion=ifc | tabla_csv). They write files outside the model
    /// (Export grant) and do not change the model: the IFC export, which Revit requires inside a transaction, is always rolled back.
    /// Verification (new file, size, hash; schedule rows) happens afterwards, as in doc pdf.
    /// </summary>
    public static class ExportOps
    {
        private static string Folder(IDictionary<string, object> a)
        {
            var folder = Environment.ExpandEnvironmentVariables(Arg.GetString(a, "folder") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            if (!Path.IsPathRooted(folder)) throw new OpFail(Status.Invalid, "folder= needs a full path");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string FileName(IDictionary<string, object> a, string fallback, string ext)
        {
            var name = Arg.GetString(a, "name");
            if (string.IsNullOrWhiteSpace(name)) name = fallback + "_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            name = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ext.Length);
            if (name.Length == 0) throw new OpFail(Status.Invalid, "invalid name=");
            return name;
        }

        private static void NotExisting(string target, IDictionary<string, object> a)
        {
            if (File.Exists(target) && !(Arg.GetBool(a, "overwrite") ?? false))
                throw new OpFail(Status.NeedsUser, target + " already exists; pass overwrite=yes or a different name=");
        }

        /// <summary>IFC versions accepted by short name (those that exist in IFCVersion in 2022–2026).</summary>
        private static IFCVersion IfcVersion(string v)
        {
            switch ((v ?? "ifc2x3").Trim().ToLowerInvariant())
            {
                case "ifc2x3": case "2x3": return IFCVersion.IFC2x3CV2;
                case "ifc4": case "ifc4rv": case "4": return IFCVersion.IFC4RV;
                case "ifc4dtv": return IFCVersion.IFC4DTV;
                default: throw new OpFail(Status.Invalid, "version: ifc2x3 (default), ifc4 (reference view) or ifc4dtv");
            }
        }

        public static Dictionary<string, object> Ifc(Document doc, IDictionary<string, object> a, DocOps.Pending pend, List<string> diffs)
        {
            if (doc.IsFamilyDocument) throw new OpFail(Status.NotSupported, "doc ifc exports projects");
            var folder = Folder(a);
            var name = FileName(a, doc.Title, ".ifc");
            var target = Path.Combine(folder, name + ".ifc");
            NotExisting(target, a);
            var o = new IFCExportOptions { FileVersion = IfcVersion(Arg.GetString(a, "version")), ExportBaseQuantities = Arg.GetBool(a, "quantities") ?? false };
            var viewArg = Arg.GetString(a, "view");
            View view = null;
            if (viewArg != null)
            {
                view = long.TryParse(viewArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vid) ? doc.GetElement(Ids.ToId(vid)) as View
                       : new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewArg, StringComparison.OrdinalIgnoreCase));
                if (view == null) throw new OpFail(Status.Invalid, "view does not exist: '" + viewArg + "'");
                o.FilterViewId = view.Id;
            }
            pend.StartUtc = DateTime.UtcNow;
            bool ok;
            // Revit requires an open transaction to export IFC; it is always rolled back: the model does not change.
            using (var t = new Transaction(doc, "BIMBRIDGE export IFC (rolled back)"))
            {
                t.Start();
                try { ok = doc.Export(folder, name, o); }
                finally { if (t.GetStatus() == TransactionStatus.Started) t.RollBack(); }
            }
            if (!ok) diffs.Add("Revit returned false from Export IFC");
            pend.Files.Add(target);
            return new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc), ["action"] = "ifc", ["file"] = target, ["version"] = o.FileVersion.ToString(),
                ["view"] = view?.Name ?? "whole model", ["base_quantities"] = o.ExportBaseQuantities, ["model_modified"] = false,
            };
        }

        public static Dictionary<string, object> ScheduleCsv(Document doc, IDictionary<string, object> a, DocOps.Pending pend, List<string> diffs)
        {
            var spec = Arg.GetString(a, "schedule") ?? throw new OpFail(Status.Invalid, "doc schedule_csv needs schedule= (schedule name or id)");
            var vs = long.TryParse(spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? doc.GetElement(Ids.ToId(id)) as ViewSchedule
                     : new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, spec, StringComparison.OrdinalIgnoreCase));
            if (vs == null) throw new OpFail(Status.Invalid, "schedule does not exist: '" + spec + "'");
            var folder = Folder(a);
            var name = FileName(a, vs.Name, ".csv");
            var target = Path.Combine(folder, name + ".csv");
            NotExisting(target, a);
            var o = new ViewScheduleExportOptions
            {
                FieldDelimiter = ",",
                TextQualifier = ExportTextQualifier.DoubleQuote,
                Title = false,
                HeadersFootersBlanks = Arg.GetBool(a, "headers") ?? true,
            };
            var body = vs.GetTableData().GetSectionData(SectionType.Body);
            var bodyRows = body?.NumberOfRows ?? 0;
            pend.StartUtc = DateTime.UtcNow;
            vs.Export(folder, name + ".csv", o);
            pend.Files.Add(target);
            if (File.Exists(target))
            {
                var lines = File.ReadAllLines(target).Count(l => l.Trim().Length > 0);
                if (lines < bodyRows - 1) diffs.Add("the CSV has " + lines + " lines and the schedule has " + bodyRows + " body rows");
            }
            return new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc), ["action"] = "schedule_csv", ["schedule"] = vs.Name, ["file"] = target,
                ["body_rows"] = (long)bodyRows, ["columns"] = (long)(body?.NumberOfColumns ?? 0),
            };
        }
    }
}
