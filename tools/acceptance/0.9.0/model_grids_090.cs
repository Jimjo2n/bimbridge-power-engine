// SPDX-License-Identifier: GPL-3.0-only
// Acceptance 0.9.0 (N9-02): model grids for drawing–model registration. READ ONLY (exec modo consulta).
// Returns [{"nombre":"A","p":[x0,y0,x1,y1]}] in feet, internal document coordinates (what power flujo ejes=... expects).
// Straight grids only; curved or multi-segment grids are listed separately and not used as anchors.
var rectos = new List<object>();
var omitidos = new List<object>();
foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(x => x.Name, StringComparer.Ordinal))
{
    var l = g.Curve as Line;
    if (l == null) { omitidos.Add(g.Name); continue; }
    var a = l.GetEndPoint(0); var b = l.GetEndPoint(1);
    rectos.Add(new Dictionary<string, object> { ["nombre"] = g.Name, ["p"] = new List<object> { Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(b.X, 6), Math.Round(b.Y, 6) } });
}
var json = Bimbridge.Protocol.Json.Write(rectos);
var ruta = ctx.Artefacto("model_grids_090.json", json);
return new { ejes = rectos.Count, omitidos, ruta };
