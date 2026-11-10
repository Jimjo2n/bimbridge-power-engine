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
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit.Families
{
    /// <summary>
    /// Operaciones de documento (R028–R030, F09): guardar, guardar como, sincronizar, exportar PDF y cerrar. Cada una se verifica después
    /// (archivo, fecha, tamaño, hash, estado modificado, evento de Revit) y distingue "transacción aplicada", "guardado local",
    /// "sincronizado con central" y "PDF exportado". Ninguna toca documentos que no se nombren.
    /// </summary>
    public static class DocOps
    {
        public sealed class Pending
        {
            /// <summary>Archivos a verificar fuera del hilo de Revit (hash de archivos grandes).</summary>
            public List<string> Files = new List<string>();
            public DateTime StartUtc;
        }

        private static bool IsCloud(Document doc)
        {
            try { return doc.IsModelInCloud; } catch (Exception) { return false; }
        }

        public static Dictionary<string, object> Save(UIApplication app, Document doc, IDictionary<string, object> a, Pending pend, List<string> diffs)
        {
            if (doc.IsReadOnly) throw new OpFail(Status.NoSoportado, "el documento está abierto en sólo lectura");
            var como = Arg.GetString(a, "como");
            pend.StartUtc = DateTime.UtcNow;
            var d = new Dictionary<string, object> { ["doc"] = DocRegistry.Instance.IdOf(doc), ["titulo_antes"] = doc.Title, ["modificado_antes"] = doc.IsModified };
            if (!string.IsNullOrWhiteSpace(como))
            {
                var path = Environment.ExpandEnvironmentVariables(como.Trim());
                if (!Path.IsPathRooted(path)) throw new OpFail(Status.Invalido, "como= necesita una ruta completa (C:\\…\\archivo." + (doc.IsFamilyDocument ? "rfa" : "rvt") + ")");
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (doc.IsFamilyDocument && ext != ".rfa") throw new OpFail(Status.Invalido, "una familia se guarda como .rfa");
                if (!doc.IsFamilyDocument && ext != ".rvt" && ext != ".rte") throw new OpFail(Status.Invalido, "un proyecto se guarda como .rvt (o .rte)");
                var overwrite = Arg.GetBool(a, "sobrescribir") ?? false;
                if (File.Exists(path) && !overwrite) throw new OpFail(Status.NecesitaUsuario, "ya existe " + path + "; pase sobrescribir=si si el usuario lo autorizó");
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
                    throw new OpFail(Status.Invalido, "el documento nunca se guardó (" + doc.Title + "): use doc guardar como=C:\\…\\" + doc.Title + (doc.IsFamilyDocument ? ".rfa" : ".rvt"));
                doc.Save();
                d["accion"] = "guardar";
                d["ruta"] = doc.PathName;
                if (IsCloud(doc)) d["nube"] = "modelo en la nube: se guardó la copia local de Revit; no hay archivo propio que verificar";
                else pend.Files.Add(doc.PathName);
            }
            d["modificado_despues"] = doc.IsModified;
            if (doc.IsModified) diffs.Add("Revit sigue marcando el documento como modificado después de guardar");
            d["titulo"] = doc.Title;
            DocRegistry.Instance.Refresh(app);
            return d;
        }

        public static Dictionary<string, object> Sync(Document doc, IDictionary<string, object> a, List<string> diffs, DocumentTracker tracker)
        {
            if (!doc.IsWorkshared) throw new OpFail(Status.NoSoportado, doc.Title + " no tiene worksharing: no hay modelo central con qué sincronizar (use doc guardar)");
            if (doc.IsFamilyDocument) throw new OpFail(Status.NoSoportado, "las familias no se sincronizan");
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
            if (tracker.LastSyncedUtc.TryGetValue(id, out var t) && t >= start.AddSeconds(-1)) d["evento_revit"] = "DocumentSynchronizedWithCentral correcto " + t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            else diffs.Add("no llegó el evento de sincronización correcta de Revit; compruebe en Revit (Colaborar → Historial)");
            if (doc.IsModified) diffs.Add("Revit sigue marcando el documento como modificado después de sincronizar");
            return d;
        }

        public static Dictionary<string, object> Pdf(Document doc, IDictionary<string, object> a, NamedSets sets, string docHandle, Pending pend, List<string> diffs)
        {
            if (doc.IsFamilyDocument) throw new OpFail(Status.NoSoportado, "doc pdf exporta hojas de un proyecto");
            var sheets = new List<ViewSheet>();
            var setName = Arg.GetString(a, "conjunto");
            if (setName != null)
            {
                var vss = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                    .FirstOrDefault(s => string.Equals(s.Name, setName, StringComparison.OrdinalIgnoreCase));
                if (vss == null)
                {
                    var names = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().Select(s => s.Name).ToList();
                    throw new OpFail(Status.Invalido, "no existe el conjunto de impresión '" + setName + "'. Hay: " + (names.Count == 0 ? "ninguno" : string.Join(", ", names)));
                }
                foreach (View v in vss.Views) if (v is ViewSheet vs) sheets.Add(vs);
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
                    // número de hoja (10E.02.NUL)
                    var byNumber = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => string.Equals(s.SheetNumber, raw, StringComparison.OrdinalIgnoreCase));
                    if (byNumber == null) throw new OpFail(Status.Invalido, "no hay una hoja con id o número '" + raw + "'");
                    ids = new[] { Ids.ToLong(byNumber.Id) };
                }
                foreach (var id in ids)
                {
                    var s = doc.GetElement(Ids.ToId(id)) as ViewSheet;
                    if (s == null) throw new OpFail(Status.Invalido, id + " no es una hoja");
                    sheets.Add(s);
                }
            }
            sheets = sheets.GroupBy(s => s.Id).Select(g => g.First()).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
            if (sheets.Count == 0) throw new OpFail(Status.Invalido, "indique hojas=ids|$conjunto|números o conjunto=\"Nombre del conjunto de impresión\"");
            var notPrintable = sheets.Where(s => { try { return !s.CanBePrinted; } catch (Exception) { return false; } }).Select(s => s.SheetNumber).ToList();
            if (notPrintable.Count > 0) throw new OpFail(Status.Invalido, "hojas que Revit no puede imprimir: " + string.Join(", ", notPrintable));

            var folder = Environment.ExpandEnvironmentVariables(Arg.GetString(a, "carpeta") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            if (!Path.IsPathRooted(folder)) throw new OpFail(Status.Invalido, "carpeta= necesita una ruta completa");
            Directory.CreateDirectory(folder);
            var combine = Arg.GetBool(a, "combinar") ?? true;
            var name = Arg.GetString(a, "nombre");
            if (combine && string.IsNullOrWhiteSpace(name))
                name = doc.Title + "_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            if (name != null) name = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (name != null && name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            var target = combine ? Path.Combine(folder, name + ".pdf") : null;
            if (target != null && File.Exists(target) && !(Arg.GetBool(a, "sobrescribir") ?? false))
                throw new OpFail(Status.NecesitaUsuario, "ya existe " + target + "; pase sobrescribir=si o otro nombre=");

            // Ajustes del encargo de Tyler (Use Sheet Size, Fit to Page, Center, Color, Vector, High; ocultar planos de referencia, scope boxes y recortes).
            var o = new PDFExportOptions
            {
                Combine = combine,
                PaperFormat = ExportPaperFormat.Default,
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
            if (!ok) diffs.Add("Revit devolvió false en Export");
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
                ["ajustes"] = "tamaño de hoja, " + (o.ZoomType == ZoomType.FitToPage ? "ajustar a página" : "100 %") + ", centrado, " +
                              (o.ColorDepth == ColorDepthType.Color ? "color" : "gris") + ", " + (o.AlwaysUseRaster ? "ráster" : "vectorial") + ", calidad alta, sin planos de referencia/scope boxes/recortes",
            };
        }

        /// <summary>Fuera del hilo de Revit: existe, es nuevo, tamaño, hash y (PDF) páginas.</summary>
        public static List<object> VerifyFiles(Pending pend, int expectedPages, List<string> diffs)
        {
            var rows = new List<object>();
            foreach (var f in pend.Files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(f)) { diffs.Add("no se encontró el archivo " + f); continue; }
                var fi = new FileInfo(f);
                var fresh = fi.LastWriteTimeUtc >= pend.StartUtc.AddSeconds(-2);
                if (!fresh) diffs.Add(f + " no se escribió ahora (fecha " + fi.LastWriteTimeUtc.ToString("u", CultureInfo.InvariantCulture) + "): puede ser un archivo viejo");
                var row = new Dictionary<string, object>
                {
                    ["archivo"] = f,
                    ["bytes"] = fi.Length,
                    ["escrito_utc"] = fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["nuevo"] = fresh,
                };
                try { row["sha256"] = Home.FileSha256(f); } catch (Exception ex) { row["sha256"] = "no legible: " + ex.Message; }
                if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var pages = PdfPages(f);
                    row["paginas"] = pages.HasValue ? (object)(long)pages.Value : "no determinado";
                    // Conteo textual de objetos /Page: orientativo (no valida el PDF ni su contenido; la revisión visual sigue siendo necesaria).
                    row["paginas_metodo"] = "orientativo (conteo de /Type /Page sin librería PDF)";
                    if (pages.HasValue && expectedPages > 0 && pend.Files.Count == 1 && pages.Value != expectedPages)
                        diffs.Add("el PDF tiene " + pages.Value + " páginas (conteo orientativo) y se pidieron " + expectedPages + " hojas");
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Cuenta páginas sin librerías: objetos /Type /Page (no /Pages). null si el PDF las guarda comprimidas en flujos de objetos.</summary>
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
                throw new OpFail(Status.NoSoportado, "Revit no permite cerrar por la API el documento activo (" + doc.Title + "); pida al usuario que active otro o lo cierre");
            var save = Arg.GetBool(a, "guardar") ?? false;
            if (!save && doc.IsModified && !(Arg.GetBool(a, "descartar") ?? false))
                throw new OpFail(Status.NecesitaUsuario, doc.Title + " tiene cambios sin guardar: pase guardar=si o descartar=si (si el usuario lo autorizó)");
            var id = DocRegistry.Instance.IdOf(doc);
            var title = doc.Title;
            var ok = doc.Close(save);
            DocRegistry.Instance.Refresh(app);
            return new Dictionary<string, object> { ["doc"] = id, ["titulo"] = title, ["cerrado"] = ok, ["guardado"] = save };
        }

        /// <summary>
        /// beta1 (NRT-05): verificación de persistencia EN DISCO, independiente del documento abierto. Copia el .rvt guardado a un
        /// temporal, lo abre en segundo plano (sin UI, sin activar), lee (id, parámetro) y lo cierra sin guardar; borra la copia.
        /// Sólo archivos locales no workshared. No modifica el documento abierto ni el archivo original.
        /// </summary>
        public static Dictionary<string, object> VerifyOnDisk(UIApplication app, Document doc, IDictionary<string, object> a)
        {
            var path = doc.PathName;
            if (string.IsNullOrEmpty(path) || IsCloud(doc) || !File.Exists(path)) throw new OpFail(Status.NoSoportado, "verificar_disco: sólo un .rvt local ya guardado");
            if (doc.IsWorkshared) throw new OpFail(Status.NoSoportado, "verificar_disco: no se usa sobre documentos workshared");
            if (doc.IsModified) throw new OpFail(Status.NecesitaUsuario, "verificar_disco: el documento tiene cambios sin guardar; lo que hay en disco no es lo que se ve");
            var param = Arg.GetString(a, "parametro") ?? throw new OpFail(Status.Invalido, "verificar_disco necesita parametro=");
            var ids = Arg.GetStringList(a, "ids").Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToList();
            if (ids.Count < 1 || ids.Count > 200) throw new OpFail(Status.Invalido, "verificar_disco: entre 1 y 200 ids");
            // beta1 run1 (2026-10-28): Revit mantiene abierto el .rvt activo; File.OpenRead/File.Copy fallan con IOException.
            // Se lee UNA vez compartiendo lectura/escritura: el hash y la copia salen de los mismos bytes.
            var tmp = Path.Combine(Path.GetTempPath(), "pdi-verif-" + Guid.NewGuid().ToString("N") + ".rvt");
            string sha;
            try { sha = PDI.RevitMcp.Engine.StableFile.CopyAndHash(path, tmp).Sha256; }   // rc1: detecta cambios concurrentes
            catch (IOException ex)
            {
                try { File.Delete(tmp); } catch (Exception) { }
                throw new OpFail(Status.Error, "verificar_disco: no se pudo leer " + path + " de forma consistente (" + ex.Message + ")");
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
