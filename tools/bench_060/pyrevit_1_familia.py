# SPDX-License-Identifier: GPL-3.0-only
# -*- coding: utf-8 -*-
# Benchmark 0.6.0 · carga 1 (familia) · motor pyRevit. Equivalente exacto de mcp_1_familia.cs (mismas llamadas a la API).
# No escribe: abre el RFA, lee y lo cierra SIN guardar. Cambie FAMILIA si quiere otra (None = primer aparato sanitario editable).
import time, json
from Autodesk.Revit.DB import FilteredElementCollector, Family, BuiltInCategory, ElementId, ConnectorElement, Domain, ConnectorProfileType
FAMILIA = None
doc = __revit__.ActiveUIDocument.Document
t0 = time.time()
fams = [f for f in FilteredElementCollector(doc).OfClass(Family) if f.IsEditable and not f.IsInPlace and
        (f.Name == FAMILIA if FAMILIA else (f.FamilyCategory is not None and f.FamilyCategory.Id == ElementId(BuiltInCategory.OST_PlumbingFixtures)))]
fams.sort(key=lambda f: f.Name)
fam = fams[0]
fd = doc.EditFamily(fam)
n_params = n_form = n_types = 0
con = []
try:
    fm = fd.FamilyManager
    n_types = fm.Types.Size
    for p in fm.Parameters:
        n_params += 1
        if p.Formula: n_form += 1
    for c in FilteredElementCollector(fd).OfClass(ConnectorElement):
        con.append([str(c.Domain), str(c.SystemClassification) if c.Domain == Domain.DomainPiping else "", round(c.Radius * 304.8, 2) if c.Shape == ConnectorProfileType.Round else 0.0])
finally:
    fd.Close(False)
print(json.dumps({"carga": "familia", "motor": "pyrevit", "familia": fam.Name, "tipos": n_types, "parametros": n_params, "formulas": n_form,
                  "conectores": con, "ms": int((time.time() - t0) * 1000)}))
