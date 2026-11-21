// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// Support tools (0.3.0): model catalog, full parameters, show on screen and view capture.
    /// All are reads; show only changes the user's selection and zoom; capture reverts what it touches within the same slice.
    /// </summary>
    public sealed partial class RequestHandler
    {
        private TimeSpan Busy => TimeSpan.FromMilliseconds(_config.BusyAfterMs);

        /// <summary>ids: list, "1,2", $set or a mix. No duplicates, in the given order.</summary>
        private static List<long> ResolveIds(Session session, IDictionary<string, object> args, string key, out string error)
        {
            error = null;
            var list = new List<long>();
            var seen = new HashSet<long>();
            foreach (var raw in Arg.GetStringList(args, key))
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;
                if (s.StartsWith("$", StringComparison.Ordinal))
                {
                    long[] ids;
                    try
                    {
                        if (!session.Sets.TryGet(NamedSets.Normalize(s), TargetId, out ids, out var setError)) { error = setError; return null; }
                    }
                    catch (ArgumentException ex) { error = ex.Message; return null; }
                    foreach (var id in ids) if (seen.Add(id)) list.Add(id);
                    continue;
                }
                if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var one)) { error = key + ": '" + s + "' is neither an id nor a set ($name)"; return null; }
                if (seen.Add(one)) list.Add(one);
            }
            return list;
        }

        // ---------------- catalog ----------------

        private async Task<Response> Catalog(Request request, CancellationToken ct)
        {
            var runner = new CatalogRunner(request.Args);
            if (runner.Error != null) return Response.Fail(request.Id, Status.Invalid, runner.Error);
            var sw = Stopwatch.StartNew();
            var r = await RunMain((app, watch, budget) => runner.Step(app, watch, budget, _tracker), highPriority: true, busyAfter: Busy, ct: ct).ConfigureAwait(false);
            if (!r.Ok) return FromWork(request.Id, r);
            if (runner.Error != null) return Response.Fail(request.Id, Status.Invalid, runner.Error);
            var data = runner.Finish();
            data["ms"] = sw.ElapsedMilliseconds;
            return new Response { Id = request.Id, Rev = runner.Rev, Data = data };
        }

        // ---------------- params (get f=*) ----------------

        private const int MaxParamElements = 50;

        private async Task<Response> Params(Session session, Request request, CancellationToken ct)
        {
            var ids = ResolveIds(session, request.Args, "ids", out var err);
            if (err != null) return Response.Fail(request.Id, Status.Invalid, err);
            if (ids.Count == 0) return Response.Fail(request.Id, Status.Invalid, "ids: specify up to " + MaxParamElements + " elements");
            var omitted = Math.Max(0, ids.Count - MaxParamElements);
            if (omitted > 0) ids = ids.Take(MaxParamElements).ToList();
            var search = Arg.GetString(request.Args, "search");
            var withType = Arg.GetBool(request.Args, "type") ?? true;
            var mm = string.Equals(Arg.GetString(request.Args, "units"), "mm", StringComparison.OrdinalIgnoreCase);
            var full = Arg.GetBool(request.Args, "full") ?? false;

            bool Wanted(string name) => string.IsNullOrWhiteSpace(search) || name.IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;

            var elems = new List<Dictionary<string, object>>();
            var inst = new List<Dictionary<string, object>>();
            var types = new Dictionary<string, object>();
            var readOnly = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var readOnlyType = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var empty = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<object>();
            string paramsDocError = null;
            long rev = 0;
            var sw = Stopwatch.StartNew();

            Dictionary<string, object> Read(Document doc, Element e, SortedSet<string> ro)
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                IList<Parameter> ps;
                try { ps = e.GetOrderedParameters(); } catch (Exception) { ps = e.Parameters.Cast<Parameter>().ToList(); }
                foreach (var p in ps)
                {
                    string name;
                    try { name = p.Definition?.Name; } catch (Exception) { continue; }
                    if (string.IsNullOrEmpty(name) || !Wanted(name)) continue;
                    object v;
                    try { v = ElementFields.ParamValue(doc, p, mm); } catch (Exception) { v = null; }
                    if (v is string s && !full) v = ResultTable.Trim(s);
                    var key = name;
                    for (var n = 2; d.ContainsKey(key); n++) key = name + " #" + n;
                    if (v == null || (v is string str && str.Length == 0)) { empty.Add(key); continue; }
                    d[key] = v;
                    try { if (p.IsReadOnly) ro.Add(key); } catch (Exception) { }
                }
                return d;
            }

            var r = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out var docError);
                if (doc == null) { missing.AddRange(ids.Cast<object>()); paramsDocError = docError; return true; }
                rev = _tracker.For(doc)?.Rev ?? 0;
                foreach (var id in ids)
                {
                    var e = doc.GetElement(Ids.ToId(id));
                    if (e == null) { missing.Add(id); continue; }
                    var typeId = e.GetTypeId();
                    var head = new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["cat"] = e.Category != null ? CatalogRunner.BicName(e.Category) : null,
                        ["fam"] = (doc.GetElement(typeId) as ElementType)?.FamilyName,
                        ["type"] = doc.GetElement(typeId)?.Name ?? e.Name,
                    };
                    if (typeId != ElementId.InvalidElementId) head["type_id"] = Ids.ToLong(typeId);
                    elems.Add(head);
                    inst.Add(Read(doc, e, readOnly));
                    var tk = Ids.ToLong(typeId).ToString(CultureInfo.InvariantCulture);
                    if (withType && typeId != ElementId.InvalidElementId && !types.ContainsKey(tk) && doc.GetElement(typeId) is ElementType et)
                        types[tk] = Read(doc, et, readOnlyType);
                }
                return true;
            }, highPriority: true, busyAfter: Busy, ct: ct).ConfigureAwait(false);
            if (!r.Ok) return FromWork(request.Id, r);

            // What all elements share goes once in "common" (with 10 fixtures of the same type, ~70% less text).
            var data = new Dictionary<string, object>();
            if (inst.Count >= 2)
            {
                var common = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var kv in inst[0])
                {
                    if (inst.All(d => d.TryGetValue(kv.Key, out var v) && Equals(Norm(v), Norm(kv.Value)))) common[kv.Key] = kv.Value;
                }
                foreach (var d in inst) foreach (var k in common.Keys) d.Remove(k);
                if (common.Count > 0) data["common"] = common;
            }
            for (var i = 0; i < elems.Count; i++) elems[i]["params"] = inst[i];
            data["elements"] = elems.Cast<object>().ToList();
            if (types.Count > 0) data["types"] = types;
            if (readOnly.Count > 0) data["read_only"] = readOnly.Cast<object>().ToList();
            if (readOnlyType.Count > 0) data["type_read_only"] = readOnlyType.Cast<object>().ToList();
            if (empty.Count > 0) data["empties"] = empty.Cast<object>().ToList();
            if (paramsDocError != null) return Response.Fail(request.Id, paramsDocError.StartsWith(Status.TargetUnavailable) ? Status.TargetUnavailable : Status.Invalid, paramsDocError);
            if (missing.Count > 0) data["missing"] = missing;
            if (omitted > 0) data["limit_warning"] = "showing " + MaxParamElements + " elements; " + omitted + " omitted (use query f=… for many)";
            data["units"] = mm ? "lengths in mm" : "lengths in decimal feet";
            data["ms"] = sw.ElapsedMilliseconds;
            return new Response { Id = request.Id, Rev = rev, Data = data };
        }

        private static object Norm(object v) => v is double d ? (object)Math.Round(d, 6) : v;

        // ---------------- show ----------------

        private async Task<Response> Show(Session session, Request request, CancellationToken ct)
        {
            var ids = ResolveIds(session, request.Args, "ids", out var err);
            if (err != null) return Response.Fail(request.Id, Status.Invalid, err);
            var viewArg = Arg.GetLong(request.Args, "view") ?? Arg.GetLong(request.Args, "view");
            var select = Arg.GetBool(request.Args, "select") ?? true;
            var zoom = Arg.GetBool(request.Args, "zoom") ?? true;
            if (ids.Count == 0 && !viewArg.HasValue) return Response.Fail(request.Id, Status.Invalid, "specify ids (or $set) and/or view");

            var data = new Dictionary<string, object>();
            string error = null;
            long rev = 0;

            // 1) Switch view: Revit does it when the slice ends, so wait until the view becomes active.
            if (viewArg.HasValue)
            {
                var changed = false;
                var r1 = await RunMain((app, watch, budget) =>
                {
                    var uidoc = DocTarget.ActiveUi(app, out var uiError);
                    var doc = uidoc?.Document;
                    if (doc == null) { error = uiError ?? "There is no active document in Revit."; return true; }
                    var v = doc.GetElement(Ids.ToId(viewArg.Value)) as View;
                    if (v == null || v.IsTemplate) { error = "view " + viewArg.Value + " does not exist or is a template (catalog kind=views)"; return true; }
                    if (uidoc.ActiveView?.Id != v.Id) { uidoc.RequestViewChange(v); changed = true; }
                    return true;
                }, highPriority: true, busyAfter: Busy, ct: ct).ConfigureAwait(false);
                if (!r1.Ok) return FromWork(request.Id, r1);
                if (error != null) return Response.Fail(request.Id, Status.Invalid, error);
                if (changed)
                {
                    var active = false;
                    for (var i = 0; i < 20 && !active; i++)
                    {
                        await Task.Delay(150, ct).ConfigureAwait(false);
                        var probe = await RunMain((app, watch, budget) =>
                        {
                            active = Ids.ToLong(app.ActiveUIDocument?.ActiveView?.Id) == viewArg.Value;
                            return true;
                        }, highPriority: true, busyAfter: Busy, ct: ct).ConfigureAwait(false);
                        if (!probe.Ok) break;
                    }
                    if (!active) data["view_warning"] = "Revit has not switched views yet (a dialog open?); the selection was made anyway";
                }
            }

            // 2) Select and zoom to fit in the active view.
            var r = await RunMain((app, watch, budget) =>
            {
                var uidoc = DocTarget.ActiveUi(app, out var uiError);
                var doc = uidoc?.Document;
                if (doc == null) { error = uiError ?? "There is no active document in Revit."; return true; }
                rev = _tracker.For(doc)?.Rev ?? 0;
                var view = uidoc.ActiveView;
                data["view"] = new Dictionary<string, object> { ["id"] = Ids.ToLong(view.Id), ["name"] = view.Name };
                var existing = new List<ElementId>();
                var missing = new List<object>();
                foreach (var id in ids)
                {
                    var eid = Ids.ToId(id);
                    if (doc.GetElement(eid) != null) existing.Add(eid); else missing.Add(id);
                }
                if (missing.Count > 0) data["missing"] = missing.Take(10).ToList();
                if (select && existing.Count > 0)
                {
                    uidoc.Selection.SetElementIds(existing);
                    data["selected"] = (long)existing.Count;
                }
                if (!zoom || existing.Count == 0) return true;

                var inView = new List<ElementId>();
                try { inView = new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(existing)).ToElementIds().ToList(); }
                catch (Exception) { }
                data["visible_in_view"] = (long)inView.Count;
                if (inView.Count == 0)
                {
                    data["views_suggestion"] = ViewsShowing(doc, existing);
                    data["warning"] = "none is visible in the active view: use show with view=<id> from the suggested ones";
                    return true;
                }
                XYZ min = null, max = null;
                foreach (var id in inView)
                {
                    var bb = doc.GetElement(id)?.get_BoundingBox(view) ?? doc.GetElement(id)?.get_BoundingBox(null);
                    if (bb == null) continue;
                    var a = bb.Transform.OfPoint(bb.Min);
                    var b = bb.Transform.OfPoint(bb.Max);
                    var lo = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
                    var hi = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
                    min = min == null ? lo : new XYZ(Math.Min(min.X, lo.X), Math.Min(min.Y, lo.Y), Math.Min(min.Z, lo.Z));
                    max = max == null ? hi : new XYZ(Math.Max(max.X, hi.X), Math.Max(max.Y, hi.Y), Math.Max(max.Z, hi.Z));
                }
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(u => u.ViewId == view.Id);
                if (min != null && ui != null)
                {
                    var size = max - min;
                    var pad = Math.Max(4.0, 0.15 * Math.Max(size.X, size.Y));
                    ui.ZoomAndCenterRectangle(new XYZ(min.X - pad, min.Y - pad, min.Z), new XYZ(max.X + pad, max.Y + pad, max.Z));
                    data["framed"] = (long)inView.Count;
                }
                return true;
            }, highPriority: true, busyAfter: Busy, ct: ct).ConfigureAwait(false);
            if (!r.Ok) return FromWork(request.Id, r);
            if (error != null) return Response.Fail(request.Id, Status.Invalid, error);
            return new Response { Id = request.Id, Rev = rev, Data = data };
        }

        /// <summary>Floor plans and ceiling plans (non-template) of those elements' level: candidates where they would be visible.</summary>
        private static List<object> ViewsShowing(Document doc, List<ElementId> ids)
        {
            var levels = new HashSet<long>();
            foreach (var id in ids.Take(200))
            {
                var e = doc.GetElement(id);
                var lid = e?.LevelId;
                if ((lid == null || lid == ElementId.InvalidElementId) && e != null)
                    lid = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId();
                if (lid != null && lid != ElementId.InvalidElementId) levels.Add(Ids.ToLong(lid));
            }
            var cats = new HashSet<long>(ids.Take(200).Select(i => doc.GetElement(i)?.Category).Where(c => c != null).Select(c => Ids.ToLong(c.Id)));
            var lighting = cats.Contains(Ids.ToLong(new ElementId(BuiltInCategory.OST_LightingFixtures)));
            return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && levels.Contains(Ids.ToLong(v.GenLevel.Id)))
                .OrderBy(v => lighting ? (v.ViewType == ViewType.CeilingPlan ? 0 : 1) : (v.ViewType == ViewType.FloorPlan ? 0 : 1))
                .ThenBy(v => v.Name)
                .Take(6)
                .Select(v => (object)new Dictionary<string, object> { ["id"] = Ids.ToLong(v.Id), ["name"] = v.Name, ["type"] = CatalogRunner.ViewTypeName(v.ViewType) })
                .ToList();
        }

        // ---------------- capture ----------------

        private async Task<Response> CaptureOp(Session session, Request request, CancellationToken ct)
        {
            var cap = new Capture { ViewId = Arg.GetLong(request.Args, "view") ?? Arg.GetLong(request.Args, "view") };
            var px = Arg.GetLong(request.Args, "px");
            if (px.HasValue)
            {
                if (px < 400 || px > 2400) return Response.Fail(request.Id, Status.Invalid, "px between 400 and 2400 (default 1400; the AI sees up to ~1568)");
                cap.Px = (int)px.Value;
            }
            var color = (Arg.GetString(request.Args, "color_by") ?? "").Trim().ToLowerInvariant();
            if (color == "categories" || color == "categories" || color == "true" || color == "yes" || color == "yes") cap.ColorByCategory = true;
            else if (color.Length > 0 && color != "no" && color != "false") return Response.Fail(request.Id, Status.Invalid, "color_by: categories (or nothing)");

            // resaltar: "$a" | "1,2,3" | "$a=rojo;$b=azul;123,456=#00AAFF"
            var spec = Arg.GetString(request.Args, "highlight");
            if (spec == null && request.Args.TryGetValue("highlight", out var raw) && raw is List<object> l) spec = string.Join(",", l);
            if (!string.IsNullOrWhiteSpace(spec))
            {
                var parts = spec.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (parts.Count > 6) return Response.Fail(request.Id, Status.Invalid, "highlight accepts up to 6 groups separated by ;");
                for (var i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    string idsText = p, colorText = null;
                    var eq = p.LastIndexOf('=');
                    if (eq > 0) { idsText = p.Substring(0, eq).Trim(); colorText = p.Substring(eq + 1).Trim(); }
                    var g = new Capture.Group { Label = idsText.Length > 40 ? idsText.Substring(0, 40) + "…" : idsText };
                    g.Ids = ResolveIds(session, new Dictionary<string, object> { ["ids"] = idsText }, "ids", out var err);
                    if (err != null) return Response.Fail(request.Id, Status.Invalid, "highlight: " + err);
                    var cname = colorText ?? (cap.ColorByCategory && i == 0 ? "magenta" : Capture.DefaultGroupColors[i % Capture.DefaultGroupColors.Length]);
                    if (!Capture.TryNamedColor(cname, out var c, out var shown))
                        return Response.Fail(request.Id, Status.Invalid, "highlight: color '" + cname + "' (use red, blue, green, magenta, orange, cyan, yellow, violet, black or #RRGGBB)");
                    g.Color = c; g.ColorName = shown;
                    cap.Groups.Add(g);
                }
            }

            var frame = Arg.GetString(request.Args, "framing");
            if (!string.IsNullOrWhiteSpace(frame))
            {
                if (frame.Trim().Equals("highlighted", StringComparison.OrdinalIgnoreCase)) cap.FrameIds = cap.Groups.SelectMany(g => g.Ids).Distinct().ToList();
                else
                {
                    cap.FrameIds = ResolveIds(session, new Dictionary<string, object> { ["ids"] = frame }, "ids", out var err);
                    if (err != null) return Response.Fail(request.Id, Status.Invalid, "framing: " + err);
                }
            }

            var r = await RunMain((app, watch, budget) => cap.Run(app, _tracker), highPriority: true, busyAfter: Busy, ct: ct).ConfigureAwait(false);
            if (!r.Ok) return FromWork(request.Id, r);
            if (cap.Error != null) return Response.Fail(request.Id, Status.Invalid, cap.Error);
            cap.Info["mime"] = cap.Mime;
            return new Response { Id = request.Id, Rev = cap.Rev, Data = cap.Info };
        }
    }
}
