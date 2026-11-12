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
        private IList<FailureMessage> _rawWarnings;
        private int _nextWarning, _nextSheet, _nextView;
        private List<ElementId> _sheetIds, _viewIds;
        private readonly HashSet<long> _onSheet = new HashSet<long>();

        /// <summary>Límites para modelos grandes: lo que excede se informa como truncado, nunca se omite en silencio.</summary>
        public const int MaxWarnings = 20000, MaxElements = 150000, MaxSheets = 5000, MaxViews = 20000;
        public int WarningsTotal, ElementsTotal, SheetsTotal, ViewsTotal;
        public int Slices;

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
            // AUD-17: ninguna fase hace un trabajo proporcional al modelo sin mirar el presupuesto. Las llamadas indivisibles de Revit
            // (GetWarnings, un colector de ids) van solas en su tramo y se cede el hilo después; lo demás se trocea y se limita.
            if (_phase == 0)
            {
                Rev = tracker.For(doc)?.Rev ?? 0;
                _rawWarnings = doc.GetWarnings();          // una llamada de Revit; no se puede trocear
                WarningsTotal = _rawWarnings.Count;
                _phase = -1;
                return false;                              // ceder el hilo antes de procesarlos
            }
            if (_phase == -1)
            {
                for (; _nextWarning < _rawWarnings.Count && _nextWarning < MaxWarnings; _nextWarning++)
                {
                    if ((_nextWarning & 31) == 0 && sw.ElapsedMilliseconds >= deadline) return false;
                    var w = _rawWarnings[_nextWarning];
                    Warnings.Add(new ModelHealth.Warning { Description = w.GetDescriptionText(), Severity = w.GetSeverity().ToString(), Ids = w.GetFailingElements().Take(50).Select(Ids.ToLong).ToList() });
                }
                _rawWarnings = null;
                _phase = -2;
                return false;
            }
            if (_phase == -2)
            {
                var filter = new ElementMulticategoryFilter(_cats.ToList());
                _ids = new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType().ToElementIds().ToList();   // sólo ids: no expande elementos
                ElementsTotal = _ids.Count;
                if (_ids.Count > MaxElements) _ids.RemoveRange(MaxElements, _ids.Count - MaxElements);
                _phase = 1;
                return false;
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
                // Ids de láminas y vistas y tablas por lámina en UNA pasada (antes: un colector por lámina).
                _sheetIds = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).ToElementIds().ToList();
                _viewIds = new FilteredElementCollector(doc).OfClass(typeof(View)).ToElementIds().ToList();
                SheetsTotal = _sheetIds.Count; ViewsTotal = _viewIds.Count;
                if (_sheetIds.Count > MaxSheets) _sheetIds.RemoveRange(MaxSheets, _sheetIds.Count - MaxSheets);
                if (_viewIds.Count > MaxViews) _viewIds.RemoveRange(MaxViews, _viewIds.Count - MaxViews);
                foreach (var id in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).ToElementIds())
                {
                    var owner = Ids.ToLong(doc.GetElement(id).OwnerViewId);
                    _schedulesBySheet[owner] = (_schedulesBySheet.TryGetValue(owner, out var n) ? n : 0) + 1;
                }
                _phase = 3;
                return false;
            }
            if (_phase == 3)
            {
                for (; _nextSheet < _sheetIds.Count; _nextSheet++)
                {
                    if ((_nextSheet & 15) == 0 && sw.ElapsedMilliseconds >= deadline) return false;
                    var s = doc.GetElement(_sheetIds[_nextSheet]) as ViewSheet;
                    if (s == null || s.IsTemplate) continue;
                    var vps = s.GetAllPlacedViews();
                    foreach (var v in vps) _onSheet.Add(Ids.ToLong(v));
                    var sid = Ids.ToLong(s.Id);
                    Sheets.Add(new ModelHealth.Sheet { Id = sid, Number = s.SheetNumber, Name = s.Name, Viewports = vps.Count, Schedules = _schedulesBySheet.TryGetValue(sid, out var sc) ? sc : 0 });
                }
                _phase = 4;
                if (sw.ElapsedMilliseconds >= deadline) return false;
            }
            if (_phase == 4)
            {
                for (; _nextView < _viewIds.Count; _nextView++)
                {
                    if ((_nextView & 63) == 0 && sw.ElapsedMilliseconds >= deadline) return false;
                    var v = doc.GetElement(_viewIds[_nextView]) as View;
                    if (v == null || v is ViewSheet) continue;
                    Views.Add(new ModelHealth.View
                    {
                        Id = Ids.ToLong(v.Id), Name = v.Name, Kind = v.ViewType.ToString(), OnSheet = _onSheet.Contains(Ids.ToLong(v.Id)), IsTemplate = v.IsTemplate,
                        IsDependent = v.GetPrimaryViewId() != ElementId.InvalidElementId,
                    });
                }
                _phase = 5;
            }
            return true;
        }

        private readonly Dictionary<long, int> _schedulesBySheet = new Dictionary<long, int>();

        /// <summary>Lo que quedó fuera por los límites (vacío si se revisó todo).</summary>
        public Dictionary<string, object> Truncation()
        {
            var d = new Dictionary<string, object>();
            void T(string k, int total, int max) { if (total > max) d[k] = "revisados " + max + " de " + total; }
            T("avisos", WarningsTotal, MaxWarnings); T("elementos", ElementsTotal, MaxElements); T("laminas", SheetsTotal, MaxSheets); T("vistas", ViewsTotal, MaxViews);
            return d;
        }
    }
}
