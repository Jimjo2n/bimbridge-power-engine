// SPDX-License-Identifier: GPL-3.0-only
// Benchmark 0.6.0 · carga 3 (grupos, vistas, láminas y exportación) · motor MCP. Equivalente: pyrevit_3_grupos_vistas_pdf.py.
// exec modo=ejecutar tx=ninguna args={"hojas":3,"carpeta":"%TEMP%\\pdi_bench"}  — no cambia el modelo; exporta un PDF a carpeta temporal.
var sw = System.Diagnostics.Stopwatch.StartNew();
var grupos = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().ToList();
int miembros = 0, espejados = 0;
foreach (var g in grupos) { var i = Bim.Grupos.Inspeccionar(g); miembros += (int)i["miembros"]; espejados += (int)i["miembros_espejados"]; }
var planos = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && v.ViewType != ViewType.AreaPlan).ToList();
int rangos = 0, conPlantilla = 0;
foreach (var v in planos) { var r = Bim.Vistas.Rango(v); rangos++; if (r.ContainsKey("plantilla")) conPlantilla++; }
var hojas = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber).ToList();
var n = args.ContainsKey("hojas") ? Convert.ToInt32(args["hojas"]) : 3;
var carpeta = Environment.ExpandEnvironmentVariables(args.ContainsKey("carpeta") ? Convert.ToString(args["carpeta"]) : "%TEMP%\\pdi_bench");
Directory.CreateDirectory(carpeta);
var nombre = "mcp_" + DateTime.Now.ToString("HHmmssfff");
var opt = new PDFExportOptions { FileName = nombre, Combine = true };
var t0 = sw.ElapsedMilliseconds;
var ok = doc.Export(carpeta, hojas.Take(n).Select(s => s.Id).ToList(), opt);
var pdf = Path.Combine(carpeta, nombre + ".pdf");
return new { carga = "grupos_vistas_pdf", motor = "mcp", grupos = grupos.Count, miembros, espejados, planos = planos.Count, rangos, conPlantilla, hojas = hojas.Count,
    exportadas = Math.Min(n, hojas.Count), pdf_ok = ok && File.Exists(pdf), pdf_bytes = File.Exists(pdf) ? new FileInfo(pdf).Length : 0, exportar_ms = sw.ElapsedMilliseconds - t0, ms = sw.ElapsedMilliseconds };
