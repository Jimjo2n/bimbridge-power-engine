// SPDX-License-Identifier: GPL-3.0-only
// Recipe shipped with BIMBRIDGE - Power Engine (since 0.6.0). Sets an INSTANCE parameter to a value on a list of elements, one per unit:
// resumable, without duplicates (skipped if it already has the value) and with verification after a crash.
// args: ids (list of ElementId) or conjunto ("$name" of a saved set), parametro (visible name), valor (text; for
// length parameters it is used as in the UI, e.g. "2400 mm"), guardar_al_final (si/no, default no).
public class ParametroPorLote : Receta
{
    public override string Version => "1";
    public override string Descripcion => "Sets an instance parameter on a list of elements (one unit per element, verifiable)";
    public override IEnumerable<string> Entradas => new[] { "parametro", "valor" };

    public override void Preparar(ScriptContext ctx)
    {
        base.Preparar(ctx);
        if (!ctx.Args.ContainsKey("ids") && !ctx.Args.ContainsKey("conjunto")) throw new ArgumentException("missing ids or conjunto");
    }

    private static IEnumerable<long> Ids(ScriptContext ctx)
    {
        if (ctx.Args.TryGetValue("conjunto", out var c) && c != null) return ctx.Set(Convert.ToString(c).TrimStart('$'));
        return ((IEnumerable<object>)ctx.Args["ids"]).Select(o => Convert.ToInt64(o));
    }

    public override IEnumerable<Unidad> Unidades(ScriptContext ctx)
    {
        foreach (var id in Ids(ctx).Distinct().OrderBy(x => x))
        {
            var e = ctx.E(id);
            if (e == null) { ctx.Excepcion("the element does not exist", id); continue; }
            yield return new Unidad("e:" + e.UniqueId, new Dictionary<string, object> { ["id"] = id }, new[] { id });
        }
        if (string.Equals(Convert.ToString(ctx.Args.TryGetValue("guardar_al_final", out var g) ? g : "no"), "si", StringComparison.OrdinalIgnoreCase))
            yield return Unidad.Guardar();
    }

    private static Parameter P(ScriptContext ctx, Element e) => e.LookupParameter(Convert.ToString(ctx.Args["parametro"]));

    private static bool Igual(Parameter p, string valor)
    {
        if (p.StorageType == StorageType.String) return (p.AsString() ?? "") == valor;
        var shown = p.AsValueString() ?? "";
        return shown == valor || (p.StorageType == StorageType.Integer && p.AsInteger().ToString() == valor);
    }

    public override object Ejecutar(ScriptContext ctx, Unidad u)
    {
        var e = ctx.E(Convert.ToInt64(u.D("id")));
        var valor = Convert.ToString(ctx.Args["valor"]);
        var p = P(ctx, e);
        if (p == null) { ctx.Excepcion("the element does not have the parameter", ctx.Long(e.Id)); ctx.Omitir("no parameter"); }
        if (p.IsReadOnly) { ctx.Excepcion("read-only parameter", ctx.Long(e.Id)); ctx.Omitir("read-only"); }
        if (Igual(p, valor)) ctx.Omitir("it already had the value");
        var antes = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
        bool ok;
        switch (p.StorageType)
        {
            case StorageType.String: ok = p.Set(valor); break;
            case StorageType.Integer: ok = int.TryParse(valor, out var i) ? p.Set(i) : p.SetValueString(valor); break;
            case StorageType.Double: ok = p.SetValueString(valor); break;
            case StorageType.ElementId: ok = p.Set(ctx.Id(long.Parse(valor))); break;
            default: ok = false; break;
        }
        if (!ok) throw new InvalidOperationException("Revit did not accept the value '" + valor + "'");
        return new Dictionary<string, object> { ["id"] = ctx.Long(e.Id), ["antes"] = antes };
    }

    public override Verificacion Verificar(ScriptContext ctx, Unidad u)
    {
        var e = ctx.E(Convert.ToInt64(u.D("id")));
        var p = e == null ? null : P(ctx, e);
        if (p == null) return Verificacion.Desconocida;
        return Igual(p, Convert.ToString(ctx.Args["valor"])) ? Verificacion.Aplicada : Verificacion.NoAplicada;
    }

    public override object Postcondicion(ScriptContext ctx)
    {
        var valor = Convert.ToString(ctx.Args["valor"]);
        var mal = Ids(ctx).Select(i => ctx.E(i)).Where(e => e != null).Where(e => { var p = P(ctx, e); return p != null && !p.IsReadOnly && !Igual(p, valor); })
            .Select(e => ctx.Long(e.Id)).ToList();
        return new Dictionary<string, object> { ["revisados"] = Ids(ctx).Count(), ["desviaciones"] = mal.Select(i => (object)("missing the value: " + i)).ToList() };
    }
}
