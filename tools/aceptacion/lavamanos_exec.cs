// SPDX-License-Identifier: GPL-3.0-only
// Caso lavamanos con el runner general (exec), sin usar las acciones tipadas de act.
// Crea o reconcilia dos conectores de agua en el RFA abierto: "fria" y "caliente", con RADIO 6.5 mm (no diámetro).
// Lado: mirando el aparato de frente, fría a la derecha y caliente a la izquierda. El frente se toma como el lado -Y
// de la familia (convención de la vista Front de Revit); si la familia usa otro frente, pase frente="+Y", "-X" o "+X".
// Flujo: "In" o "Bidirectional" NO está resuelto; si no se pasa flujo=, no se cambia y se informa como pendiente.
//
// Uso (doc = handle del RFA, de "revit docs" o de "act abrir familia=..."):
//   exec modo=ejecutar tx=auto doc=dN args={"cara_fria":"812:3","cara_caliente":"812:5","radio_mm":6.5}
// Repetirlo no duplica: busca primero por la descripción del conector ("fria"/"caliente").
using Autodesk.Revit.DB.Plumbing;

public static class Lavamanos
{
    public static object Run(PDI.RevitMcp.Addin.Scripting.ScriptContext ctx)
    {
        var doc = ctx.Doc;
        if (!doc.IsFamilyDocument) throw new InvalidOperationException("abra el RFA del lavamanos (act abrir familia=...) y pase doc=dN");
        var a = ctx.Args;
        string S(string k) => a != null && a.ContainsKey(k) && a[k] != null ? Convert.ToString(a[k], System.Globalization.CultureInfo.InvariantCulture) : null;
        var radioMm = S("radio_mm") != null ? double.Parse(S("radio_mm"), System.Globalization.CultureInfo.InvariantCulture) : 6.5;
        var radioFt = radioMm / 304.8;
        var frente = (S("frente") ?? "-Y").ToUpperInvariant();
        var flujo = S("flujo");

        // Caras planas con referencia, numeradas igual que "revit fam caras" (elemento:n).
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
            return new { estado = "faltan caras", aviso = "pase cara_fria y cara_caliente (elemento:n); candidatas abajo", caras = faces.Select(f => new { f.label, normal = f.face.FaceNormal, origen_mm = new[] { f.face.Origin.X * 304.8, f.face.Origin.Y * 304.8, f.face.Origin.Z * 304.8 } }).Take(60) };

        PlanarFace Face(string label) => faces.FirstOrDefault(f => f.label == label).face ?? throw new ArgumentException("no existe la cara " + label);
        var fFria = Face(S("cara_fria"));
        var fCal = Face(S("cara_caliente"));

        var all = new FilteredElementCollector(doc).OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>().ToList();
        string Desc(ConnectorElement c) => c.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString() ?? "";
        var cambios = new List<string>();
        var pendientes = new List<string>();

        ConnectorElement Upsert(string clave, PipeSystemType sys, PlanarFace face)
        {
            var mine = all.Where(c => string.Equals(Desc(c), clave, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count > 1) throw new InvalidOperationException("ya hay " + mine.Count + " conectores '" + clave + "': resuelva el duplicado antes");
            var c = mine.FirstOrDefault();
            if (c == null)
            {
                c = ConnectorElement.CreatePipeConnector(doc, sys, face.Reference);
                c.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.Set(clave);
                cambios.Add(clave + ": creado");
            }
            var pr = c.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
            var assoc = doc.FamilyManager.GetAssociatedFamilyParameter(pr);
            if (assoc != null)
            {
                if (Math.Abs(pr.AsDouble() - radioFt) > 1e-6)
                    pendientes.Add(clave + ": el radio lo controla '" + assoc.Definition.Name + "'; cámbielo en ese parámetro (no se rompe la asociación)");
            }
            else if (Math.Abs(pr.AsDouble() - radioFt) > 1e-6) { pr.Set(radioFt); cambios.Add(clave + ": radio → " + radioMm + " mm"); }
            if (flujo != null)
            {
                var fv = flujo.Equals("In", StringComparison.OrdinalIgnoreCase) ? FlowDirectionType.In
                       : flujo.StartsWith("Bi", StringComparison.OrdinalIgnoreCase) ? FlowDirectionType.Bidirectional
                       : flujo.Equals("Out", StringComparison.OrdinalIgnoreCase) ? FlowDirectionType.Out
                       : throw new ArgumentException("flujo debe ser In, Out o Bidirectional");
                var pf = c.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM);
                if (pf != null && pf.AsInteger() != (int)fv) { pf.Set((int)fv); cambios.Add(clave + ": flujo → " + fv); }
            }
            return c;
        }

        var fria = Upsert("fria", PipeSystemType.DomesticColdWater, fFria);
        var cal = Upsert("caliente", PipeSystemType.DomesticHotWater, fCal);
        if (flujo == null) pendientes.Add("flujo In o Bidirectional sin decidir: no se cambió");

        // Lado: "derecha" del observador que mira el frente. Con frente -Y el observador mira hacia +Y y su derecha es +X.
        XYZ derecha = frente == "-Y" ? XYZ.BasisX : frente == "+Y" ? -XYZ.BasisX : frente == "-X" ? -XYZ.BasisY : frente == "+X" ? XYZ.BasisY
                    : throw new ArgumentException("frente debe ser -Y, +Y, -X o +X");
        var lado = (fria.Origin - cal.Origin).DotProduct(derecha);
        var ladoOk = lado > 1e-6;
        if (!ladoOk) pendientes.Add("la fría NO queda a la derecha de la caliente mirando el frente " + frente + ": cambie las caras");

        // Verificación: un conector "fria" y uno "caliente", radio exacto, sin duplicados.
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
            cambios, pendientes,
            conectores = after.Select(V).ToList(),
            n_fria = after.Count(c => Desc(c) == "fria"), n_caliente = after.Count(c => Desc(c) == "caliente"),
            lado_correcto = ladoOk, frente,
        };
    }
}
