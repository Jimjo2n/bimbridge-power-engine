// SPDX-License-Identifier: GPL-3.0-only
// Acceptance 0.6.0 (D): generates a JSON manifest from the CURRENT fixtures of one level of the practice COPY.
// exec (modo consulta) args={"nivel":"LEVEL 03","vista_etiquetas":<RCP id>} → saves manifest_060.json as an artifact and returns the path.
// Used for the known-deviations test: the copy is then perturbed and qc_manifiesto must find each change.
var level = Convert.ToString(args["nivel"]);
View view = args.ContainsKey("vista_etiquetas") ? ctx.E<View>(Convert.ToInt64(args["vista_etiquetas"])) : null;
var fixtures = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType()
    .Where(e => (doc.GetElement(e.LevelId) as Level)?.Name == level).ToList();
var actual = Bim.Manifest.Collect(doc, fixtures, view);
var items = actual.OrderBy(a => a.Uid, StringComparer.Ordinal).Select(a => (object)new Dictionary<string, object>
{
    ["ref"] = "L-" + a.Id, ["x"] = Math.Round(a.X, 1), ["y"] = Math.Round(a.Y, 1), ["z"] = Math.Round(a.Z, 1), ["giro"] = a.RotDeg.HasValue ? Math.Round(a.RotDeg.Value, 2) : (object)null,
    ["nivel"] = a.Level, ["tipos"] = new List<object> { a.FullType }, ["etiqueta"] = view == null ? (object)null : a.Tags > 0,
    ["fuente"] = "practice model before perturbation (acceptance test, not a drawing)",
}).ToList();
var json = Bimbridge.Protocol.Json.Write(new Dictionary<string, object> { ["tolerancias"] = new Dictionary<string, object> { ["xy"] = 25, ["z"] = 10, ["giro"] = 1, ["duplicado"] = 100 }, ["elementos"] = items });
var path = ctx.Artifact("manifest_060.json", json);
return new { luces = items.Count, ruta = path };
