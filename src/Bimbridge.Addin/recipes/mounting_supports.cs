// SPDX-License-Identifier: GPL-3.0-only
// Recipe shipped with BIMBRIDGE - Power Engine (since 0.6.0). Classifies the mounting of each element against its real support (also in links):
// al_ras, empotrado, separado or sin_referencia, with the clearance in mm. Read-only: moves nothing.
// args: categoria (default OST_LightingFixtures), nivel (optional), direccion (arriba|abajo, default arriba),
// tol_mm (3), max_mm (3000), lote (elements per unit, 50).
public class MontajeSoportes : Receta
{
    public override string Version => "1";
    public override string Descripcion => "Classifies mounting against the real support (vertical ray, includes links); read-only";
    public override PoliticaTx Transaccion => PoliticaTx.Lectura;

    private static string A(ScriptContext ctx, string k, string def) => ctx.Args.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : def;

    public override IEnumerable<Unidad> Unidades(ScriptContext ctx)
    {
        var cat = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), A(ctx, "categoria", "OST_LightingFixtures"));
        var nivel = A(ctx, "nivel", null);
        var lote = int.Parse(A(ctx, "lote", "50"));
        var els = new FilteredElementCollector(ctx.Doc).OfCategory(cat).WhereElementIsNotElementType()
            .Where(e => nivel == null || (ctx.Doc.GetElement(e.LevelId) as Level)?.Name == nivel)
            .OrderBy(e => e.UniqueId, StringComparer.Ordinal).ToList();
        for (var i = 0; i < els.Count; i += lote)
        {
            var part = els.Skip(i).Take(lote).ToList();
            // Stable key: first and last UniqueId of the chunk (same model → same keys).
            yield return new Unidad("tramo:" + part[0].UniqueId + ".." + part[part.Count - 1].UniqueId,
                new Dictionary<string, object> { ["uids"] = part.Select(e => (object)e.UniqueId).ToList() }, part.Select(e => ctx.Long(e.Id)));
        }
    }

    public override object Ejecutar(ScriptContext ctx, Unidad u)
    {
        var view = Bim.Montaje.Vista3D(ctx.Doc);
        var tol = double.Parse(A(ctx, "tol_mm", "3"), System.Globalization.CultureInfo.InvariantCulture);
        var max = double.Parse(A(ctx, "max_mm", "3000"), System.Globalization.CultureInfo.InvariantCulture);
        var dir = A(ctx, "direccion", "arriba");
        var cuenta = new Dictionary<string, int>();
        foreach (var uid in (IEnumerable<object>)u.D("uids"))
        {
            ctx.ThrowIfCancelled();
            var e = ctx.Doc.GetElement(Convert.ToString(uid));
            if (e == null) continue;
            var r = Bim.Montaje.Soporte(e, view, dir, max, tol);
            var clase = Convert.ToString(r["clase"]);
            cuenta[clase] = cuenta.TryGetValue(clase, out var n) ? n + 1 : 1;
            if (clase != "al_ras") ctx.Excepcion(clase, r);
        }
        return cuenta;
    }
}
