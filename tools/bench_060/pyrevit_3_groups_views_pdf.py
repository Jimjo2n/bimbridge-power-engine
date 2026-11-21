# SPDX-License-Identifier: GPL-3.0-only
# -*- coding: utf-8 -*-
# Benchmark 0.6.0 · workload 3 (groups, views, sheets and export) · pyRevit engine. Equivalent of mcp_3_groups_views_pdf.cs.
# Does not change the model; exports a PDF to %TEMP%\bimbridge_bench. Note: the pyRevit guard may block export from scripts;
# if it does, record "blocked" (it is part of the comparison, not a script failure).
import time, json, os, datetime
from Autodesk.Revit.DB import (FilteredElementCollector, Group, ViewPlan, ViewType, ViewSheet, PDFExportOptions, ElementId, FamilyInstance,
    PlanViewPlane, PlanViewRange, Level)
from System.Collections.Generic import List
SHEETS = 3
FOLDER = os.path.expandvars(r"%TEMP%\bimbridge_bench")
doc = __revit__.ActiveUIDocument.Document
t0 = time.time()
groups = list(FilteredElementCollector(doc).OfClass(Group))
members = mirrored = 0
for g in groups:
    for i in g.GetMemberIds():
        m = doc.GetElement(i)
        if m is None or isinstance(m, Group): continue
        members += 1
        if isinstance(m, FamilyInstance) and m.Mirrored: mirrored += 1
plans = [v for v in FilteredElementCollector(doc).OfClass(ViewPlan) if not v.IsTemplate and v.ViewType != ViewType.AreaPlan]
ranges = with_template = 0
for v in plans:
    r = v.GetViewRange()
    for p in (PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane):
        lid = r.GetLevelId(p); off = r.GetOffset(p)
        lvl = doc.GetElement(lid) if lid not in (PlanViewRange.Unlimited, PlanViewRange.LevelAbove, PlanViewRange.Current) else None
        _ = None if lvl is None else (lvl.Elevation + off) * 304.8
    ranges += 1
    if v.ViewTemplateId != ElementId.InvalidElementId: with_template += 1
sheets = sorted([s for s in FilteredElementCollector(doc).OfClass(ViewSheet) if not s.IsPlaceholder], key=lambda s: s.SheetNumber)
if not os.path.isdir(FOLDER): os.makedirs(FOLDER)
name = "pyrevit_" + datetime.datetime.now().strftime("%H%M%S%f")
opt = PDFExportOptions(); opt.FileName = name; opt.Combine = True
t1 = time.time()
ok = doc.Export(FOLDER, List[ElementId]([s.Id for s in sheets[:SHEETS]]), opt)
pdf = os.path.join(FOLDER, name + ".pdf")
print(json.dumps({"carga": "grupos_vistas_pdf", "motor": "pyrevit", "grupos": len(groups), "miembros": members, "espejados": mirrored,
                  "planos": len(plans), "rangos": ranges, "conPlantilla": with_template, "hojas": len(sheets), "exportadas": min(SHEETS, len(sheets)),
                  "pdf_ok": bool(ok) and os.path.isfile(pdf), "pdf_bytes": os.path.getsize(pdf) if os.path.isfile(pdf) else 0,
                  "exportar_ms": int((time.time() - t1) * 1000), "ms": int((time.time() - t0) * 1000)}))
