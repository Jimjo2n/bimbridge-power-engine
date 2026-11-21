// SPDX-License-Identifier: GPL-3.0-only
// Recipe shipped with BIMBRIDGE - Power Engine (since 0.6.0). Sets an INSTANCE parameter to a value on a list of elements, one per unit:
// resumable, without duplicates (skipped if it already has the value) and with verification after a crash.
// args: ids (list of ElementId) or conjunto ("$name" of a saved set), parametro (visible name), valor (text; for
// length parameters it is used as in the UI, e.g. "2400 mm"), guardar_al_final (si/no, default no).
public class BatchParameter : Recipe
{
    public override string Version => "1";
    public override string Description => "Sets an instance parameter on a list of elements (one unit per element, verifiable)";
    public override IEnumerable<string> Inputs => new[] { "parametro", "valor" };

    public override void Prepare(ScriptContext ctx)
    {
        base.Prepare(ctx);
        if (!ctx.Args.ContainsKey("ids") && !ctx.Args.ContainsKey("conjunto")) throw new ArgumentException("missing ids or conjunto");
    }

    private static IEnumerable<long> Ids(ScriptContext ctx)
    {
        if (ctx.Args.TryGetValue("conjunto", out var c) && c != null) return ctx.Set(Convert.ToString(c).TrimStart('$'));
        return ((IEnumerable<object>)ctx.Args["ids"]).Select(o => Convert.ToInt64(o));
    }

    public override IEnumerable<Unit> Units(ScriptContext ctx)
    {
        foreach (var id in Ids(ctx).Distinct().OrderBy(x => x))
        {
            var e = ctx.E(id);
            if (e == null) { ctx.Exception("the element does not exist", id); continue; }
            yield return new Unit("e:" + e.UniqueId, new Dictionary<string, object> { ["id"] = id }, new[] { id });
        }
        if (string.Equals(Convert.ToString(ctx.Args.TryGetValue("guardar_al_final", out var g) ? g : "no"), "si", StringComparison.OrdinalIgnoreCase))
            yield return Unit.Save();
    }

    private static Parameter P(ScriptContext ctx, Element e) => e.LookupParameter(Convert.ToString(ctx.Args["parametro"]));

    private static bool HasValue(Parameter p, string value)
    {
        if (p.StorageType == StorageType.String) return (p.AsString() ?? "") == value;
        var shown = p.AsValueString() ?? "";
        return shown == value || (p.StorageType == StorageType.Integer && p.AsInteger().ToString() == value);
    }

    public override object Run(ScriptContext ctx, Unit u)
    {
        var e = ctx.E(Convert.ToInt64(u.D("id")));
        var value = Convert.ToString(ctx.Args["valor"]);
        var p = P(ctx, e);
        if (p == null) { ctx.Exception("the element does not have the parameter", ctx.Long(e.Id)); ctx.Skip("no parameter"); }
        if (p.IsReadOnly) { ctx.Exception("read-only parameter", ctx.Long(e.Id)); ctx.Skip("read-only"); }
        if (HasValue(p, value)) ctx.Skip("it already had the value");
        var before = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
        bool ok;
        switch (p.StorageType)
        {
            case StorageType.String: ok = p.Set(value); break;
            case StorageType.Integer: ok = int.TryParse(value, out var i) ? p.Set(i) : p.SetValueString(value); break;
            case StorageType.Double: ok = p.SetValueString(value); break;
            case StorageType.ElementId: ok = p.Set(ctx.Id(long.Parse(value))); break;
            default: ok = false; break;
        }
        if (!ok) throw new InvalidOperationException("Revit did not accept the value '" + value + "'");
        return new Dictionary<string, object> { ["id"] = ctx.Long(e.Id), ["antes"] = before };
    }

    public override Verification Verify(ScriptContext ctx, Unit u)
    {
        var e = ctx.E(Convert.ToInt64(u.D("id")));
        var p = e == null ? null : P(ctx, e);
        if (p == null) return Verification.Unknown;
        return HasValue(p, Convert.ToString(ctx.Args["valor"])) ? Verification.Applied : Verification.NotApplied;
    }

    public override object Postcondition(ScriptContext ctx)
    {
        var value = Convert.ToString(ctx.Args["valor"]);
        var wrong = Ids(ctx).Select(i => ctx.E(i)).Where(e => e != null).Where(e => { var p = P(ctx, e); return p != null && !p.IsReadOnly && !HasValue(p, value); })
            .Select(e => ctx.Long(e.Id)).ToList();
        return new Dictionary<string, object> { ["revisados"] = Ids(ctx).Count(), ["desviaciones"] = wrong.Select(i => (object)("missing the value: " + i)).ToList() };
    }
}
