// SPDX-License-Identifier: GPL-3.0-only
// Receta incluida en BIMBRIDGE - Power Engine (desde 0.6.0). Fija un parámetro de INSTANCIA a un valor en una lista de elementos, uno por unidad:
// reanudable, sin duplicar (si ya tiene el valor se omite) y con verificación tras una caída.
// args: ids (lista de ElementId) o conjunto ("$nombre" de un conjunto guardado), parametro (nombre visible), valor (texto; en
// parámetros de longitud se usa como en la interfaz, p. ej. "2400 mm"), guardar_al_final (si/no, por defecto no).
public class ParametroPorLote : Receta
{
    public override string Version => "1";
    public override string Descripcion => "Fija un parámetro de instancia en una lista de elementos (una unidad por elemento, verificable)";
    public override IEnumerable<string> Entradas => new[] { "parametro", "valor" };

    public override void Preparar(ScriptContext ctx)
    {
        base.Preparar(ctx);
        if (!ctx.Args.ContainsKey("ids") && !ctx.Args.ContainsKey("conjunto")) throw new ArgumentException("faltan ids o conjunto");
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
            if (e == null) { ctx.Excepcion("no existe el elemento", id); continue; }
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
        if (p == null) { ctx.Excepcion("el elemento no tiene el parámetro", ctx.Long(e.Id)); ctx.Omitir("sin parámetro"); }
        if (p.IsReadOnly) { ctx.Excepcion("parámetro de solo lectura", ctx.Long(e.Id)); ctx.Omitir("solo lectura"); }
        if (Igual(p, valor)) ctx.Omitir("ya tenía el valor");
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
        if (!ok) throw new InvalidOperationException("Revit no aceptó el valor '" + valor + "'");
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
        return new Dictionary<string, object> { ["revisados"] = Ids(ctx).Count(), ["desviaciones"] = mal.Select(i => (object)("sin el valor: " + i)).ToList() };
    }
}
