// SPDX-License-Identifier: GPL-3.0-only
// Recipe shipped with BIMBRIDGE - Power Engine (since 0.6.0). One-to-one QC of a manifest (expected list of positions, types, heights, tags)
// against the model elements. Read-only. Leaves a CSV with all deviations as a task artifact.
// args: manifiesto (JSON text, or path to a .json), categoria (BuiltInCategory, default OST_LightingFixtures),
// nivel (optional: only that level), vista_etiquetas (optional: id of the view where tags are counted).
// Manifest format: {"tolerancias":{"xy":150,"z":25,"giro":2},"elementos":[{"ref":"L-001","x":..,"y":..,"z":..,"tipos":["D"],"etiqueta":true}]}
public class QcManifiesto : Receta
{
    public override string Version => "1";
    public override string Descripcion => "One-to-one QC against a JSON manifest (position, height, rotation, type, level, tags); read-only";
    public override PoliticaTx Transaccion => PoliticaTx.Lectura;
    public override IEnumerable<string> Entradas => new[] { "manifiesto" };

    private static string Texto(ScriptContext ctx)
    {
        var m = Convert.ToString(ctx.Args["manifiesto"]).Trim();
        return m.StartsWith("{") ? m : File.ReadAllText(Environment.ExpandEnvironmentVariables(m));
    }

    private static List<Element> Elementos(ScriptContext ctx)
    {
        var cat = ctx.Args.TryGetValue("categoria", out var c) && c != null ? (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), Convert.ToString(c)) : BuiltInCategory.OST_LightingFixtures;
        var q = new FilteredElementCollector(ctx.Doc).OfCategory(cat).WhereElementIsNotElementType().ToElements();
        var nivel = ctx.Args.TryGetValue("nivel", out var n) ? Convert.ToString(n) : null;
        return q.Where(e => nivel == null || (ctx.Doc.GetElement(e.LevelId) as Level)?.Name == nivel).ToList();
    }

    public override IEnumerable<Unidad> Unidades(ScriptContext ctx)
    {
        yield return new Unidad("qc:" + Bimbridge.Engine.Home.Sha256Hex(Texto(ctx)).Substring(0, 16));
    }

    public override object Ejecutar(ScriptContext ctx, Unidad u)
    {
        View vista = ctx.Args.TryGetValue("vista_etiquetas", out var v) && v != null ? ctx.E<View>(Convert.ToInt64(v)) : null;
        var res = Bim.Manifiesto.Qc(ctx.Doc, Texto(ctx), Elementos(ctx), vista);
        var csv = new StringBuilder("codigo;ref;id;detalle\n");
        foreach (var f in res.Findings) csv.Append(string.Join(";", f.Row().Select(x => Convert.ToString(x).Replace(";", ","))) ).Append('\n');
        ctx.Artefacto("qc_manifiesto.csv", csv.ToString());
        foreach (var f in res.Findings.Take(200)) ctx.Excepcion(f.Code, f.Ref + " " + f.Detail);
        ctx.Decision(res.Passed ? "manifest passed" : "manifest with deviations: see qc_manifiesto.csv");
        return res.ToJson(20);
    }
}
