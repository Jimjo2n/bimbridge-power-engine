// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Engine.Qa;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// 0.9.1 (Bloque H): lectura de SOLO LECTURA para check name=model_health. Avisos del documento, elementos de las categorías
    /// pedidas (posición, tipo, parámetros requeridos), conectores MEP, láminas y vistas. Por tramos para no bloquear el hilo de Revit
    /// más que el presupuesto. Las reglas se aplican fuera, en Engine.Qa.ModelHealth.
    /// </summary>
    public sealed class ModelHealthProbe
    {
        private readonly List<BuiltInCategory> _cats;
        private readonly List<string> _required;
        private int _phase;
        private List<ElementId> _ids;
        private int _next;

        public readonly List<ModelHealth.Warning> Warnings = new List<ModelHealth.Warning>();
        public readonly List<ModelHealth.Element> Elements = new List<ModelHealth.Element>();
        public readonly List<ModelHealth.Connector> Connectors = new List<ModelHealth.Connector>();
        public readonly List<ModelHealth.Sheet> Sheets = new List<ModelHealth.Sheet>();
        public readonly List<ModelHealth.View> Views = new List<ModelHealth.View>();
        public string Error;
        public long Rev;

        public static readonly BuiltInCategory[] DefaultCategories =
        {
            BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_LightingDevices,
        };

        public ModelHealthProbe(IEnumerable<string> categories, IEnumerable<string> required, out string error)
        {
            error = null;
            _required = (required ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            var names = (categories ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            _cats = new List<BuiltInCategory>();
            foreach (var n in names)
            {
                if (!QueryRunner.TryCategory(n, out var bic)) { error = "no existe la categoría " + n; return; }
                _cats.Add(bic);
            }
            if (_cats.Count == 0) _cats.AddRange(DefaultCategories);
        }

        public bool Step(UIApplication app, Stopwatch sw, int budgetMs, DocumentTracker tracker)
        {
            var doc = DocTarget.Resolve(app, out var docError);
            if (doc == null) { Error = docError; return true; }
            if (doc.IsFamilyDocument) { Error = Status.NoSoportado + ": model_health revisa proyectos"; return true; }
            var deadline = sw.ElapsedMilliseconds + budgetMs;
            if (_phase == 0)
            {
                Rev = tracker.For(doc)?.Rev ?? 0;
                foreach (var w in doc.GetWarnings())
                    Warnings.Add(new ModelHealth.Warning { Description = w.GetDescriptionText(), Severity = w.GetSeverity().ToString(), Ids = w.GetFailingElements().Select(Ids.ToLong).ToList() });
                var filter = new ElementMulticategoryFilter(_cats.ToList());
                _ids = new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType().ToElementIds().ToList();
                _phase = 1;
                if (sw.ElapsedMilliseconds >= deadline) return false;
            }
            if (_phase == 1)
            {
                for (; _next < _ids.Count; _next++)
                {
                    if ((_next & 63) == 0 && sw.ElapsedMilliseconds >= deadline) return false;
                    var e = doc.GetElement(_ids[_next]);
                    if (e == null) continue;
                    var fi = e as FamilyInstance;
                    var row = new ModelHealth.Element
                    {
                        Id = Ids.ToLong(e.Id), Category = e.Category?.Name, Family = fi?.Symbol?.FamilyName ?? (doc.GetElement(e.GetTypeId()) as ElementType)?.FamilyName,
                        Type = fi?.Symbol?.Name ?? doc.GetElement(e.GetTypeId())?.Name, Level = (doc.GetElement(e.LevelId) as Level)?.Name,
                    };
                    if (e.Location is LocationPoint lp) { row.X = lp.Point.X; row.Y = lp.Point.Y; row.Z = lp.Point.Z; row.RotationDeg = lp.Rotation * 180 / Math.PI; }
                    else if (e.Location is LocationCurve lc && lc.Curve.IsBound) { var m = lc.Curve.Evaluate(0.5, true); row.X = m.X; row.Y = m.Y; row.Z = m.Z; }
                    else row.HasLocation = false;
                    foreach (var p in _required)
                    {
                        var prm = e.LookupParameter(p) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(p);
                        if (prm != null) row.Params[p] = prm.HasValue ? (prm.StorageType == StorageType.String ? prm.AsString() : prm.AsValueString()) ?? "" : "";
                    }
                    Elements.Add(row);
                    var cm = fi?.MEPModel?.ConnectorManager ?? (e as MEPCurve)?.ConnectorManager;
                    if (cm != null)
                        foreach (Connector c in cm.Connectors)
                        {
                            if (c.ConnectorType == ConnectorType.Logical) continue;   // conectores lógicos (eléctricos de familia) no se «conectan» físicamente
                            Connectors.Add(new ModelHealth.Connector { ElementId = row.Id, Domain = c.Domain.ToString(), Connected = c.IsConnected, Category = row.Category });
                        }
                }
                _phase = 2;
                if (sw.ElapsedMilliseconds >= deadline) return false;
            }
            if (_phase == 2)
            {
                var onSheet = new HashSet<long>();
                foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                {
                    if (s.IsTemplate) continue;
                    var vps = s.GetAllPlacedViews();
                    foreach (var v in vps) onSheet.Add(Ids.ToLong(v));
                    var sched = new FilteredElementCollector(doc, s.Id).OfClass(typeof(ScheduleSheetInstance)).GetElementCount();
                    Sheets.Add(new ModelHealth.Sheet { Id = Ids.ToLong(s.Id), Number = s.SheetNumber, Name = s.Name, Viewports = vps.Count, Schedules = sched });
                }
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    if (v is ViewSheet) continue;
                    Views.Add(new ModelHealth.View
                    {
                        Id = Ids.ToLong(v.Id), Name = v.Name, Kind = v.ViewType.ToString(), OnSheet = onSheet.Contains(Ids.ToLong(v.Id)), IsTemplate = v.IsTemplate,
                        IsDependent = v.GetPrimaryViewId() != ElementId.InvalidElementId,
                    });
                }
                _phase = 3;
            }
            return true;
        }
    }
}
