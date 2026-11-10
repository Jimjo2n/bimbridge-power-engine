// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Addin.Revit.Writes;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Captura de una vista como imagen para que la IA la vea (percepción del lienzo de Revit).
    /// Sin cambios: exporta la vista tal cual (ExportImage nativo, sin tocar la pantalla del usuario).
    /// Con resaltado, colores por categoría o encuadre: aplica sobrescrituras/recorte DENTRO de un grupo de transacciones,
    /// exporta y revierte el grupo entero en el mismo tramo. El modelo queda idéntico, no aparece en Deshacer
    /// y el registro de cambios lo ignora (prefijo "PDI MCP temporal").
    /// </summary>
    public sealed class Capture
    {
        public sealed class Group
        {
            public string Label;
            public List<long> Ids = new List<long>();
            public Color Color;
            public string ColorName;
        }

        public long? ViewId;
        public readonly List<Group> Groups = new List<Group>();
        public bool ColorByCategory;
        public List<long> FrameIds;
        public int Px = 1400;
        public double Margin = 0.15;

        public string Error;
        public string File;
        public string Mime = "image/png";
        public long Rev;
        public readonly Dictionary<string, object> Info = new Dictionary<string, object>();
        private readonly List<string> _notes = new List<string>();

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDI.RevitMcp", "capturas");

        // Paleta pensada para distinguir lo que más se confunde en planos eléctricos (luminarias vs. tomas vs. interruptores).
        private static readonly Dictionary<BuiltInCategory, (byte r, byte g, byte b, string name)> Palette = new Dictionary<BuiltInCategory, (byte, byte, byte, string)>
        {
            [BuiltInCategory.OST_LightingFixtures] = (255, 140, 0, "naranja"),
            [BuiltInCategory.OST_LightingDevices] = (0, 160, 0, "verde"),
            [BuiltInCategory.OST_ElectricalFixtures] = (0, 90, 255, "azul"),
            [BuiltInCategory.OST_ElectricalEquipment] = (200, 0, 200, "magenta"),
            [BuiltInCategory.OST_FireAlarmDevices] = (220, 0, 0, "rojo"),
            [BuiltInCategory.OST_CommunicationDevices] = (0, 170, 190, "cian"),
            [BuiltInCategory.OST_DataDevices] = (0, 170, 190, "cian"),
            [BuiltInCategory.OST_TelephoneDevices] = (0, 170, 190, "cian"),
            [BuiltInCategory.OST_SecurityDevices] = (0, 120, 140, "cian oscuro"),
            [BuiltInCategory.OST_NurseCallDevices] = (0, 120, 140, "cian oscuro"),
            [BuiltInCategory.OST_Conduit] = (140, 90, 40, "marrón"),
            [BuiltInCategory.OST_ConduitFitting] = (140, 90, 40, "marrón"),
            [BuiltInCategory.OST_CableTray] = (110, 110, 110, "gris"),
            [BuiltInCategory.OST_CableTrayFitting] = (110, 110, 110, "gris"),
            [BuiltInCategory.OST_MechanicalEquipment] = (128, 0, 255, "violeta"),
            [BuiltInCategory.OST_PlumbingFixtures] = (0, 128, 128, "verde azulado"),
            [BuiltInCategory.OST_Sprinklers] = (255, 0, 128, "rosa"),
        };

        private static readonly (byte r, byte g, byte b, string name)[] Extra =
        {
            (160, 160, 0, "oliva"), (255, 90, 90, "salmón"), (90, 90, 255, "lavanda"), (0, 200, 120, "verde menta"),
            (180, 100, 0, "ocre"), (100, 0, 160, "púrpura"), (0, 100, 0, "verde oscuro"), (0, 0, 140, "azul marino"),
        };

        public static readonly Dictionary<string, (byte r, byte g, byte b)> Named = new Dictionary<string, (byte, byte, byte)>(StringComparer.OrdinalIgnoreCase)
        {
            ["rojo"] = (230, 0, 0), ["verde"] = (0, 170, 0), ["azul"] = (0, 90, 255), ["naranja"] = (255, 140, 0),
            ["magenta"] = (230, 0, 230), ["cian"] = (0, 190, 210), ["amarillo"] = (240, 200, 0), ["violeta"] = (128, 0, 255),
            ["negro"] = (0, 0, 0), ["rosa"] = (255, 0, 128), ["marron"] = (140, 90, 40), ["marrón"] = (140, 90, 40), ["gris"] = (110, 110, 110),
        };

        public static readonly string[] DefaultGroupColors = { "rojo", "azul", "verde", "magenta", "naranja", "cian" };

        public bool Run(UIApplication app, DocumentTracker tracker)
        {
            var total = Stopwatch.StartNew();
            var uidoc = DocTarget.ActiveUi(app, out var uiError);
            var doc = uidoc?.Document;
            if (doc == null) { Error = uiError ?? "No hay ningún documento activo en Revit."; return true; }
            Rev = tracker.For(doc)?.Rev ?? 0;

            var view = ViewId.HasValue ? doc.GetElement(Ids.ToId(ViewId.Value)) as View : uidoc.ActiveView;
            if (view == null) { Error = "vista " + ViewId + " no existe en este documento (catalog que=vistas)"; return true; }
            if (view.IsTemplate) { Error = "'" + view.Name + "' es una plantilla de vista: capture una vista que la use"; return true; }
            bool printable;
            try { printable = view.CanBePrinted; } catch (Exception) { printable = false; }
            if (!printable) { Error = "'" + view.Name + "' no se puede exportar como imagen (tipo " + view.ViewType + ")"; return true; }

            Info["vista"] = new Dictionary<string, object>
            {
                ["id"] = Ids.ToLong(view.Id), ["nombre"] = view.Name, ["tipo"] = CatalogRunner.ViewTypeName(view.ViewType),
                ["escala"] = SafeScale(view),
            };

            // Contenido visible (sólo el modelo propio; los vínculos no se cuentan): ayuda a leer la imagen sin adivinar.
            var visible = new Dictionary<long, List<ElementId>>();
            var catOf = new Dictionary<long, Category>();
            var isSheetLike = view is ViewSheet || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.Legend || view.ViewType == ViewType.DraftingView;
            if (!(view is ViewSheet) && view.ViewType != ViewType.Schedule)
            {
                try
                {
                    foreach (var e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                    {
                        var c = e.Category;
                        if (c == null) continue;
                        var k = Ids.ToLong(c.Id);
                        if (!visible.TryGetValue(k, out var l)) { visible[k] = l = new List<ElementId>(); catOf[k] = c; }
                        l.Add(e.Id);
                    }
                }
                catch (Exception) { }
                Info["contenido"] = visible.OrderByDescending(kv => kv.Value.Count).Take(15)
                    .ToDictionary(kv => CatalogRunner.BicName(catOf[kv.Key]), kv => (object)(long)kv.Value.Count);
                LinkContent(doc, view);
            }

            var wantsChanges = Groups.Any(g => g.Ids.Count > 0) || ColorByCategory || (FrameIds != null && FrameIds.Count > 0);
            var canChange = wantsChanges && CanChange(doc, view, isSheetLike) && Engine.TrialGuard.MayOpenTemporaryGroup(doc.IsModified);   // P0: sin grupo temporal si hay cambios sin guardar

            var dir = Folder;
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var tmp = Path.Combine(dir, "_" + stamp);
            Directory.CreateDirectory(tmp);
            long exportMs = 0, prepMs = 0;
            try
            {
                if (canChange)
                {
                    using (tracker.Suspend())
                    using (var tg = new TransactionGroup(doc, DocumentTracker.TemporaryPrefix + " captura"))
                    {
                        tg.Start();
                        try
                        {
                            var prep = Stopwatch.StartNew();
                            using (var t = new Transaction(doc, DocumentTracker.TemporaryPrefix + " captura"))
                            {
                                var fo = t.GetFailureHandlingOptions();
                                fo.SetFailuresPreprocessor(new Quiet());
                                fo.SetClearAfterRollback(true);
                                t.SetFailureHandlingOptions(fo);
                                t.Start();
                                Apply(doc, view, visible, catOf);
                                if (t.Commit() != TransactionStatus.Committed) _notes.Add("Revit rechazó el resaltado; se capturó sin él");
                            }
                            prepMs = prep.ElapsedMilliseconds;
                            var ex = Stopwatch.StartNew();
                            Export(doc, view, tmp);
                            exportMs = ex.ElapsedMilliseconds;
                        }
                        finally
                        {
                            if (tg.HasStarted() && !tg.HasEnded()) tg.RollBack();
                        }
                    }
                    Info["temporal"] = "resaltado/encuadre aplicados sólo para la imagen y revertidos (el modelo no cambió)";
                }
                else
                {
                    var ex = Stopwatch.StartNew();
                    Export(doc, view, tmp);
                    exportMs = ex.ElapsedMilliseconds;
                }
                if (Error != null) return true;

                var produced = Directory.GetFiles(tmp).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                if (produced == null) { Error = "Revit no generó la imagen (¿la vista está vacía o recortada fuera del modelo?)"; return true; }
                var final = Path.Combine(dir, stamp + "_" + Safe(view.Name) + Path.GetExtension(produced).ToLowerInvariant());
                System.IO.File.Move(produced, final);
                File = final;
                Shrink();
                ReadSize();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Error = "No se pudo exportar la imagen: " + ex.Message;
                return true;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch (Exception) { }
                Prune(dir, 60);
            }

            Info["archivo"] = File;
            Info["ms"] = total.ElapsedMilliseconds;
            Info["export_ms"] = exportMs;
            if (prepMs > 0) Info["preparar_ms"] = prepMs;
            if (_notes.Count > 0) Info["notas"] = _notes.Cast<object>().ToList();
            return true;
        }

        /// <summary>
        /// Lo que se ve de cada vínculo en esta vista (arquitectura, estructura…), por categoría. Revit 2023+ trae un colector
        /// "elementos del vínculo visibles en la vista anfitriona"; en 2022 no existe y se indica.
        /// </summary>
        private void LinkContent(Document doc, View view)
        {
#if REVIT2023_OR_GREATER
            var res = new Dictionary<string, object>();
            var sw = Stopwatch.StartNew();
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                if (sw.ElapsedMilliseconds > 1500) { _notes.Add("contenido de vínculos parcial (más de 1,5 s)"); break; }
                if (li.GetLinkDocument() == null) continue;
                try { if (li.IsHidden(view)) continue; } catch (Exception) { }
                var counts = new Dictionary<string, long>();
                try
                {
                    foreach (var e in new FilteredElementCollector(doc, view.Id, li.Id).WhereElementIsNotElementType())
                    {
                        var c = e.Category;
                        if (c == null || c.CategoryType != CategoryType.Model) continue;
                        var k = CatalogRunner.BicName(c);
                        counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
                    }
                }
                catch (Exception) { continue; }
                if (counts.Count == 0) continue;
                var name = li.Name;
                var colon = name.IndexOf(" : ", StringComparison.Ordinal);
                if (colon > 0) name = name.Substring(0, colon);
                res[name] = counts.OrderByDescending(k => k.Value).Take(8).ToDictionary(k => k.Key, k => (object)k.Value);
            }
            if (res.Count > 0) Info["contenido_vinculos"] = res;
#else
            _notes.Add("Revit 2022 no permite contar lo visible de los vínculos en una vista (2023+ sí)");
#endif
        }

        private bool CanChange(Document doc, View view, bool isSheetLike)
        {
            if (view is ViewSheet) { _notes.Add("en hojas no se resalta ni encuadra: capture la vista del viewport"); return false; }
            if (view.ViewType == ViewType.Schedule) { _notes.Add("en tablas no se resalta"); return false; }
            if (doc.IsReadOnly) { _notes.Add("documento de sólo lectura: captura sin resaltar"); return false; }
            if (doc.IsModifiable) { _notes.Add("Revit está en medio de otra edición: captura sin resaltar"); return false; }
            if (!view.AreGraphicsOverridesAllowed() && !isSheetLike) { _notes.Add("esta vista no admite sobrescrituras: captura sin resaltar"); return false; }
            if (doc.IsWorkshared)
            {
                try
                {
                    var st = WorksharingUtils.GetCheckoutStatus(doc, view.Id, out var owner);
                    if (st == CheckoutStatus.OwnedByOtherUser)
                    {
                        _notes.Add("la vista la tiene tomada " + owner + ": captura sin resaltar (no se toma prestada)");
                        return false;
                    }
                }
                catch (Exception) { }
            }
            return true;
        }

        private void Apply(Document doc, View view, Dictionary<long, List<ElementId>> visible, Dictionary<long, Category> catOf)
        {
            var solid = SolidFill(doc);
            var highlighted = new HashSet<long>(Groups.SelectMany(g => g.Ids));

            if (ColorByCategory && view.AreGraphicsOverridesAllowed())
            {
                var legend = new Dictionary<string, object>();
                var extra = 0;
                var applied = 0;
                foreach (var kv in visible.OrderByDescending(k => k.Value.Count))
                {
                    var cat = catOf[kv.Key];
                    if (cat.CategoryType != CategoryType.Model) continue;
                    BuiltInCategory bic;
#if REVIT2023_OR_GREATER
                    bic = cat.BuiltInCategory;
#else
                    bic = (BuiltInCategory)cat.Id.IntegerValue;
#endif
                    if (!Palette.TryGetValue(bic, out var col))
                    {
                        if (extra >= Extra.Length) continue;
                        col = Extra[extra++];
                    }
                    var ogs = Paint(new Color(col.r, col.g, col.b), solid, 5);
                    foreach (var id in kv.Value)
                    {
                        if (highlighted.Contains(Ids.ToLong(id))) continue;
                        if (applied >= 30000) break;
                        try { view.SetElementOverrides(id, ogs); applied++; } catch (Exception) { }
                    }
                    legend[CatalogRunner.BicName(cat)] = col.name + " (" + kv.Value.Count + ")";
                }
                Info["leyenda"] = legend;
                if (applied >= 30000) _notes.Add("más de 30000 elementos visibles: se colorearon los 30000 primeros por categoría");
                _notes.Add("los colores por categoría se aplican al modelo propio; los vínculos se ven como siempre");
            }

            if (Groups.Count > 0)
            {
                var inView = new HashSet<long>(visible.Values.SelectMany(l => l).Select(Ids.ToLong));
                var res = new Dictionary<string, object>();
                foreach (var g in Groups)
                {
                    var ogs = Paint(g.Color, solid, 10);
                    long ok = 0, missing = 0, hidden = 0;
                    foreach (var id in g.Ids)
                    {
                        var eid = Ids.ToId(id);
                        var e = doc.GetElement(eid);
                        if (e == null) { missing++; continue; }
                        try { view.SetElementOverrides(eid, ogs); ok++; } catch (Exception) { hidden++; }
                    }
                    var visibleCount = g.Ids.Count(inView.Contains);
                    var d = new Dictionary<string, object> { ["color"] = g.ColorName, ["elementos"] = ok, ["visibles_en_vista"] = (long)visibleCount };
                    if (missing > 0) d["no_existen"] = missing;
                    if (hidden > 0) d["sin_sobrescritura"] = hidden;
                    res[g.Label] = d;
                }
                Info["resaltado"] = res;
            }

            if (FrameIds != null && FrameIds.Count > 0) Frame(doc, view);
        }

        private static OverrideGraphicSettings Paint(Color c, ElementId solid, int weight)
        {
            var o = new OverrideGraphicSettings();
            o.SetProjectionLineColor(c);
            o.SetCutLineColor(c);
            try { o.SetProjectionLineWeight(weight); o.SetCutLineWeight(weight); } catch (Exception) { }
            if (solid != null && solid != ElementId.InvalidElementId)
            {
                o.SetSurfaceForegroundPatternId(solid);
                o.SetSurfaceForegroundPatternColor(c);
                o.SetCutForegroundPatternId(solid);
                o.SetCutForegroundPatternColor(c);
            }
            o.SetHalftone(false);
            return o;
        }

        private static ElementId SolidFill(Document doc)
        {
            foreach (var fp in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                try { if (fp.GetFillPattern().IsSolidFill) return fp.Id; } catch (Exception) { }
            }
            return null;
        }

        /// <summary>Recorte temporal a los elementos pedidos: plantas/secciones con el recorte de la vista, 3D con caja de sección.</summary>
        private void Frame(Document doc, View view)
        {
            XYZ min = null, max = null;
            var used = 0;
            foreach (var id in FrameIds)
            {
                var e = doc.GetElement(Ids.ToId(id));
                var bb = e?.get_BoundingBox(null);
                if (bb == null) continue;
                var a = bb.Transform.OfPoint(bb.Min);
                var b = bb.Transform.OfPoint(bb.Max);
                var lo = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
                var hi = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
                min = min == null ? lo : new XYZ(Math.Min(min.X, lo.X), Math.Min(min.Y, lo.Y), Math.Min(min.Z, lo.Z));
                max = max == null ? hi : new XYZ(Math.Max(max.X, hi.X), Math.Max(max.Y, hi.Y), Math.Max(max.Z, hi.Z));
                used++;
            }
            if (min == null) { _notes.Add("encuadre: ninguno de esos elementos tiene geometría"); return; }
            var size = max - min;
            var pad = Math.Max(3.0, Margin * Math.Max(size.X, Math.Max(size.Y, size.Z)));
            min = new XYZ(min.X - pad, min.Y - pad, min.Z - pad);
            max = new XYZ(max.X + pad, max.Y + pad, max.Z + pad);

            if (view is View3D v3)
            {
                if (v3.IsPerspective) { _notes.Add("encuadre: vista en perspectiva, se captura completa"); return; }
                v3.SetSectionBox(new BoundingBoxXYZ { Min = min, Max = max });
                v3.IsSectionBoxActive = true;
                Info["encuadre"] = used + " elementos (caja de sección temporal)";
                return;
            }
            if (!(view is ViewPlan) && !(view is ViewSection))
            {
                _notes.Add("encuadre: sólo en plantas, techos, secciones, alzados y 3D");
                return;
            }
            // Una caja de referencia (scope box) bloquea el recorte: se quita sólo mientras dura la captura.
            var scope = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
            if (scope != null && !scope.IsReadOnly && scope.AsElementId() != ElementId.InvalidElementId) scope.Set(ElementId.InvalidElementId);
            var cb = view.CropBox;
            var inv = cb.Transform.Inverse;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var x in new[] { min.X, max.X })
            foreach (var y in new[] { min.Y, max.Y })
            foreach (var z in new[] { min.Z, max.Z })
            {
                var p = inv.OfPoint(new XYZ(x, y, z));
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
            }
            view.CropBox = new BoundingBoxXYZ { Transform = cb.Transform, Min = new XYZ(x0, y0, cb.Min.Z), Max = new XYZ(x1, y1, cb.Max.Z) };
            view.CropBoxActive = true;
            Info["encuadre"] = used + " elementos (recorte temporal de la vista)";
        }

        private void Export(Document doc, View view, string tmp)
        {
            var dir = FitDirectionType.Horizontal;
            try
            {
                var o = view.Outline;
                if (o != null && (o.Max.V - o.Min.V) > (o.Max.U - o.Min.U)) dir = FitDirectionType.Vertical;
            }
            catch (Exception) { }
            var opt = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(tmp, "captura"),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = dir,
                PixelSize = Px,
            };
            opt.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(opt);
        }

        /// <summary>Si el PNG pesa demasiado para la IA (planos muy densos), se pasa a JPEG: misma imagen, ~4× menos bytes.</summary>
        private void Shrink()
        {
            try
            {
                var fi = new FileInfo(File);
                if (fi.Length <= 1_500_000) return;
                System.Windows.Media.Imaging.BitmapSource frame;
                using (var s = fi.OpenRead())
                {
                    var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(s, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    frame = dec.Frames[0];
                }
                var f = frame.Format;
                if (f == System.Windows.Media.PixelFormats.Bgra32 || f == System.Windows.Media.PixelFormats.Pbgra32 || f == System.Windows.Media.PixelFormats.Prgba64 || f == System.Windows.Media.PixelFormats.Rgba64)
                    return; // con transparencia el JPEG saldría negro; se deja el PNG
                var jpg = Path.ChangeExtension(File, ".jpg");
                var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                using (var o = System.IO.File.Create(jpg)) enc.Save(o);
                System.IO.File.Delete(File);
                File = jpg;
                Mime = "image/jpeg";
            }
            catch (Exception) { }
        }

        private void ReadSize()
        {
            try
            {
                var len = new FileInfo(File).Length;
                Info["bytes"] = len;
                if (Mime != "image/png") return;
                using (var s = System.IO.File.OpenRead(File))
                {
                    var h = new byte[24];
                    if (s.Read(h, 0, 24) < 24) return;
                    Info["px"] = ((h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19]) + "x" + ((h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23]);
                }
            }
            catch (Exception) { }
        }

        private static void Prune(string dir, int keep)
        {
            try
            {
                foreach (var f in new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.CreationTimeUtc).Skip(keep))
                {
                    try { f.Delete(); } catch (Exception) { }
                }
                foreach (var d in new DirectoryInfo(dir).GetDirectories("_*"))
                {
                    if (DateTime.UtcNow - d.CreationTimeUtc > TimeSpan.FromHours(1)) { try { d.Delete(true); } catch (Exception) { } }
                }
            }
            catch (Exception) { }
        }

        private static string Safe(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var r = new string((s ?? "vista").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
            return r.Length > 60 ? r.Substring(0, 60) : r;
        }

        private static object SafeScale(View v)
        {
            try { return v.ViewType == ViewType.Schedule || v.Scale <= 0 ? null : (object)(long)v.Scale; }
            catch (Exception) { return null; }
        }

        /// <summary>Los avisos no abren diálogos; los errores revierten (y la captura sale sin resaltar).</summary>
        private sealed class Quiet : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                var error = false;
                foreach (var m in a.GetFailureMessages())
                {
                    if (m.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(m);
                    else error = true;
                }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }

        /// <summary>Color por nombre (rojo, azul…) o #RRGGBB.</summary>
        public static bool TryNamedColor(string s, out Color c, out string name)
        {
            c = null; name = s;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (Named.TryGetValue(s.Trim(), out var rgb)) { c = new Color(rgb.r, rgb.g, rgb.b); name = s.Trim().ToLowerInvariant(); return true; }
            if (WriteKit.TryColor(s, out c)) { name = s.Trim().ToUpperInvariant(); return true; }
            return false;
        }
    }
}
