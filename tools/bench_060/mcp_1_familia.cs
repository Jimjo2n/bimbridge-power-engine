// SPDX-License-Identifier: GPL-3.0-only
// Benchmark 0.6.0 · carga 1 (familia) · motor MCP. Equivalente exacto: pyrevit_1_familia.py.
// exec modo=ejecutar tx=ninguna args={"familia":"<nombre>"}  — no escribe: abre el RFA, lee y lo cierra SIN guardar.
var sw = System.Diagnostics.Stopwatch.StartNew();
var nombre = args.ContainsKey("familia") ? Convert.ToString(args["familia"]) : null;
var fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
    .Where(f => f.IsEditable && !f.IsInPlace && (nombre == null ? f.FamilyCategory != null && f.FamilyCategory.Id.Equals(new ElementId(BuiltInCategory.OST_PlumbingFixtures)) : f.Name == nombre))
    .OrderBy(f => f.Name).FirstOrDefault();
if (fam == null) throw new ArgumentException("no hay familia editable con ese nombre");
var fd = doc.EditFamily(fam);
int nParams = 0, nFormulas = 0, nTypes = 0; var conectores = new List<object>();
try
{
    var fm = fd.FamilyManager;
    nTypes = fm.Types.Size;
    foreach (FamilyParameter p in fm.Parameters) { nParams++; if (!string.IsNullOrEmpty(p.Formula)) nFormulas++; }
    foreach (ConnectorElement c in new FilteredElementCollector(fd).OfClass(typeof(ConnectorElement)))
        conectores.Add(new object[] { c.Domain.ToString(), c.Domain == Domain.DomainPiping ? c.SystemClassification.ToString() : "", c.Shape == ConnectorProfileType.Round ? Math.Round(c.Radius * 304.8, 2) : 0.0 });
}
finally { fd.Close(false); }
return new { carga = "familia", motor = "mcp", familia = fam.Name, tipos = nTypes, parametros = nParams, formulas = nFormulas, conectores, ms = sw.ElapsedMilliseconds };
