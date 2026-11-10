// SPDX-License-Identifier: GPL-3.0-only
// Receta incluida en BIMBRIDGE - Power Engine (desde 0.6.0). QC uno a uno de un manifiesto (lista esperada de posiciones, tipos, alturas, etiquetas)
// contra los elementos del modelo. Sólo lectura. Deja un CSV con todas las desviaciones como artefacto de la tarea.
// args: manifiesto (JSON en texto, o ruta a un .json), categoria (BuiltInCategory, por defecto OST_LightingFixtures),
// nivel (opcional: sólo ese nivel), vista_etiquetas (opcional: id de la vista donde contar etiquetas).
// Formato del manifiesto: {"tolerancias":{"xy":150,"z":25,"giro":2},"elementos":[{"ref":"L-001","x":..,"y":..,"z":..,"tipos":["D"],"etiqueta":true}]}
public class QcManifiesto : Receta
{
    public override string Version => "1";
    public override string Descripcion => "QC uno a uno contra un manifiesto JSON (posición, altura, giro, tipo, nivel, etiquetas); sólo lectura";
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
        yield return new Unidad("qc:" + PDI.RevitMcp.Engine.Home.Sha256Hex(Texto(ctx)).Substring(0, 16));
    }

    public override object Ejecutar(ScriptContext ctx, Unidad u)
    {
        View vista = ctx.Args.TryGetValue("vista_etiquetas", out var v) && v != null ? ctx.E<View>(Convert.ToInt64(v)) : null;
        var res = Bim.Manifiesto.Qc(ctx.Doc, Texto(ctx), Elementos(ctx), vista);
        var csv = new StringBuilder("codigo;ref;id;detalle\n");
        foreach (var f in res.Findings) csv.Append(string.Join(";", f.Row().Select(x => Convert.ToString(x).Replace(";", ","))) ).Append('\n');
        ctx.Artefacto("qc_manifiesto.csv", csv.ToString());
        foreach (var f in res.Findings.Take(200)) ctx.Excepcion(f.Code, f.Ref + " " + f.Detail);
        ctx.Decision(res.Passed ? "manifiesto aprobado" : "manifiesto con desviaciones: ver qc_manifiesto.csv");
        return res.ToJson(20);
    }
}
