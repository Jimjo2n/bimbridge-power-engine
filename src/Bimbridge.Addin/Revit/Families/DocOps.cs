// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Families
{
    /// <summary>
    /// Document operations (R028–R030, F09): save, save as, sync, export PDF and close. Each one is verified afterwards
    /// (file, date, size, hash, modified state, Revit event) and distinguishes "transaction applied", "saved locally",
    /// "synced with central" and "PDF exported". None of them touches documents that are not named.
    /// </summary>
    public static class DocOps
    {
        public sealed class Pending
        {
            /// <summary>Files to verify off the Revit thread (hashing large files).</summary>
            public List<string> Files = new List<string>();
            public DateTime StartUtc;
        }

        private static bool IsCloud(Document doc)
        {
            try { return doc.IsModelInCloud; } catch (Exception) { return false; }
        }

        public static Dictionary<string, object> Save(UIApplication app, Document doc, IDictionary<string, object> a, Pending pend, List<string> diffs)
        {
            if (doc.IsReadOnly) throw new OpFail(Status.NoSoportado, "the document is open read-only");
            var como = Arg.GetString(a, "como");
            pend.StartUtc = DateTime.UtcNow;
            var d = new Dictionary<string, object> { ["doc"] = DocRegistry.Instance.IdOf(doc), ["titulo_antes"] = doc.Title, ["modificado_antes"] = doc.IsModified };
            if (!string.IsNullOrWhiteSpace(como))
            {
                var path = Environment.ExpandEnvironmentVariables(como.Trim());
                if (!Path.IsPathRooted(path)) throw new OpFail(Status.Invalido, "como= needs a full path (C:\\…\\file." + (doc.IsFamilyDocument ? "rfa" : "rvt") + ")");
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (doc.IsFamilyDocument && ext != ".rfa") throw new OpFail(Status.Invalido, "a family is saved as .rfa");
                if (!doc.IsFamilyDocument && ext != ".rvt" && ext != ".rte") throw new OpFail(Status.Invalido, "a project is saved as .rvt (or .rte)");
                var overwrite = Arg.GetBool(a, "sobrescribir") ?? false;
                if (File.Exists(path) && !overwrite) throw new OpFail(Status.NecesitaUsuario, path + " already exists; pass sobrescribir=si if the user authorized it");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var opts = new SaveAsOptions { OverwriteExistingFile = overwrite };
                if (doc.IsWorkshared)
                {
                    var ws = new WorksharingSaveAsOptions { SaveAsCentral = Arg.GetBool(a, "central") ?? false };
                    opts.SetWorksharingOptions(ws);
                }
                doc.SaveAs(path, opts);
                d["accion"] = "guardar_como";
                d["ruta"] = path;
                pend.Files.Add(path);
            }
            else
            {
                if (string.IsNullOrEmpty(doc.PathName))
                    throw new OpFail(Status.Invalido, "the document was never saved (" + doc.Title + "): use doc guardar como=C:\\…\\" + doc.Title + (doc.IsFamilyDocument ? ".rfa" : ".rvt"));
                doc.Save();
                d["accion"] = "guardar";
                d["ruta"] = doc.PathName;
                if (IsCloud(doc)) d["nube"] = "cloud model: Revit's local copy was saved; there is no file of its own to verify";
                else pend.Files.Add(doc.PathName);
            }
            d["modificado_despues"] = doc.IsModified;
            if (doc.IsModified) diffs.Add("Revit still marks the document as modified after saving");
            d["titulo"] = doc.Title;
            DocRegistry.Instance.Refresh(app);
            return d;
        }

        public static Dictionary<string, object> Sync(Document doc, IDictionary<string, object> a, List<string> diffs, DocumentTracker tracker)
        {
            if (!doc.IsWorkshared) throw new OpFail(Status.NoSoportado, doc.Title + " is not workshared: there is no central model to sync with (use doc guardar)");
            if (doc.IsFamilyDocument) throw new OpFail(Status.NoSoportado, "families are not synced");
            var start = DateTime.UtcNow;
            var twc = new TransactWithCentralOptions();
            var so = new SynchronizeWithCentralOptions
            {
                Comment = Arg.GetString(a, "comentario") ?? "",
                Compact = Arg.GetBool(a, "compactar") ?? false,
                SaveLocalBefore = true,
                SaveLocalAfter = true,
            };
            var relinquish = Arg.GetBool(a, "liberar") ?? true;
            so.SetRelinquishOptions(new RelinquishOptions(relinquish));
            doc.SynchronizeWithCentral(twc, so);
            var d = new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc),
                ["accion"] = "sincronizar",
                ["comentario"] = so.Comment,
                ["libero_elementos"] = relinquish,
                ["modificado_despues"] = doc.IsModified,
            };
            try { d["central"] = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); } catch (Exception) { }
            var id = DocRegistry.Instance.IdOf(doc);
            if (tracker.LastSyncedUtc.TryGetValue(id, out var t) && t >= start.AddSeconds(-1)) d["evento_revit"] = "DocumentSynchronizedWithCentral succeeded " + t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            else diffs.Add("Revit's successful-sync event did not arrive; check in Revit (Collaborate → History)");
            if (doc.IsModified) diffs.Add("Revit still marks the document as modified after syncing");
            return d;
        }

        public static Dictionary<string, object> Pdf(Document doc, IDictionary<string, object> a, NamedSets sets, string docHandle, Pending pend, List<string> diffs)
        {
            if (doc.IsFamilyDocument) throw new OpFail(Status.NoSoportado, "doc pdf exports a project's sheets");
            var sheets = new List<ViewSheet>();
            var setName = Arg.GetString(a, "conjunto");
            var checkPaper = false;
            var order = Arg.GetString(a, "orden")?.Trim().ToLowerInvariant();
            if (order != null && order != "numero" && order != "entrada")
                throw new OpFail(Status.Invalido, "orden=numero or entrada");
            if (setName != null)
            {
                var vss = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                    .FirstOrDefault(s => string.Equals(s.Name, setName, StringComparison.OrdinalIgnoreCase));
                if (vss == null)
                {
                    var names = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().Select(s => s.Name).ToList();
                    throw new OpFail(Status.Invalido, "sheet set '" + setName + "' does not exist. Available: " + (names.Count == 0 ? "none" : string.Join(", ", names)));
                }
                foreach (View v in vss.Views) if (v is ViewSheet vs) sheets.Add(vs);
                var saved = SheetSetPdf.Read(vss);
                checkPaper = saved.Order != null;
                if (saved.Order != null && order != "numero")
                {
                    var members = sheets.ToDictionary(s => s.Id);
                    if (!PrintSheetSet.MatchesMembership(saved.Order.Select(Ids.ToLong).ToList(), members.Keys.Select(Ids.ToLong)))
                        throw new OpFail(Status.Invalido, "the sheet set's sheets changed; save the set again or use orden=numero");
                    sheets = saved.Order.Select(id => members[id]).ToList();
                    order = "entrada";
                }
                else if (order == "entrada")
                    throw new OpFail(Status.Invalido, "the sheet set has no saved order; use orden=numero or save the set again");
                if (saved.Preset != null) a = PrintSheetSet.ApplyPreset(a, saved.Preset);
            }
            foreach (var raw in Arg.GetStringList(a, "hojas"))
            {
                IEnumerable<long> ids;
                if (raw.StartsWith("$", StringComparison.Ordinal))
                {
                    if (!sets.TryGet(raw, docHandle, out var arr, out var err)) throw new OpFail(Status.Invalido, err);
                    ids = arr;
                }
                else if (long.TryParse(raw, out var one)) ids = new[] { one };
                else
                {
                    // sheet number (10E.02.NUL)
                    var byNumber = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => string.Equals(s.SheetNumber, raw, StringComparison.OrdinalIgnoreCase));
                    if (byNumber == null) throw new OpFail(Status.Invalido, "there is no sheet with id or number '" + raw + "'");
                    ids = new[] { Ids.ToLong(byNumber.Id) };
                }
                foreach (var id in ids)
                {
                    var s = doc.GetElement(Ids.ToId(id)) as ViewSheet;
                    if (s == null) throw new OpFail(Status.Invalido, id + " is not a sheet");
                    sheets.Add(s);
                }
            }
            sheets = sheets.GroupBy(s => s.Id).Select(g => g.First()).ToList();
            order ??= "numero";
            if (order == "numero") sheets = sheets.OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
            if (sheets.Count == 0) throw new OpFail(Status.Invalido, "specify hojas=ids|$set|numbers or conjunto=\"Sheet set name\"");
            var notPrintable = sheets.Where(s => { try { return !s.CanBePrinted; } catch (Exception) { return false; } }).Select(s => s.SheetNumber).ToList();
            if (notPrintable.Count > 0) throw new OpFail(Status.Invalido, "sheets Revit cannot print: " + string.Join(", ", notPrintable));
            if (checkPaper) SheetSetPdf.CheckViewportBounds(doc, sheets, diffs);

            var folder = Environment.ExpandEnvironmentVariables(Arg.GetString(a, "carpeta") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            if (!Path.IsPathRooted(folder)) throw new OpFail(Status.Invalido, "carpeta= needs a full path");
            Directory.CreateDirectory(folder);
            var combine = Arg.GetBool(a, "combinar") ?? true;
            var name = Arg.GetString(a, "nombre");
            if (combine && string.IsNullOrWhiteSpace(name))
                name = doc.Title + "_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            if (name != null) name = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (name != null && name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            var target = combine ? Path.Combine(folder, name + ".pdf") : null;
            if (target != null && File.Exists(target) && !(Arg.GetBool(a, "sobrescribir") ?? false))
                throw new OpFail(Status.NecesitaUsuario, target + " already exists; pass sobrescribir=si or a different nombre=");

            // Settings from Tyler's brief (Use Sheet Size, Fit to Page, Center, Color, Vector, High; hide reference planes, scope boxes and crop regions).
            var paperName = Arg.GetString(a, "papel") ?? "Default";
            if (!Enum.TryParse(paperName, true, out ExportPaperFormat paper) || !Enum.IsDefined(typeof(ExportPaperFormat), paper) ||
                int.TryParse(paperName, out _))
                throw new OpFail(Status.Invalido, "papel: specify a Revit export paper format, for example Default");
            var o = new PDFExportOptions
            {
                Combine = combine,
                PaperFormat = paper,
                ZoomType = (Arg.GetString(a, "zoom") ?? "ajustar") == "100" ? ZoomType.Zoom : ZoomType.FitToPage,
                PaperPlacement = PaperPlacementType.Center,
                ColorDepth = (Arg.GetString(a, "color") ?? "color") == "gris" ? ColorDepthType.GrayScale : ColorDepthType.Color,
                AlwaysUseRaster = Arg.GetBool(a, "raster") ?? false,
                RasterQuality = RasterQualityType.High,
                HideReferencePlane = true,
                HideScopeBoxes = true,
                HideCropBoundaries = true,
                HideUnreferencedViewTags = Arg.GetBool(a, "ocultar_etiquetas_sin_ref") ?? false,
                StopOnError = true,
            };
            if (o.ZoomType == ZoomType.Zoom) o.ZoomPercentage = 100;
            if (combine) o.FileName = name;
            pend.StartUtc = DateTime.UtcNow;
            var before = new HashSet<string>(Directory.GetFiles(folder, "*.pdf"), StringComparer.OrdinalIgnoreCase);
            var ok = doc.Export(folder, sheets.Select(s => s.Id).ToList(), o);
            if (!ok) diffs.Add("Revit returned false from Export");
            if (combine) pend.Files.Add(target);
            else
            {
                foreach (var f in Directory.GetFiles(folder, "*.pdf"))
                    if (!before.Contains(f) || File.GetLastWriteTimeUtc(f) >= pend.StartUtc.AddSeconds(-2)) pend.Files.Add(f);
            }
            return new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc),
                ["accion"] = "pdf",
                ["hojas"] = sheets.Select(s => (object)(s.SheetNumber + " " + s.Name)).ToList(),
                ["n_hojas"] = (long)sheets.Count,
                ["carpeta"] = folder,
                ["combinado"] = combine,
                ["orden"] = order, ["papel"] = paper.ToString(),
                ["ajustes"] = (paper == ExportPaperFormat.Default ? "sheet size" : paper.ToString()) + ", " + (o.ZoomType == ZoomType.FitToPage ? "fit to page" : "100 %") + ", centered, " +
                              (o.ColorDepth == ColorDepthType.Color ? "color" : "grayscale") + ", " + (o.AlwaysUseRaster ? "raster" : "vector") + ", high quality, no reference planes/scope boxes/crop regions",
            };
        }

        /// <summary>Off the Revit thread: exists, is new, size, hash and (PDF) pages.</summary>
        public static List<object> VerifyFiles(Pending pend, int expectedPages, List<string> diffs)
        {
            var rows = new List<object>();
            foreach (var f in pend.Files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(f)) { diffs.Add("file not found: " + f); continue; }
                var fi = new FileInfo(f);
                var fresh = fi.LastWriteTimeUtc >= pend.StartUtc.AddSeconds(-2);
                if (!fresh) diffs.Add(f + " was not written now (date " + fi.LastWriteTimeUtc.ToString("u", CultureInfo.InvariantCulture) + "): it may be an old file");
                var row = new Dictionary<string, object>
                {
                    ["archivo"] = f,
                    ["bytes"] = fi.Length,
                    ["escrito_utc"] = fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["nuevo"] = fresh,
                };
                try { row["sha256"] = Home.FileSha256(f); } catch (Exception ex) { row["sha256"] = "unreadable: " + ex.Message; }
                if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var pages = PdfPages(f);
                    row["paginas"] = pages.HasValue ? (object)(long)pages.Value : "undetermined";
                    // Textual count of /Page objects: approximate (does not validate the PDF or its content; visual review is still required).
                    row["paginas_metodo"] = "approximate (count of /Type /Page without a PDF library)";
                    if (pages.HasValue && expectedPages > 0 && pend.Files.Count == 1 && pages.Value != expectedPages)
                        diffs.Add("the PDF has " + pages.Value + " pages (approximate count) and " + expectedPages + " sheets were requested");
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Counts pages without libraries: /Type /Page objects (not /Pages). null if the PDF stores them compressed in object streams.</summary>
        public static int? PdfPages(string path)
        {
            try
            {
                var text = Encoding.GetEncoding("ISO-8859-1").GetString(File.ReadAllBytes(path));
                var n = Regex.Matches(text, @"/Type\s*/Page(?![s\w])").Count;
                if (n > 0) return n;
                var m = Regex.Matches(text, @"/Type\s*/Pages[^>]*?/Count\s+(\d+)");
                var max = 0;
                foreach (Match x in m) max = Math.Max(max, int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture));
                return max > 0 ? max : (int?)null;
            }
            catch (Exception) { return null; }
        }

        public static Dictionary<string, object> Close(UIApplication app, Document doc, IDictionary<string, object> a)
        {
            var active = app.ActiveUIDocument?.Document;
            if (active != null && active.Equals(doc))
                throw new OpFail(Status.NoSoportado, "Revit does not allow closing the active document through the API (" + doc.Title + "); ask the user to activate another one or close it");
            var save = Arg.GetBool(a, "guardar") ?? false;
            if (!save && doc.IsModified && !(Arg.GetBool(a, "descartar") ?? false))
                throw new OpFail(Status.NecesitaUsuario, doc.Title + " has unsaved changes: pass guardar=si or descartar=si (if the user authorized it)");
            var id = DocRegistry.Instance.IdOf(doc);
            var title = doc.Title;
            var ok = doc.Close(save);
            DocRegistry.Instance.Refresh(app);
            return new Dictionary<string, object> { ["doc"] = id, ["titulo"] = title, ["cerrado"] = ok, ["guardado"] = save };
        }

        /// <summary>
        /// beta1 (NRT-05): ON-DISK persistence verification, independent of the open document. Copies the saved .rvt to a
        /// temp file, opens it in the background (no UI, not activated), reads (id, parameter) and closes it without saving; deletes the copy.
        /// Local non-workshared files only. Does not modify the open document or the original file.
        /// </summary>
        public static Dictionary<string, object> VerifyOnDisk(UIApplication app, Document doc, IDictionary<string, object> a)
        {
            var path = doc.PathName;
            if (string.IsNullOrEmpty(path) || IsCloud(doc) || !File.Exists(path)) throw new OpFail(Status.NoSoportado, "verificar_disco: only an already saved local .rvt");
            if (doc.IsWorkshared) throw new OpFail(Status.NoSoportado, "verificar_disco: not used on workshared documents");
            if (doc.IsModified) throw new OpFail(Status.NecesitaUsuario, "verificar_disco: the document has unsaved changes; what is on disk is not what you see");
            var param = Arg.GetString(a, "parametro") ?? throw new OpFail(Status.Invalido, "verificar_disco needs parametro=");
            var ids = Arg.GetStringList(a, "ids").Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToList();
            if (ids.Count < 1 || ids.Count > 200) throw new OpFail(Status.Invalido, "verificar_disco: between 1 and 200 ids");
            // beta1 run1 (2026-10-28): Revit keeps the active .rvt open; File.OpenRead/File.Copy fail with IOException.
            // It is read ONCE with read/write sharing: the hash and the copy come from the same bytes.
            var tmp = Path.Combine(Path.GetTempPath(), "bimbridge-verif-" + Guid.NewGuid().ToString("N") + ".rvt");
            string sha;
            try { sha = Bimbridge.Engine.StableFile.CopyAndHash(path, tmp).Sha256; }   // rc1: detects concurrent changes
            catch (IOException ex)
            {
                try { File.Delete(tmp); } catch (Exception) { }
                throw new OpFail(Status.Error, "verificar_disco: could not read " + path + " consistently (" + ex.Message + ")");
            }
            Document copy = null;
            var rows = new List<object>();
            try
            {
                copy = app.Application.OpenDocumentFile(tmp);
                foreach (var id in ids)
                {
#if REVIT2024_OR_GREATER
                    var e = copy.GetElement(new ElementId(id));
#else
                    var e = copy.GetElement(new ElementId((int)id));
#endif
                    var p = e?.LookupParameter(param);
                    rows.Add(new List<object> { id, e != null, p == null ? null : (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString()) });
                }
            }
            finally
            {
                try { copy?.Close(false); } catch (Exception) { }
                try { File.Delete(tmp); } catch (Exception) { }
            }
            return new Dictionary<string, object> { ["ruta"] = path, ["sha256"] = sha, ["parametro"] = param, ["filas"] = rows, ["cols"] = new List<object> { "id", "existe", param } };
        }
    }
}
