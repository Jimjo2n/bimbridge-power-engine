// SPDX-License-Identifier: GPL-3.0-only
// Aceptación 0.9.0 (N9-05): tramos de conduit de un nivel con sus conectores, para el análisis de red en tres capas
// (continuidad geométrica, conexión física, proximidad). SÓLO LECTURA (exec modo consulta). args={"nivel":"Level 1"}
// Devuelve [{"id","a":[x,y,z],"b":[x,y,z],"diam","sistema","con_a","con_b"}] en pies; con_* = id del elemento conectado o null.
var nivel = args.ContainsKey("nivel") ? Convert.ToString(args["nivel"]) : null;
var tramos = new List<object>();
foreach (var c in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Conduit).WhereElementIsNotElementType().OfType<MEPCurve>())
{
    if (nivel != null && (doc.GetElement(c.ReferenceLevel?.Id ?? ElementId.InvalidElementId) as Level)?.Name != nivel) continue;
    var l = (c.Location as LocationCurve)?.Curve as Line;
    if (l == null) continue;
    var a = l.GetEndPoint(0); var b = l.GetEndPoint(1);
    string Other(XYZ end)
    {
        foreach (Connector k in c.ConnectorManager.Connectors)
        {
            if (k.ConnectorType != ConnectorType.End || k.Origin.DistanceTo(end) > 1e-3) continue;
            foreach (Connector r in k.AllRefs)
                if (r.Owner != null && r.Owner.Id != c.Id && r.ConnectorType != ConnectorType.Logical) return ctx.Long(r.Owner.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }
    var d = c.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble() ?? 0;
    tramos.Add(new Dictionary<string, object>
    {
        ["id"] = ctx.Long(c.Id).ToString(System.Globalization.CultureInfo.InvariantCulture), ["a"] = new List<object> { a.X, a.Y, a.Z }, ["b"] = new List<object> { b.X, b.Y, b.Z },
        ["diam"] = d, ["sistema"] = c.LookupParameter("Service Type")?.AsString(), ["con_a"] = Other(a), ["con_b"] = Other(b),
    });
}
var ruta = ctx.Artefacto("conductos_090.json", PDI.RevitMcp.Protocol.Json.Write(tramos));
return new { tramos = tramos.Count, ruta };
