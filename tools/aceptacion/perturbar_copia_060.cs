// SPDX-License-Identifier: GPL-3.0-only
// Aceptación 0.6.0 (D): introduce desviaciones CONOCIDAS en la COPIA de práctica (nunca en el modelo de producción).
// exec modo=ejecutar tx=auto args={"mover":<id>,"cambiar_tipo":<id>,"tipo_nuevo":<id tipo>,"borrar":<id>,"duplicar":<id>,"quitar_etiqueta_de":<id>,"vista":<id RCP>}
// Cada clave es opcional. Devuelve lo hecho para anotarlo; Ctrl+Z (un paso) lo deshace.
var hecho = new List<object>();
Func<string, Element> E = k => args.ContainsKey(k) ? ctx.E(Convert.ToInt64(args[k])) : null;
if (E("mover") is Element m) { ElementTransformUtils.MoveElement(doc, m.Id, ctx.MmXyz(300, 0, 0)); hecho.Add("movida 300 mm en X: " + ctx.Long(m.Id) + " → espera 'posicion'"); }
if (E("cambiar_tipo") is Element c && args.ContainsKey("tipo_nuevo")) { c.ChangeTypeId(ctx.Id(Convert.ToInt64(args["tipo_nuevo"]))); hecho.Add("tipo cambiado: " + ctx.Long(c.Id) + " → espera 'tipo'"); }
if (E("borrar") is Element b) { var id = ctx.Long(b.Id); doc.Delete(b.Id); hecho.Add("borrada: " + id + " → espera 'faltante'"); }
if (E("duplicar") is Element d) { var n = ElementTransformUtils.CopyElement(doc, d.Id, ctx.MmXyz(50, 0, 0)); hecho.Add("copiada a 50 mm: " + ctx.Long(n.First()) + " → espera 'casi_duplicado' o 'sobrante'"); }
if (E("quitar_etiqueta_de") is Element q && args.ContainsKey("vista"))
{
    var v = ctx.Id(Convert.ToInt64(args["vista"]));
    var tags = new FilteredElementCollector(doc, v).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().Where(t => t.GetTaggedLocalElementIds().Contains(q.Id)).Select(t => t.Id).ToList();
    if (tags.Count > 0) { doc.Delete(tags); hecho.Add("etiqueta quitada de " + ctx.Long(q.Id) + " → espera 'sin_etiqueta'"); }
}
return hecho;
