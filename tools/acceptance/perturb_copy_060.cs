// SPDX-License-Identifier: GPL-3.0-only
// Acceptance 0.6.0 (D): introduces KNOWN deviations in the practice COPY (never in the production model).
// exec modo=ejecutar tx=auto args={"mover":<id>,"cambiar_tipo":<id>,"tipo_nuevo":<type id>,"borrar":<id>,"duplicar":<id>,"quitar_etiqueta_de":<id>,"vista":<RCP id>}
// Each key is optional. Returns what was done so it can be noted; Ctrl+Z (one step) undoes it.
var done = new List<object>();
Func<string, Element> E = k => args.ContainsKey(k) ? ctx.E(Convert.ToInt64(args[k])) : null;
if (E("mover") is Element m) { ElementTransformUtils.MoveElement(doc, m.Id, ctx.MmXyz(300, 0, 0)); done.Add("moved 300 mm in X: " + ctx.Long(m.Id) + " → expect 'posicion'"); }
if (E("cambiar_tipo") is Element c && args.ContainsKey("tipo_nuevo")) { c.ChangeTypeId(ctx.Id(Convert.ToInt64(args["tipo_nuevo"]))); done.Add("type changed: " + ctx.Long(c.Id) + " → expect 'tipo'"); }
if (E("borrar") is Element b) { var id = ctx.Long(b.Id); doc.Delete(b.Id); done.Add("deleted: " + id + " → expect 'faltante'"); }
if (E("duplicar") is Element d) { var n = ElementTransformUtils.CopyElement(doc, d.Id, ctx.MmXyz(50, 0, 0)); done.Add("copied at 50 mm: " + ctx.Long(n.First()) + " → expect 'casi_duplicado' or 'sobrante'"); }
if (E("quitar_etiqueta_de") is Element q && args.ContainsKey("vista"))
{
    var v = ctx.Id(Convert.ToInt64(args["vista"]));
    var tags = new FilteredElementCollector(doc, v).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().Where(t => t.GetTaggedLocalElementIds().Contains(q.Id)).Select(t => t.Id).ToList();
    if (tags.Count > 0) { doc.Delete(tags); done.Add("tag removed from " + ctx.Long(q.Id) + " → expect 'sin_etiqueta'"); }
}
return done;
