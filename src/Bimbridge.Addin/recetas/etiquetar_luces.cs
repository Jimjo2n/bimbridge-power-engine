// SPDX-License-Identifier: GPL-3.0-only
// Recipe shipped with BIMBRIDGE - Power Engine (since 0.6.0). One tag per fixture in ONE view (rule "exactly one tag per fixture"):
// one unit per fixture visible in the view; if it already has a tag in that view it is skipped (no duplicates on resume or repeat).
// Ends by saving (barrier) and, if sheets are requested, exporting the PDF. The postcondition lists fixtures with 0 or more than 1 tag.
// args: vista (ElementId of the view), tipo_etiqueta (ElementId of the tag type; optional: by category),
//       guardar (si|no, default si), pdf_hojas (list of sheet numbers, optional), pdf_carpeta, pdf_nombre.
public class EtiquetarLuces : Receta
{
    public override string Version => "1";
    public override string Descripcion => "One tag per fixture in a view; resumable without duplicates; saves and optionally exports PDF";
    public override IEnumerable<string> Entradas => new[] { "vista" };

    private static View Vista(ScriptContext ctx) => ctx.E<View>(Convert.ToInt64(ctx.Args["vista"])) ?? throw new ArgumentException("the view does not exist");

    private static int TagsOf(Document doc, View v, ElementId id)
    {
        var n = 0;
        foreach (IndependentTag t in new FilteredElementCollector(doc, v.Id).OfClass(typeof(IndependentTag)))
        {
            try { if (t.GetTaggedLocalElementIds().Contains(id)) n++; } catch (Exception) { }
        }
        return n;
    }

    public override void Preparar(ScriptContext ctx)
    {
        base.Preparar(ctx);
        var v = Vista(ctx);
        if (v.IsTemplate) throw new ArgumentException("the view is a template");
    }

    public override IEnumerable<Unidad> Unidades(ScriptContext ctx)
    {
        var v = Vista(ctx);
        foreach (var e in new FilteredElementCollector(ctx.Doc, v.Id).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType()
                     .OrderBy(x => x.UniqueId, StringComparer.Ordinal))
            yield return new Unidad("tag:" + v.UniqueId + ":" + e.UniqueId, new Dictionary<string, object> { ["uid"] = e.UniqueId }, new[] { ctx.Long(e.Id) });
        if (!string.Equals(Convert.ToString(ctx.Args.TryGetValue("guardar", out var g) ? g : "si"), "no", StringComparison.OrdinalIgnoreCase))
            yield return Unidad.Guardar();
        if (ctx.Args.TryGetValue("pdf_hojas", out var h) && h != null)
        {
            var o = new Dictionary<string, object> { ["hojas"] = h };
            if (ctx.Args.TryGetValue("pdf_carpeta", out var c)) o["carpeta"] = c;
            if (ctx.Args.TryGetValue("pdf_nombre", out var nm)) o["nombre"] = nm;
            o["sobrescribir"] = true;
            yield return Unidad.Pdf("pdf", o);
        }
    }

    public override object Ejecutar(ScriptContext ctx, Unidad u)
    {
        var v = Vista(ctx);
        var e = ctx.Doc.GetElement(Convert.ToString(u.D("uid")));
        if (e == null) ctx.Omitir("the fixture no longer exists");
        if (TagsOf(ctx.Doc, v, e.Id) > 0) ctx.Omitir("it already had a tag in the view");
        var p = (e.Location as LocationPoint)?.Point ?? (e.get_BoundingBox(v) is BoundingBoxXYZ bb ? (bb.Min + bb.Max) / 2 : null);
        if (p == null) { ctx.Excepcion("no insertion point", ctx.Long(e.Id)); ctx.Omitir("no point"); }
        IndependentTag tag;
        if (ctx.Args.TryGetValue("tipo_etiqueta", out var tt) && tt != null)
            tag = IndependentTag.Create(ctx.Doc, ctx.Id(Convert.ToInt64(tt)), v.Id, new Reference(e), false, TagOrientation.Horizontal, p);
        else
            tag = IndependentTag.Create(ctx.Doc, v.Id, new Reference(e), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, p);
        return new Dictionary<string, object> { ["luz"] = ctx.Long(e.Id), ["etiqueta"] = ctx.Long(tag.Id) };
    }

    public override Verificacion Verificar(ScriptContext ctx, Unidad u)
    {
        var e = ctx.Doc.GetElement(Convert.ToString(u.D("uid")));
        if (e == null) return Verificacion.Desconocida;
        return TagsOf(ctx.Doc, Vista(ctx), e.Id) > 0 ? Verificacion.Aplicada : Verificacion.NoAplicada;
    }

    public override object Postcondicion(ScriptContext ctx)
    {
        var v = Vista(ctx);
        var count = new Dictionary<ElementId, int>();
        foreach (IndependentTag t in new FilteredElementCollector(ctx.Doc, v.Id).OfClass(typeof(IndependentTag)))
        {
            ICollection<ElementId> ids;
            try { ids = t.GetTaggedLocalElementIds(); } catch (Exception) { continue; }
            foreach (var id in ids) count[id] = count.TryGetValue(id, out var n) ? n + 1 : 1;
        }
        var luces = new FilteredElementCollector(ctx.Doc, v.Id).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType().ToElementIds();
        var sin = luces.Where(i => !count.ContainsKey(i)).Select(i => (object)("untagged: " + ctx.Long(i)));
        var dobles = luces.Where(i => count.TryGetValue(i, out var n) && n > 1).Select(i => (object)("with " + count[i] + " tags: " + ctx.Long(i)));
        return new Dictionary<string, object> { ["luces_en_vista"] = luces.Count, ["desviaciones"] = sin.Concat(dobles).ToList() };
    }
}
