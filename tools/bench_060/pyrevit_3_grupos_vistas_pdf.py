# SPDX-License-Identifier: GPL-3.0-only
# -*- coding: utf-8 -*-
# Benchmark 0.6.0 · carga 3 (grupos, vistas, láminas y exportación) · motor pyRevit. Equivalente de mcp_3_grupos_vistas_pdf.cs.
# No cambia el modelo; exporta un PDF a %TEMP%\pdi_bench. Nota: el guardián de pyRevit puede bloquear la exportación desde scripts;
# si la bloquea, anote "bloqueado" (es parte de la comparación, no un fallo del script).
import time, json, os, datetime
from Autodesk.Revit.DB import (FilteredElementCollector, Group, ViewPlan, ViewType, ViewSheet, PDFExportOptions, ElementId, FamilyInstance,
    PlanViewPlane, PlanViewRange, Level)
from System.Collections.Generic import List
HOJAS = 3
CARPETA = os.path.expandvars(r"%TEMP%\pdi_bench")
doc = __revit__.ActiveUIDocument.Document
t0 = time.time()
grupos = list(FilteredElementCollector(doc).OfClass(Group))
miembros = espejados = 0
for g in grupos:
    for i in g.GetMemberIds():
        m = doc.GetElement(i)
        if m is None or isinstance(m, Group): continue
        miembros += 1
        if isinstance(m, FamilyInstance) and m.Mirrored: espejados += 1
planos = [v for v in FilteredElementCollector(doc).OfClass(ViewPlan) if not v.IsTemplate and v.ViewType != ViewType.AreaPlan]
rangos = con_plantilla = 0
for v in planos:
    r = v.GetViewRange()
    for p in (PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane):
        lid = r.GetLevelId(p); off = r.GetOffset(p)
        lvl = doc.GetElement(lid) if lid not in (PlanViewRange.Unlimited, PlanViewRange.LevelAbove, PlanViewRange.Current) else None
        _ = None if lvl is None else (lvl.Elevation + off) * 304.8
    rangos += 1
    if v.ViewTemplateId != ElementId.InvalidElementId: con_plantilla += 1
hojas = sorted([s for s in FilteredElementCollector(doc).OfClass(ViewSheet) if not s.IsPlaceholder], key=lambda s: s.SheetNumber)
if not os.path.isdir(CARPETA): os.makedirs(CARPETA)
nombre = "pyrevit_" + datetime.datetime.now().strftime("%H%M%S%f")
opt = PDFExportOptions(); opt.FileName = nombre; opt.Combine = True
t1 = time.time()
ok = doc.Export(CARPETA, List[ElementId]([s.Id for s in hojas[:HOJAS]]), opt)
pdf = os.path.join(CARPETA, nombre + ".pdf")
print(json.dumps({"carga": "grupos_vistas_pdf", "motor": "pyrevit", "grupos": len(grupos), "miembros": miembros, "espejados": espejados,
                  "planos": len(planos), "rangos": rangos, "conPlantilla": con_plantilla, "hojas": len(hojas), "exportadas": min(HOJAS, len(hojas)),
                  "pdf_ok": bool(ok) and os.path.isfile(pdf), "pdf_bytes": os.path.getsize(pdf) if os.path.isfile(pdf) else 0,
                  "exportar_ms": int((time.time() - t1) * 1000), "ms": int((time.time() - t0) * 1000)}))
