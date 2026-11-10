// SPDX-License-Identifier: GPL-3.0-only
// Receta incluida en BIMBRIDGE - Power Engine (desde 0.6.0). Una etiqueta por luminaria en UNA vista (regla "exactamente una etiqueta por luminaria"):
// una unidad por luminaria visible en la vista; si ya tiene etiqueta en esa vista se omite (no duplica al reanudar ni al repetir).
// Termina guardando (barrera) y, si se piden hojas, exportando el PDF. La postcondición lista luminarias con 0 o más de 1 etiqueta.
// args: vista (ElementId de la vista), tipo_etiqueta (ElementId del tipo de etiqueta; opcional: por categoría),
//       guardar (si|no, por defecto si), pdf_hojas (lista de números de hoja, opcional), pdf_carpeta, pdf_nombre.
public class EtiquetarLuces : Receta
{
    public override string Version => "1";
    public override string Descripcion => "Una etiqueta por luminaria en una vista; reanudable sin duplicar; guarda y exporta PDF opcional";
    public override IEnumerable<string> Entradas => new[] { "vista" };

    private static View Vista(ScriptContext ctx) => ctx.E<View>(Convert.ToInt64(ctx.Args["vista"])) ?? throw new ArgumentException("la vista no existe");

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
        if (v.IsTemplate) throw new ArgumentException("la vista es una plantilla");
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
        if (e == null) ctx.Omitir("la luminaria ya no existe");
        if (TagsOf(ctx.Doc, v, e.Id) > 0) ctx.Omitir("ya tenía etiqueta en la vista");
        var p = (e.Location as LocationPoint)?.Point ?? (e.get_BoundingBox(v) is BoundingBoxXYZ bb ? (bb.Min + bb.Max) / 2 : null);
        if (p == null) { ctx.Excepcion("sin punto de inserción", ctx.Long(e.Id)); ctx.Omitir("sin punto"); }
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
        var sin = luces.Where(i => !count.ContainsKey(i)).Select(i => (object)("sin etiqueta: " + ctx.Long(i)));
        var dobles = luces.Where(i => count.TryGetValue(i, out var n) && n > 1).Select(i => (object)("con " + count[i] + " etiquetas: " + ctx.Long(i)));
        return new Dictionary<string, object> { ["luces_en_vista"] = luces.Count, ["desviaciones"] = sin.Concat(dobles).ToList() };
    }
}
