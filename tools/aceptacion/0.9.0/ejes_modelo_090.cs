// SPDX-License-Identifier: GPL-3.0-only
// Aceptación 0.9.0 (N9-02): ejes del modelo para el registro plano–modelo. SÓLO LECTURA (exec modo consulta).
// Devuelve [{"nombre":"A","p":[x0,y0,x1,y1]}] en pies, coordenadas internas del documento (lo que espera power flujo ejes=...).
// Sólo ejes rectos; los curvos o multisegmento se listan aparte y no se usan como anclas.
var rectos = new List<object>();
var omitidos = new List<object>();
foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(x => x.Name, StringComparer.Ordinal))
{
    var l = g.Curve as Line;
    if (l == null) { omitidos.Add(g.Name); continue; }
    var a = l.GetEndPoint(0); var b = l.GetEndPoint(1);
    rectos.Add(new Dictionary<string, object> { ["nombre"] = g.Name, ["p"] = new List<object> { Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(b.X, 6), Math.Round(b.Y, 6) } });
}
var json = PDI.RevitMcp.Protocol.Json.Write(rectos);
var ruta = ctx.Artefacto("ejes_modelo_090.json", json);
return new { ejes = rectos.Count, omitidos, ruta };
