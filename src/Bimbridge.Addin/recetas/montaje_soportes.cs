// SPDX-License-Identifier: GPL-3.0-only
// Receta incluida en BIMBRIDGE - Power Engine (desde 0.6.0). Clasifica el montaje de cada elemento contra su soporte real (también en vínculos):
// al_ras, empotrado, separado o sin_referencia, con la holgura en mm. Sólo lectura: no mueve nada.
// args: categoria (por defecto OST_LightingFixtures), nivel (opcional), direccion (arriba|abajo, por defecto arriba),
// tol_mm (3), max_mm (3000), lote (elementos por unidad, 50).
public class MontajeSoportes : Receta
{
    public override string Version => "1";
    public override string Descripcion => "Clasifica el montaje contra el soporte real (rayo vertical, incluye vínculos); sólo lectura";
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
            // Clave estable: primer y último UniqueId del tramo (mismo modelo → mismas claves).
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
