# SPDX-License-Identifier: GPL-3.0-only
# -*- coding: utf-8 -*-
# Benchmark 0.6.0 · carga 2 (QC geométrico de luces) · motor pyRevit. Mismo algoritmo que Bim.Montaje.Soporte del MCP:
# cuerpo por sólidos (sin "Light Source"), rayo vertical desde 1 mm bajo la base, filtro categorías + vínculos, holgura con tolerancia.
import time, json
from Autodesk.Revit.DB import (FilteredElementCollector, BuiltInCategory, View3D, Level, Options, ViewDetailLevel, Solid, GeometryInstance,
    ElementMulticategoryFilter, ElementClassFilter, RevitLinkInstance, LogicalOrFilter, LogicalAndFilter, ExclusionFilter,
    ReferenceIntersector, FindReferenceTarget, XYZ, ElementId)
from System.Collections.Generic import List
NIVEL = None; TOL = 3.0; MAX = 3000.0
doc = __revit__.ActiveUIDocument.Document
FT = 304.8
def iid(x): return x.Value if hasattr(x, "Value") else x.IntegerValue   # 2026 quitó IntegerValue
t0 = time.time()
CATS = [BuiltInCategory.OST_Floors, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_Stairs, BuiltInCategory.OST_Ramps, BuiltInCategory.OST_GenericModel]
CAT_IDS = set(int(c) for c in CATS)
v3 = sorted([v for v in FilteredElementCollector(doc).OfClass(View3D) if not v.IsTemplate], key=lambda v: 1 if v.IsSectionBoxActive else 0)[0]
opt = Options(); opt.DetailLevel = ViewDetailLevel.Fine; opt.ComputeReferences = False; opt.IncludeNonVisibleObjects = False

def solids(ge, out, depth=0):
    if ge is None or depth > 8: return
    for o in ge:
        if isinstance(o, Solid):
            if o.Faces.Size == 0 or o.Volume < 1e-7: continue
            gs = doc.GetElement(o.GraphicsStyleId) if o.GraphicsStyleId != ElementId.InvalidElementId else None
            if gs is not None and gs.GraphicsStyleCategory is not None and gs.GraphicsStyleCategory.Name == "Light Source": continue
            out.append(o)
        elif isinstance(o, GeometryInstance):
            solids(o.GetInstanceGeometry(), out, depth + 1)

def caja(e):
    ss = []; solids(e.get_Geometry(opt), ss)
    pts = []
    for s in ss:
        bb = s.GetBoundingBox(); t = bb.Transform
        for x in (bb.Min.X, bb.Max.X):
            for y in (bb.Min.Y, bb.Max.Y):
                for z in (bb.Min.Z, bb.Max.Z): pts.append(t.OfPoint(XYZ(x, y, z)))
    if not pts:
        bb = e.get_BoundingBox(None)
        if bb is None: return None
        pts = [bb.Min, bb.Max]
    return (min(p.X for p in pts), min(p.Y for p in pts), min(p.Z for p in pts), max(p.X for p in pts), max(p.Y for p in pts), max(p.Z for p in pts))

cat_list = List[BuiltInCategory](CATS)
cuenta = {}; muestra = []
luces = [e for e in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType()
         if not NIVEL or (doc.GetElement(e.LevelId) is not None and doc.GetElement(e.LevelId).Name == NIVEL)]
for e in luces:
    b = caja(e)
    if b is None:
        clase = "sin_cuerpo"
    else:
        f = LogicalAndFilter(LogicalOrFilter(ElementMulticategoryFilter(cat_list), ElementClassFilter(RevitLinkInstance)), ExclusionFilter(List[ElementId]([e.Id])))
        ri = ReferenceIntersector(f, FindReferenceTarget.Face, v3); ri.FindReferencesInRevitLinks = True
        start = b[2] - 1 / FT; h = (b[5] - b[2]) + 1 / FT
        hits = sorted(ri.Find(XYZ((b[0] + b[3]) / 2, (b[1] + b[4]) / 2, start), XYZ.BasisZ), key=lambda x: x.Proximity)
        clase = "sin_referencia"; gap = None
        for hw in hits:
            if hw.Proximity * FT > MAX + h * FT: break
            r = hw.GetReference(); host = doc.GetElement(r.ElementId)
            if isinstance(host, RevitLinkInstance):
                ld = host.GetLinkDocument(); hit = ld.GetElement(r.LinkedElementId) if ld else None
                if hit is None or hit.Category is None or iid(hit.Category.Id) not in CAT_IDS: continue
            gap = (hw.Proximity - h) * FT
            clase = "al_ras" if abs(gap) <= TOL else ("empotrado" if gap < 0 else "separado")
            break
    cuenta[clase] = cuenta.get(clase, 0) + 1
    if clase != "al_ras" and len(muestra) < 5: muestra.append([iid(e.Id), clase, None if b is None or gap is None else round(gap, 1)])
print(json.dumps({"carga": "montaje", "motor": "pyrevit", "luces": len(luces), "cuenta": cuenta, "muestra": muestra, "ms": int((time.time() - t0) * 1000)}))
