// SPDX-License-Identifier: GPL-3.0-only
// Recipe shipped with BIMBRIDGE - Power Engine (since 0.6.0). One tag per fixture in ONE view (rule "exactly one tag per fixture"):
// one unit per fixture visible in the view; if it already has a tag in that view it is skipped (no duplicates on resume or repeat).
// Ends by saving (barrier) and, if sheets are requested, exporting the PDF. The postcondition lists fixtures with 0 or more than 1 tag.
// args: view (ElementId of the view), tag_type_name (tag type; optional: by category),
//       save (yes|no, default yes), pdf_sheets (list of sheet numbers, optional), pdf_folder, pdf_name.
//       The 0.9.4 names (vista, tipo_etiqueta, guardar=si|no, pdf_hojas, pdf_carpeta, pdf_nombre) are accepted until 0.9.5.
public class TagFixtures : Recipe
{
    public override string Version => "1";
    public override string Description => "One tag per fixture in a view; resumable without duplicates; saves and optionally exports PDF";
    public override IEnumerable<string> Inputs => new[] { "view" };

    private static View ViewOf(ScriptContext ctx) => ctx.E<View>(Convert.ToInt64(ctx.Args["view"])) ?? throw new ArgumentException("the view does not exist");

    private static int TagsOf(Document doc, View v, ElementId id)
    {
        var n = 0;
        foreach (IndependentTag t in new FilteredElementCollector(doc, v.Id).OfClass(typeof(IndependentTag)))
        {
            try { if (t.GetTaggedLocalElementIds().Contains(id)) n++; } catch (Exception) { }
        }
        return n;
    }

    public override void Prepare(ScriptContext ctx)
    {
        base.Prepare(ctx);
        var v = ViewOf(ctx);
        if (v.IsTemplate) throw new ArgumentException("the view is a template");
    }

    public override IEnumerable<Unit> Units(ScriptContext ctx)
    {
        var v = ViewOf(ctx);
        foreach (var e in new FilteredElementCollector(ctx.Doc, v.Id).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType()
                     .OrderBy(x => x.UniqueId, StringComparer.Ordinal))
            yield return new Unit("tag:" + v.UniqueId + ":" + e.UniqueId, new Dictionary<string, object> { ["uid"] = e.UniqueId }, new[] { ctx.Long(e.Id) });
        if (!string.Equals(Convert.ToString(ctx.Args.TryGetValue("save", out var g) ? g : "yes"), "no", StringComparison.OrdinalIgnoreCase))
            yield return Unit.Save();
        if (ctx.Args.TryGetValue("pdf_sheets", out var h) && h != null)
        {
            var o = new Dictionary<string, object> { ["sheets"] = h };
            if (ctx.Args.TryGetValue("pdf_folder", out var c)) o["folder"] = c;
            if (ctx.Args.TryGetValue("pdf_name", out var nm)) o["name"] = nm;
            o["overwrite"] = true;
            yield return Unit.Pdf("pdf", o);
        }
    }

    public override object Run(ScriptContext ctx, Unit u)
    {
        var v = ViewOf(ctx);
        var e = ctx.Doc.GetElement(Convert.ToString(u.D("uid")));
        if (e == null) ctx.Skip("the fixture no longer exists");
        if (TagsOf(ctx.Doc, v, e.Id) > 0) ctx.Skip("it already had a tag in the view");
        var p = (e.Location as LocationPoint)?.Point ?? (e.get_BoundingBox(v) is BoundingBoxXYZ bb ? (bb.Min + bb.Max) / 2 : null);
        if (p == null) { ctx.Exception("no insertion point", ctx.Long(e.Id)); ctx.Skip("no point"); }
        IndependentTag tag;
        if (ctx.Args.TryGetValue("tag_type_name", out var tt) && tt != null)
            tag = IndependentTag.Create(ctx.Doc, ctx.Id(Convert.ToInt64(tt)), v.Id, new Reference(e), false, TagOrientation.Horizontal, p);
        else
            tag = IndependentTag.Create(ctx.Doc, v.Id, new Reference(e), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, p);
        return new Dictionary<string, object> { ["light"] = ctx.Long(e.Id), ["tag"] = ctx.Long(tag.Id) };
    }

    public override Verification Verify(ScriptContext ctx, Unit u)
    {
        var e = ctx.Doc.GetElement(Convert.ToString(u.D("uid")));
        if (e == null) return Verification.Unknown;
        return TagsOf(ctx.Doc, ViewOf(ctx), e.Id) > 0 ? Verification.Applied : Verification.NotApplied;
    }

    public override object Postcondition(ScriptContext ctx)
    {
        var v = ViewOf(ctx);
        var count = new Dictionary<ElementId, int>();
        foreach (IndependentTag t in new FilteredElementCollector(ctx.Doc, v.Id).OfClass(typeof(IndependentTag)))
        {
            ICollection<ElementId> ids;
            try { ids = t.GetTaggedLocalElementIds(); } catch (Exception) { continue; }
            foreach (var id in ids) count[id] = count.TryGetValue(id, out var n) ? n + 1 : 1;
        }
        var fixtures = new FilteredElementCollector(ctx.Doc, v.Id).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType().ToElementIds();
        var untagged = fixtures.Where(i => !count.ContainsKey(i)).Select(i => (object)("untagged: " + ctx.Long(i)));
        var doubled = fixtures.Where(i => count.TryGetValue(i, out var n) && n > 1).Select(i => (object)("with " + count[i] + " tags: " + ctx.Long(i)));
        return new Dictionary<string, object> { ["lights_in_view"] = fixtures.Count, ["deviations"] = untagged.Concat(doubled).ToList() };
    }
}
