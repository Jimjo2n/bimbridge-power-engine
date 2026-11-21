# SPDX-License-Identifier: GPL-3.0-only
# -*- coding: utf-8 -*-
# Benchmark 0.6.0 · workload 1 (family) · pyRevit engine. Exact equivalent of mcp_1_family.cs (same API calls).
# Does not write: opens the RFA, reads it and closes it WITHOUT saving. Change FAMILY for another one (None = first editable plumbing fixture).
import time, json
from Autodesk.Revit.DB import FilteredElementCollector, Family, BuiltInCategory, ElementId, ConnectorElement, Domain, ConnectorProfileType
FAMILY = None
doc = __revit__.ActiveUIDocument.Document
t0 = time.time()
fams = [f for f in FilteredElementCollector(doc).OfClass(Family) if f.IsEditable and not f.IsInPlace and
        (f.Name == FAMILY if FAMILY else (f.FamilyCategory is not None and f.FamilyCategory.Id == ElementId(BuiltInCategory.OST_PlumbingFixtures)))]
fams.sort(key=lambda f: f.Name)
fam = fams[0]
fd = doc.EditFamily(fam)
n_params = n_form = n_types = 0
connectors = []
try:
    fm = fd.FamilyManager
    n_types = fm.Types.Size
    for p in fm.Parameters:
        n_params += 1
        if p.Formula: n_form += 1
    for c in FilteredElementCollector(fd).OfClass(ConnectorElement):
        connectors.append([str(c.Domain), str(c.SystemClassification) if c.Domain == Domain.DomainPiping else "", round(c.Radius * 304.8, 2) if c.Shape == ConnectorProfileType.Round else 0.0])
finally:
    fd.Close(False)
print(json.dumps({"carga": "familia", "motor": "pyrevit", "familia": fam.Name, "tipos": n_types, "parametros": n_params, "formulas": n_form,
                  "conectores": connectors, "ms": int((time.time() - t0) * 1000)}))
