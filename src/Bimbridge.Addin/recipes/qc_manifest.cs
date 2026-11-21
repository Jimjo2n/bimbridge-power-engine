// SPDX-License-Identifier: GPL-3.0-only
// Recipe shipped with BIMBRIDGE - Power Engine (since 0.6.0). One-to-one QC of a manifest (expected list of positions, types, heights, tags)
// against the model elements. Read-only. Leaves a CSV with all deviations as a task artifact.
// args: manifiesto (JSON text, or path to a .json), categoria (BuiltInCategory, default OST_LightingFixtures),
// nivel (optional: only that level), vista_etiquetas (optional: id of the view where tags are counted).
// Manifest format: {"tolerancias":{"xy":150,"z":25,"giro":2},"elementos":[{"ref":"L-001","x":..,"y":..,"z":..,"tipos":["D"],"etiqueta":true}]}
public class QcManifest : Recipe
{
    public override string Version => "1";
    public override string Description => "One-to-one QC against a JSON manifest (position, height, rotation, type, level, tags); read-only";
    public override TxPolicy Transaction => TxPolicy.ReadOnly;
    public override IEnumerable<string> Inputs => new[] { "manifiesto" };

    private static string ManifestText(ScriptContext ctx)
    {
        var m = Convert.ToString(ctx.Args["manifiesto"]).Trim();
        return m.StartsWith("{") ? m : File.ReadAllText(Environment.ExpandEnvironmentVariables(m));
    }

    private static List<Element> Elements(ScriptContext ctx)
    {
        var cat = ctx.Args.TryGetValue("categoria", out var c) && c != null ? (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), Convert.ToString(c)) : BuiltInCategory.OST_LightingFixtures;
        var q = new FilteredElementCollector(ctx.Doc).OfCategory(cat).WhereElementIsNotElementType().ToElements();
        var level = ctx.Args.TryGetValue("nivel", out var n) ? Convert.ToString(n) : null;
        return q.Where(e => level == null || (ctx.Doc.GetElement(e.LevelId) as Level)?.Name == level).ToList();
    }

    public override IEnumerable<Unit> Units(ScriptContext ctx)
    {
        yield return new Unit("qc:" + Bimbridge.Engine.Home.Sha256Hex(ManifestText(ctx)).Substring(0, 16));
    }

    public override object Run(ScriptContext ctx, Unit u)
    {
        View view = ctx.Args.TryGetValue("vista_etiquetas", out var v) && v != null ? ctx.E<View>(Convert.ToInt64(v)) : null;
        var res = Bim.Manifest.Qc(ctx.Doc, ManifestText(ctx), Elements(ctx), view);
        var csv = new StringBuilder("codigo;ref;id;detalle\n");
        foreach (var f in res.Findings) csv.Append(string.Join(";", f.Row().Select(x => Convert.ToString(x).Replace(";", ","))) ).Append('\n');
        ctx.Artifact("qc_manifiesto.csv", csv.ToString());
        foreach (var f in res.Findings.Take(200)) ctx.Exception(f.Code, f.Ref + " " + f.Detail);
        ctx.Decision(res.Passed ? "manifest passed" : "manifest with deviations: see qc_manifiesto.csv");
        return res.ToJson(20);
    }
}
