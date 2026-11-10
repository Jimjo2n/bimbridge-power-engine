// SPDX-License-Identifier: GPL-3.0-only
// Benchmark 0.6.0 · carga 2 (QC geométrico de luces) · motor MCP. Equivalente: pyrevit_2_montaje.py (mismo algoritmo).
// exec codigo/archivo (modo consulta) args={"nivel":"<nombre o vacío>","tol_mm":3,"max_mm":3000}
var sw = System.Diagnostics.Stopwatch.StartNew();
var nivel = args.ContainsKey("nivel") ? Convert.ToString(args["nivel"]) : null;
var tol = args.ContainsKey("tol_mm") ? Convert.ToDouble(args["tol_mm"]) : 3.0;
var max = args.ContainsKey("max_mm") ? Convert.ToDouble(args["max_mm"]) : 3000.0;
var v3 = Bim.Montaje.Vista3D(doc);
var luces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType()
    .Where(e => string.IsNullOrEmpty(nivel) || (doc.GetElement(e.LevelId) as Level)?.Name == nivel).ToList();
var cuenta = new SortedDictionary<string, int>();
var muestra = new List<object>();
foreach (var e in luces)
{
    var r = Bim.Montaje.Soporte(e, v3, "arriba", max, tol);
    var c = Convert.ToString(r["clase"]);
    cuenta[c] = cuenta.TryGetValue(c, out var n) ? n + 1 : 1;
    if (c != "al_ras" && muestra.Count < 5) muestra.Add(new object[] { r["id"], c, r.ContainsKey("holgura_mm") ? r["holgura_mm"] : null });
}
return new { carga = "montaje", motor = "mcp", luces = luces.Count, cuenta, muestra, ms = sw.ElapsedMilliseconds };
