// SPDX-License-Identifier: GPL-3.0-only
// Aceptación 0.6.0 (D): genera un manifiesto JSON a partir de las luminarias ACTUALES de un nivel de la COPIA de práctica.
// exec (modo consulta) args={"nivel":"LEVEL 03","vista_etiquetas":<id RCP>} → guarda manifiesto_060.json como artefacto y devuelve la ruta.
// Sirve para la prueba de desviaciones conocidas: después se perturba la copia y qc_manifiesto debe encontrar cada cambio.
var nivel = Convert.ToString(args["nivel"]);
View vista = args.ContainsKey("vista_etiquetas") ? ctx.E<View>(Convert.ToInt64(args["vista_etiquetas"])) : null;
var luces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType()
    .Where(e => (doc.GetElement(e.LevelId) as Level)?.Name == nivel).ToList();
var reales = Bim.Manifiesto.Juntar(doc, luces, vista);
var elementos = reales.OrderBy(a => a.Uid, StringComparer.Ordinal).Select(a => (object)new Dictionary<string, object>
{
    ["ref"] = "L-" + a.Id, ["x"] = Math.Round(a.X, 1), ["y"] = Math.Round(a.Y, 1), ["z"] = Math.Round(a.Z, 1), ["giro"] = a.RotDeg.HasValue ? Math.Round(a.RotDeg.Value, 2) : (object)null,
    ["nivel"] = a.Level, ["tipos"] = new List<object> { a.FullType }, ["etiqueta"] = vista == null ? (object)null : a.Tags > 0,
    ["fuente"] = "modelo de práctica antes de perturbar (prueba de aceptación, no un plano)",
}).ToList();
var json = PDI.RevitMcp.Protocol.Json.Write(new Dictionary<string, object> { ["tolerancias"] = new Dictionary<string, object> { ["xy"] = 25, ["z"] = 10, ["giro"] = 1, ["duplicado"] = 100 }, ["elementos"] = elementos });
var ruta = ctx.Artefacto("manifiesto_060.json", json);
return new { luces = elementos.Count, ruta };
