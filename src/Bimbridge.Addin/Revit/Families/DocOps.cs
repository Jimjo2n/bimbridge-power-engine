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
            if (doc.IsReadOnly) throw new OpFail(Status.NotSupported, "the document is open read-only");
            var saveAs = Arg.GetString(a, "as");
            pend.StartUtc = DateTime.UtcNow;
            var d = new Dictionary<string, object> { ["doc"] = DocRegistry.Instance.IdOf(doc), ["title_before"] = doc.Title, ["modified_before"] = doc.IsModified };
            if (!string.IsNullOrWhiteSpace(saveAs))
            {
                var path = Environment.ExpandEnvironmentVariables(saveAs.Trim());
                if (!Path.IsPathRooted(path)) throw new OpFail(Status.Invalid, "as= needs a full path (C:\\…\\file." + (doc.IsFamilyDocument ? "rfa" : "rvt") + ")");
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (doc.IsFamilyDocument && ext != ".rfa") throw new OpFail(Status.Invalid, "a family is saved as .rfa");
                if (!doc.IsFamilyDocument && ext != ".rvt" && ext != ".rte") throw new OpFail(Status.Invalid, "a project is saved as .rvt (or .rte)");
                var overwrite = Arg.GetBool(a, "overwrite") ?? false;
                if (File.Exists(path) && !overwrite) throw new OpFail(Status.NeedsUser, path + " already exists; pass overwrite=yes if the user authorized it");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var opts = new SaveAsOptions { OverwriteExistingFile = overwrite };
                if (doc.IsWorkshared)
                {
                    var ws = new WorksharingSaveAsOptions { SaveAsCentral = Arg.GetBool(a, "central") ?? false };
                    opts.SetWorksharingOptions(ws);
                }
                doc.SaveAs(path, opts);
                d["action"] = "save_as";
                d["path"] = path;
                pend.Files.Add(path);
            }
            else
            {
                if (string.IsNullOrEmpty(doc.PathName))
                    throw new OpFail(Status.Invalid, "the document was never saved (" + doc.Title + "): use doc save as=C:\\…\\" + doc.Title + (doc.IsFamilyDocument ? ".rfa" : ".rvt"));
                doc.Save();
                d["action"] = "save";
                d["path"] = doc.PathName;
                if (IsCloud(doc)) d["cloud"] = "cloud model: Revit's local copy was saved; there is no file of its own to verify";
                else pend.Files.Add(doc.PathName);
            }
            d["modified_after"] = doc.IsModified;
            if (doc.IsModified) diffs.Add("Revit still marks the document as modified after saving");
            d["title"] = doc.Title;
            DocRegistry.Instance.Refresh(app);
            return d;
        }

        public static Dictionary<string, object> Sync(Document doc, IDictionary<string, object> a, List<string> diffs, DocumentTracker tracker)
        {
            if (!doc.IsWorkshared) throw new OpFail(Status.NotSupported, doc.Title + " is not workshared: there is no central model to sync with (use doc save)");
            if (doc.IsFamilyDocument) throw new OpFail(Status.NotSupported, "families are not synced");
            var start = DateTime.UtcNow;
            var twc = new TransactWithCentralOptions();
            var so = new SynchronizeWithCentralOptions
            {
                Comment = Arg.GetString(a, "comment") ?? "",
                Compact = Arg.GetBool(a, "compact") ?? false,
                SaveLocalBefore = true,
                SaveLocalAfter = true,
            };
            var relinquish = Arg.GetBool(a, "relinquish") ?? true;
            so.SetRelinquishOptions(new RelinquishOptions(relinquish));
            doc.SynchronizeWithCentral(twc, so);
            var d = new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc),
                ["action"] = "sync",
                ["comment"] = so.Comment,
                ["relinquished_elements"] = relinquish,
                ["modified_after"] = doc.IsModified,
            };
            try { d["central"] = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); } catch (Exception) { }
            var id = DocRegistry.Instance.IdOf(doc);
            if (tracker.LastSyncedUtc.TryGetValue(id, out var t) && t >= start.AddSeconds(-1)) d["revit_event"] = "DocumentSynchronizedWithCentral succeeded " + t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            else diffs.Add("Revit's successful-sync event did not arrive; check in Revit (Collaborate → History)");
            if (doc.IsModified) diffs.Add("Revit still marks the document as modified after syncing");
            return d;
        }

        public static Dictionary<string, object> Pdf(Document doc, IDictionary<string, object> a, NamedSets sets, string docHandle, Pending pend, List<string> diffs)
        {
            if (doc.IsFamilyDocument) throw new OpFail(Status.NotSupported, "doc pdf exports a project's sheets");
            var sheets = new List<ViewSheet>();
            var setName = Arg.GetString(a, "sheet_set");
            var checkPaper = false;
            var order = Arg.GetString(a, "order")?.Trim().ToLowerInvariant();
            if (order != null && order != "number" && order != "in")
                throw new OpFail(Status.Invalid, "order=number or in");
            if (setName != null)
            {
                var vss = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                    .FirstOrDefault(s => string.Equals(s.Name, setName, StringComparison.OrdinalIgnoreCase));
                if (vss == null)
                {
                    var names = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().Select(s => s.Name).ToList();
                    throw new OpFail(Status.Invalid, "sheet set '" + setName + "' does not exist. Available: " + (names.Count == 0 ? "none" : string.Join(", ", names)));
                }
                foreach (View v in vss.Views) if (v is ViewSheet vs) sheets.Add(vs);
                var saved = SheetSetPdf.Read(vss);
                checkPaper = saved.Order != null;
                if (saved.Order != null && order != "number")
                {
                    var members = sheets.ToDictionary(s => s.Id);
                    if (!PrintSheetSet.MatchesMembership(saved.Order.Select(Ids.ToLong).ToList(), members.Keys.Select(Ids.ToLong)))
                        throw new OpFail(Status.Invalid, "the sheet set's sheets changed; save the set again or use order=number");
                    sheets = saved.Order.Select(id => members[id]).ToList();
                    order = "in";
                }
                else if (order == "in")
                    throw new OpFail(Status.Invalid, "the sheet set has no saved order; use order=number or save the set again");
                if (saved.Preset != null) a = PrintSheetSet.ApplyPreset(a, saved.Preset);
            }
            foreach (var raw in Arg.GetStringList(a, "sheets"))
            {
                IEnumerable<long> ids;
                if (raw.StartsWith("$", StringComparison.Ordinal))
                {
                    if (!sets.TryGet(raw, docHandle, out var arr, out var err)) throw new OpFail(Status.Invalid, err);
                    ids = arr;
                }
                else if (long.TryParse(raw, out var one)) ids = new[] { one };
                else
                {
                    // sheet number (10E.02.NUL)
                    var byNumber = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => string.Equals(s.SheetNumber, raw, StringComparison.OrdinalIgnoreCase));
                    if (byNumber == null) throw new OpFail(Status.Invalid, "there is no sheet with id or number '" + raw + "'");
                    ids = new[] { Ids.ToLong(byNumber.Id) };
                }
                foreach (var id in ids)
                {
                    var s = doc.GetElement(Ids.ToId(id)) as ViewSheet;
                    if (s == null) throw new OpFail(Status.Invalid, id + " is not a sheet");
                    sheets.Add(s);
                }
            }
            sheets = sheets.GroupBy(s => s.Id).Select(g => g.First()).ToList();
            order ??= "number";
            if (order == "number") sheets = sheets.OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
            if (sheets.Count == 0) throw new OpFail(Status.Invalid, "specify sheets=ids|$set|numbers or sheet_set=\"Sheet set name\"");
            var notPrintable = sheets.Where(s => { try { return !s.CanBePrinted; } catch (Exception) { return false; } }).Select(s => s.SheetNumber).ToList();
            if (notPrintable.Count > 0) throw new OpFail(Status.Invalid, "sheets Revit cannot print: " + string.Join(", ", notPrintable));
            if (checkPaper) SheetSetPdf.CheckViewportBounds(doc, sheets, diffs);

            var folder = Environment.ExpandEnvironmentVariables(Arg.GetString(a, "folder") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            if (!Path.IsPathRooted(folder)) throw new OpFail(Status.Invalid, "folder= needs a full path");
            Directory.CreateDirectory(folder);
            var combine = Arg.GetBool(a, "combine") ?? true;
            var name = Arg.GetString(a, "name");
            if (combine && string.IsNullOrWhiteSpace(name))
                name = doc.Title + "_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            if (name != null) name = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (name != null && name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            var target = combine ? Path.Combine(folder, name + ".pdf") : null;
            if (target != null && File.Exists(target) && !(Arg.GetBool(a, "overwrite") ?? false))
                throw new OpFail(Status.NeedsUser, target + " already exists; pass overwrite=yes or a different name=");

            // Settings from Tyler's brief (Use Sheet Size, Fit to Page, Center, Color, Vector, High; hide reference planes, scope boxes and crop regions).
            var paperName = Arg.GetString(a, "paper") ?? "Default";
            if (!Enum.TryParse(paperName, true, out ExportPaperFormat paper) || !Enum.IsDefined(typeof(ExportPaperFormat), paper) ||
                int.TryParse(paperName, out _))
                throw new OpFail(Status.Invalid, "paper: specify a Revit export paper format, for example Default");
            var o = new PDFExportOptions
            {
                Combine = combine,
                PaperFormat = paper,
                ZoomType = (Arg.GetString(a, "zoom") ?? "adjust") == "100" ? ZoomType.Zoom : ZoomType.FitToPage,
                PaperPlacement = PaperPlacementType.Center,
                ColorDepth = LegacyAliases.Token(Arg.GetString(a, "color") ?? "color") == "gray" ? ColorDepthType.GrayScale : ColorDepthType.Color,
                AlwaysUseRaster = Arg.GetBool(a, "raster") ?? false,
                RasterQuality = RasterQualityType.High,
                HideReferencePlane = true,
                HideScopeBoxes = true,
                HideCropBoundaries = true,
                HideUnreferencedViewTags = Arg.GetBool(a, "hide_unreferenced_tags") ?? false,
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
                ["action"] = "pdf",
                ["sheets"] = sheets.Select(s => (object)(s.SheetNumber + " " + s.Name)).ToList(),
                ["n_sheets"] = (long)sheets.Count,
                ["folder"] = folder,
                ["combined"] = combine,
                ["order"] = order, ["paper"] = paper.ToString(),
                ["settings"] = (paper == ExportPaperFormat.Default ? "sheet size" : paper.ToString()) + ", " + (o.ZoomType == ZoomType.FitToPage ? "fit to page" : "100 %") + ", centered, " +
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
                    ["file"] = f,
                    ["bytes"] = fi.Length,
                    ["written_utc"] = fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["new"] = fresh,
                };
                try { row["sha256"] = Home.FileSha256(f); } catch (Exception ex) { row["sha256"] = "unreadable: " + ex.Message; }
                if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var pages = PdfPages(f);
                    row["pages"] = pages.HasValue ? (object)(long)pages.Value : "undetermined";
                    // Textual count of /Page objects: approximate (does not validate the PDF or its content; visual review is still required).
                    row["pages_method"] = "approximate (count of /Type /Page without a PDF library)";
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
                throw new OpFail(Status.NotSupported, "Revit does not allow closing the active document through the API (" + doc.Title + "); ask the user to activate another one or close it");
            var save = Arg.GetBool(a, "save") ?? false;
            if (!save && doc.IsModified && !(Arg.GetBool(a, "discard") ?? false))
                throw new OpFail(Status.NeedsUser, doc.Title + " has unsaved changes: pass save=yes or discard=yes (if the user authorized it)");
            var id = DocRegistry.Instance.IdOf(doc);
            var title = doc.Title;
            var ok = doc.Close(save);
            DocRegistry.Instance.Refresh(app);
            return new Dictionary<string, object> { ["doc"] = id, ["title"] = title, ["closed"] = ok, ["saved"] = save };
        }

        /// <summary>
        /// beta1 (NRT-05): ON-DISK persistence verification, independent of the open document. Copies the saved .rvt to a
        /// temp file, opens it in the background (no UI, not activated), reads (id, parameter) and closes it without saving; deletes the copy.
        /// Local non-workshared files only. Does not modify the open document or the original file.
        /// </summary>
        public static Dictionary<string, object> VerifyOnDisk(UIApplication app, Document doc, IDictionary<string, object> a, NamedSets sets, string docHandle)
        {
            var path = doc.PathName;
            if (string.IsNullOrEmpty(path) || IsCloud(doc) || !File.Exists(path)) throw new OpFail(Status.NotSupported, "verify_disk: only an already saved local .rvt");
            if (doc.IsWorkshared) throw new OpFail(Status.NotSupported, "verify_disk: not used on workshared documents");
            if (doc.IsModified) throw new OpFail(Status.NeedsUser, "verify_disk: the document has unsaved changes; what is on disk is not what you see");
            var param = Arg.GetString(a, "parameter") ?? throw new OpFail(Status.Invalid, "verify_disk needs parameter=");
            var ids = new List<long>();
            foreach (var raw in Arg.GetStringList(a, "ids"))
            {
                if (raw.StartsWith("$", StringComparison.Ordinal))
                {
                    if (!sets.TryGet(raw, docHandle, out var arr, out var err)) throw new OpFail(Status.Invalid, err);
                    ids.AddRange(arr);
                }
                else if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var one)) ids.Add(one);
                else throw new OpFail(Status.Invalid, "verify_disk: '" + raw + "' is neither an id nor a set ($name)");
            }
            if (ids.Count < 1 || ids.Count > 200) throw new OpFail(Status.Invalid, "verify_disk: between 1 and 200 ids");
            // beta1 run1 (2026-10-28): Revit keeps the active .rvt open; File.OpenRead/File.Copy fail with IOException.
            // It is read ONCE with read/write sharing: the hash and the copy come from the same bytes.
            var tmp = Path.Combine(Path.GetTempPath(), "bimbridge-verif-" + Guid.NewGuid().ToString("N") + ".rvt");
            string sha;
            try { sha = Bimbridge.Engine.StableFile.CopyAndHash(path, tmp).Sha256; }   // rc1: detects concurrent changes
            catch (IOException ex)
            {
                try { File.Delete(tmp); } catch (Exception) { }
                throw new OpFail(Status.Error, "verify_disk: could not read " + path + " consistently (" + ex.Message + ")");
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
            return new Dictionary<string, object> { ["path"] = path, ["sha256"] = sha, ["parameter"] = param, ["rows"] = rows, ["cols"] = new List<object> { "id", "exists", param } };
        }
    }
}
