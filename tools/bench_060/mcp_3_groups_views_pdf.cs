// SPDX-License-Identifier: GPL-3.0-only
// Benchmark 0.6.0 · workload 3 (groups, views, sheets and export) · MCP engine. Equivalent: pyrevit_3_groups_views_pdf.py.
// exec modo=ejecutar tx=ninguna args={"hojas":3,"carpeta":"%TEMP%\\bimbridge_bench"}  — does not change the model; exports a PDF to a temporary folder.
var sw = System.Diagnostics.Stopwatch.StartNew();
var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().ToList();
int members = 0, mirrored = 0;
foreach (var g in groups) { var i = Bim.Groups.Inspect(g); members += (int)i["miembros"]; mirrored += (int)i["miembros_espejados"]; }
var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && v.ViewType != ViewType.AreaPlan).ToList();
int ranges = 0, withTemplate = 0;
foreach (var v in plans) { var r = Bim.Views.Range(v); ranges++; if (r.ContainsKey("plantilla")) withTemplate++; }
var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber).ToList();
var n = args.ContainsKey("hojas") ? Convert.ToInt32(args["hojas"]) : 3;
var folder = Environment.ExpandEnvironmentVariables(args.ContainsKey("carpeta") ? Convert.ToString(args["carpeta"]) : "%TEMP%\\bimbridge_bench");
Directory.CreateDirectory(folder);
var name = "mcp_" + DateTime.Now.ToString("HHmmssfff");
var opt = new PDFExportOptions { FileName = name, Combine = true };
var t0 = sw.ElapsedMilliseconds;
var ok = doc.Export(folder, sheets.Take(n).Select(s => s.Id).ToList(), opt);
var pdf = Path.Combine(folder, name + ".pdf");
return new { carga = "grupos_vistas_pdf", motor = "mcp", grupos = groups.Count, miembros = members, espejados = mirrored, planos = plans.Count, rangos = ranges, conPlantilla = withTemplate, hojas = sheets.Count,
    exportadas = Math.Min(n, sheets.Count), pdf_ok = ok && File.Exists(pdf), pdf_bytes = File.Exists(pdf) ? new FileInfo(pdf).Length : 0, exportar_ms = sw.ElapsedMilliseconds - t0, ms = sw.ElapsedMilliseconds };
