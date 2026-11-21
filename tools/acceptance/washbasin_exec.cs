// SPDX-License-Identifier: GPL-3.0-only
// Washbasin case with the general runner (exec), without using the typed act actions.
// Creates or reconciles two water connectors in the open RFA: "fria" (cold) and "caliente" (hot), with RADIUS 6.5 mm (not diameter).
// Side: facing the fixture from the front, cold on the right and hot on the left. The front is taken as the -Y side
// of the family (Revit Front view convention); if the family uses another front, pass frente="+Y", "-X" or "+X".
// Flow: "In" or "Bidirectional" is NOT decided; if flujo= is not passed, it is not changed and is reported as pending.
//
// Usage (doc = handle of the RFA, from "revit docs" or from "act abrir familia=..."):
//   exec modo=ejecutar tx=auto doc=dN args={"cara_fria":"812:3","cara_caliente":"812:5","radio_mm":6.5}
// Repeating it does not duplicate: it first searches by the connector description ("fria"/"caliente").
using Autodesk.Revit.DB.Plumbing;

public static class Washbasin
{
    public static object Run(Bimbridge.Addin.Scripting.ScriptContext ctx)
    {
        var doc = ctx.Doc;
        if (!doc.IsFamilyDocument) throw new InvalidOperationException("open the washbasin RFA (act abrir familia=...) and pass doc=dN");
        var a = ctx.Args;
        string S(string k) => a != null && a.ContainsKey(k) && a[k] != null ? Convert.ToString(a[k], System.Globalization.CultureInfo.InvariantCulture) : null;
        var radiusMm = S("radio_mm") != null ? double.Parse(S("radio_mm"), System.Globalization.CultureInfo.InvariantCulture) : 6.5;
        var radiusFt = radiusMm / 304.8;
        var front = (S("frente") ?? "-Y").ToUpperInvariant();
        var flow = S("flujo");

        // Planar faces with a reference, numbered the same as "revit fam caras" (element:n).
        var faces = new List<(string label, PlanarFace face)>();
        var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType()
                     .Where(x => x is GenericForm || x is FreeFormElement || x is ImportInstance).OrderBy(x => ctx.Long(x.Id)))
        {
            var geo = e.get_Geometry(opt);
            if (geo == null) continue;
            var idx = 0;
            foreach (var obj in geo)
            {
                if (!(obj is Solid s) || s.Volume <= 0) continue;
                foreach (Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf)) continue;
                    idx++;
                    if (pf.Reference != null) faces.Add((ctx.Long(e.Id) + ":" + idx, pf));
                }
            }
        }
        if (S("cara_fria") == null || S("cara_caliente") == null)
            return new { estado = "missing faces", aviso = "pass cara_fria and cara_caliente (element:n); candidates below", caras = faces.Select(f => new { f.label, normal = f.face.FaceNormal, origen_mm = new[] { f.face.Origin.X * 304.8, f.face.Origin.Y * 304.8, f.face.Origin.Z * 304.8 } }).Take(60) };

        PlanarFace Face(string label) => faces.FirstOrDefault(f => f.label == label).face ?? throw new ArgumentException("face does not exist: " + label);
        var coldFace = Face(S("cara_fria"));
        var hotFace = Face(S("cara_caliente"));

        var all = new FilteredElementCollector(doc).OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>().ToList();
        string Desc(ConnectorElement c) => c.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString() ?? "";
        var changes = new List<string>();
        var pending = new List<string>();

        ConnectorElement Upsert(string key, PipeSystemType sys, PlanarFace face)
        {
            var mine = all.Where(c => string.Equals(Desc(c), key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count > 1) throw new InvalidOperationException("there are already " + mine.Count + " connectors '" + key + "': resolve the duplicate first");
            var c = mine.FirstOrDefault();
            if (c == null)
            {
                c = ConnectorElement.CreatePipeConnector(doc, sys, face.Reference);
                c.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.Set(key);
                changes.Add(key + ": created");
            }
            var pr = c.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
            var assoc = doc.FamilyManager.GetAssociatedFamilyParameter(pr);
            if (assoc != null)
            {
                if (Math.Abs(pr.AsDouble() - radiusFt) > 1e-6)
                    pending.Add(key + ": the radius is driven by '" + assoc.Definition.Name + "'; change it in that parameter (the association is not broken)");
            }
            else if (Math.Abs(pr.AsDouble() - radiusFt) > 1e-6) { pr.Set(radiusFt); changes.Add(key + ": radius → " + radiusMm + " mm"); }
            if (flow != null)
            {
                var fv = flow.Equals("In", StringComparison.OrdinalIgnoreCase) ? FlowDirectionType.In
                       : flow.StartsWith("Bi", StringComparison.OrdinalIgnoreCase) ? FlowDirectionType.Bidirectional
                       : flow.Equals("Out", StringComparison.OrdinalIgnoreCase) ? FlowDirectionType.Out
                       : throw new ArgumentException("flujo must be In, Out or Bidirectional");
                var pf = c.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM);
                if (pf != null && pf.AsInteger() != (int)fv) { pf.Set((int)fv); changes.Add(key + ": flow → " + fv); }
            }
            return c;
        }

        var cold = Upsert("fria", PipeSystemType.DomesticColdWater, coldFace);
        var hot = Upsert("caliente", PipeSystemType.DomesticHotWater, hotFace);
        if (flow == null) pending.Add("flow In or Bidirectional undecided: not changed");

        // Side: the "right" of an observer facing the front. With front -Y the observer looks toward +Y and their right is +X.
        XYZ right = front == "-Y" ? XYZ.BasisX : front == "+Y" ? -XYZ.BasisX : front == "-X" ? -XYZ.BasisY : front == "+X" ? XYZ.BasisY
                    : throw new ArgumentException("frente must be -Y, +Y, -X or +X");
        var side = (cold.Origin - hot.Origin).DotProduct(right);
        var sideOk = side > 1e-6;
        if (!sideOk) pending.Add("the cold connector is NOT to the right of the hot one facing front " + front + ": change the faces");

        // Verification: one "fria" connector and one "caliente" connector, exact radius, no duplicates.
        doc.Regenerate();
        var after = new FilteredElementCollector(doc).OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>().ToList();
        object V(ConnectorElement c) => new
        {
            id = ctx.Long(c.Id), clave = Desc(c), sistema = c.SystemClassification.ToString(),
            radio_mm = Math.Round(c.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS).AsDouble() * 304.8, 4),
            flujo = c.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM)?.AsValueString(),
            origen_mm = new[] { Math.Round(c.Origin.X * 304.8, 1), Math.Round(c.Origin.Y * 304.8, 1), Math.Round(c.Origin.Z * 304.8, 1) },
        };
        return new
        {
            cambios = changes, pendientes = pending,
            conectores = after.Select(V).ToList(),
            n_fria = after.Count(c => Desc(c) == "fria"), n_caliente = after.Count(c => Desc(c) == "caliente"),
            lado_correcto = sideOk, frente = front,
        };
    }
}
