// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// An operation requested by the AI in "plan" (section 7). Typed operations only; never free code.
    /// The full catalog (27 operations) is in <see cref="OpCatalog"/>.
    /// ids: list of ids, "1,2,3", "$set" or "@n" (what operation n of the same plan created).
    /// </summary>
    public sealed class OpSpec
    {
        public string Kind;
        /// <summary>Position in the plan, from 1 (for @n references).</summary>
        public int Index;
        public List<long> Ids = new List<long>();
        public string Set;
        /// <summary>Earlier operations of the same plan whose created elements are used as ids.</summary>
        public List<int> RefOps = new List<int>();
        /// <summary>Arguments as received (keys already validated).</summary>
        public Dictionary<string, object> Args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Shortcuts for the original operations.
        public string Name;
        public object Value;
        public string Type;
        public string Workset;
        public double? Ft;

        public static string[] Kinds => OpCatalog.All.Select(i => i.Kind).ToArray();
        public const int MaxOps = 200;

        public static List<OpSpec> ParseAll(IDictionary<string, object> args)
        {
            if (!args.TryGetValue("ops", out var raw) || !(raw is List<object> list) || list.Count == 0)
                throw new QueryException("plan needs ops: [{op, ids, ...}]. Operations: " + OpCatalog.KindList + " (detail: guide topic=write)");
            if (list.Count > MaxOps) throw new QueryException("maximum " + MaxOps + " operations per plan");
            var result = new List<OpSpec>();
            foreach (var item in list)
            {
                if (!(item is Dictionary<string, object> o)) throw new QueryException("each op must be an object");
                var s = Parse(o, result.Count + 1);
                foreach (var r in s.RefOps)
                {
                    if (r < 1 || r >= s.Index) throw new QueryException(s.Kind + ": @" + r + " must be an earlier operation of the same plan");
                    if (!OpCatalog.ByKind[result[r - 1].Kind].Creates)
                        throw new QueryException(s.Kind + ": @" + r + " (" + result[r - 1].Kind + ") does not create elements");
                }
                result.Add(s);
            }
            return result;
        }

        public static OpSpec Parse(IDictionary<string, object> o, int index = 1)
        {
            var s = new OpSpec { Kind = (Arg.GetString(o, "op") ?? "").Trim().ToLowerInvariant(), Index = index };
            if (!OpCatalog.ByKind.TryGetValue(s.Kind, out var info))
                throw new QueryException("unknown op '" + s.Kind + "'. Use: " + OpCatalog.KindList + " (detail: guide topic=write)");
            foreach (var kv in o) s.Args[kv.Key] = kv.Value;

            foreach (var x in Arg.GetStringList(o, "ids"))
            {
                if (x.StartsWith("$")) { s.Set = NamedSets.Normalize(x); continue; }
                if (x.StartsWith("@"))
                {
                    if (!int.TryParse(x.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        throw new QueryException("ids: '" + x + "' must be @n (number of an earlier operation)");
                    s.RefOps.Add(n);
                    continue;
                }
                if (!long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    throw new QueryException("ids: '" + x + "' is not an id, a $set or @n");
                s.Ids.Add(id);
            }
            foreach (var key in new[] { "view", "sheet", "type", "origin" })
            {
                var v = Arg.GetString(o, key);
                if (v != null && v.StartsWith("@") && int.TryParse(v.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    s.RefOps.Add(n);
            }
            var hasIds = s.Ids.Count > 0 || s.Set != null || s.RefOps.Count > 0;
            OpCatalog.Validate(info, o, hasIds);

            switch (s.Kind)
            {
                case "set_param":
                    s.Name = Arg.GetString(o, "name");
                    o.TryGetValue("value", out s.Value);
                    break;
                case "set_values":
                    s.Name = Arg.GetString(o, "name");
                    if (!(o["values"] is Dictionary<string, object> vals) || vals.Count == 0)
                        throw new QueryException("set_values: values must be {\"id\": value, ...}");
                    if (vals.Count > PlanOpsLimits.MaxEntries) throw new QueryException("set_values: maximum " + PlanOpsLimits.MaxEntries + " values");
                    foreach (var k in vals.Keys)
                    {
                        if (!long.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vid))
                            throw new QueryException("set_values: the key '" + k + "' is not an id");
                        s.Ids.Add(vid);
                    }
                    break;
                case "change_type":
                    s.Type = Arg.GetString(o, "type");
                    break;
                case "set_elevation":
                    if (o.TryGetValue("ft", out var ft) && ft != null)
                    {
                        if (!(ft is double || ft is long)) throw new QueryException("set_elevation: ft must be a number in feet (or use value with units)");
                        s.Ft = Convert.ToDouble(ft, CultureInfo.InvariantCulture);
                    }
                    else if (o.TryGetValue("value", out var value) && value != null)
                    {
                        if (!Lengths.TryFeet(value, out var f, out var err)) throw new QueryException("set_elevation value: " + err);
                        s.Ft = f;
                    }
                    else throw new QueryException("set_elevation: missing ft (feet) or value (\"10' - 9\\\"\", \"3277 mm\")");
                    if (s.Ft < -1000 || s.Ft > 1000) throw new QueryException("set_elevation: out of range");
                    break;
                case "set_workset":
                    s.Workset = Arg.GetString(o, "ws");
                    break;
                case "set_type_param":
                    s.Name = Arg.GetString(o, "name");
                    o.TryGetValue("value", out s.Value);
                    s.Type = Arg.GetString(o, "type");
                    if (s.Type == null && !hasIds) throw new QueryException("set_type_param: give type \"Family: Type\" or instance ids");
                    break;
                case "snap_to_surface":
                    if (s.RefOps.Count > 0) throw new QueryException("snap_to_surface measures elements that already exist: do not use @n (to place flush use place with mounting=slab)");
                    var side = (Arg.GetString(o, "side") ?? "up").Trim().ToLowerInvariant();
                    if (side != "up" && side != "down") throw new QueryException("snap_to_surface side: up (default) or down");
                    OpCatalog.Length(o, "clearance", "snap_to_surface");
                    OpCatalog.Length(o, "max", "snap_to_surface");
                    var pts = OpCatalog.Number(o, "points", "snap_to_surface");
                    if (pts.HasValue && pts != 1 && pts != 5 && pts != 9) throw new QueryException("snap_to_surface points: 1, 5 or 9");
                    break;
                case "move":
                    if (o.ContainsKey("to"))
                    {
                        OpCatalog.Point(o["to"], "move to");
                        if (s.Ids.Count + (s.Set != null ? 2 : 0) + s.RefOps.Count != 1) throw new QueryException("move to: only with one element; for several use dx/dy/dz");
                    }
                    else
                    {
                        var any = false;
                        foreach (var k in new[] { "dx", "dy", "dz" }) any |= OpCatalog.Length(o, k, "move").HasValue;
                        if (!any) throw new QueryException("move: give dx, dy, dz or to [x,y,z]");
                    }
                    break;
                case "rotate":
                    OpCatalog.Number(o, "deg", "rotate");
                    if (o.TryGetValue("center", out var c) && c is List<object>) OpCatalog.Point(c, "rotate center", false);
                    break;
                case "mirror":
                    var axis = OpCatalog.Points(o["axis"], "mirror axis", 2, 2, false);
                    if (Math.Abs(axis[0][0] - axis[1][0]) + Math.Abs(axis[0][1] - axis[1][1]) < 1e-6) throw new QueryException("mirror axis: the two points are equal");
                    break;
                case "copy":
                    if (!o.ContainsKey("level"))
                    {
                        var anyc = false;
                        foreach (var k in new[] { "dx", "dy", "dz" }) anyc |= OpCatalog.Length(o, k, "copy").HasValue;
                        if (!anyc) throw new QueryException("copy: give dx/dy/dz or level");
                    }
                    break;
                case "place":
                    var placePoints = OpCatalog.Points(o["points"], "place points", 1, 5000);
                    if (o.TryGetValue("source_items", out var rawItems))
                    {
                        if (!(rawItems is List<object> items) || items.Count != placePoints.Count ||
                            items.Any(item => !(item is string text) || string.IsNullOrWhiteSpace(text)) ||
                            items.Distinct().Count() != items.Count)
                            throw new QueryException("place: source_items requires a unique identifier per point");
                    }
                    OpCatalog.Length(o, "elev", "place");
                    OpCatalog.Length(o, "clearance", "place");
                    OpCatalog.Number(o, "rot", "place");
                    var mounting = Arg.GetString(o, "mounting");
                    if (mounting != null && !string.Equals(mounting, "slab", StringComparison.OrdinalIgnoreCase) && !string.Equals(mounting, "floor", StringComparison.OrdinalIgnoreCase))
                        throw new QueryException("place mounting: slab (top face under what is above) or floor (base on what is below)");
                    if (mounting != null && o.ContainsKey("elev")) throw new QueryException("place: use elev or mounting, not both");
                    if (o.TryGetValue("params", out var pr) && pr != null && !(pr is Dictionary<string, object>))
                        throw new QueryException("place params: {\"Parameter\": value}");
                    break;
                case "tag":
                    if (o.TryGetValue("offset", out var off) && off != null) OpCatalog.Point(off, "tag offset", false);
                    var orientation = Arg.GetString(o, "orientation");
                    if (orientation != null && orientation != "horizontal" && orientation != "vertical") throw new QueryException("tag orientation: horizontal or vertical");
                    break;
                case "tag_move":
                    if (o.TryGetValue("head", out var h) && h != null) OpCatalog.Point(h, "tag_move head", false);
                    foreach (var k in new[] { "dx", "dy" }) OpCatalog.Length(o, k, "tag_move");
                    foreach (var k in new[] { "dxp", "dyp" }) OpCatalog.Number(o, k, "tag_move");
                    if (o.TryGetValue("end", out var ex) && ex != null) OpCatalog.Point(ex, "tag_move end", false);
                    if (o.TryGetValue("elbow", out var co) && co != null) OpCatalog.Point(co, "tag_move elbow", false);
                    if (!new[] { "head", "dx", "dy", "dxp", "dyp", "leader", "end", "elbow", "orientation" }.Any(o.ContainsKey))
                        throw new QueryException("tag_move: give head, dx/dy, dxp/dyp, leader, end, elbow or orientation");
                    break;
                case "group_place":
                    OpCatalog.Points(o["points"], "group_place points", 1, 1000);
                    OpCatalog.Number(o, "rot", "group_place");
                    if (o.ContainsKey("reuse") && !Arg.GetBool(o, "reuse").HasValue)
                        throw new QueryException("group_place reuse: give true or false");
                    if (o.ContainsKey("prototype") && (!(Arg.GetLong(o, "prototype") is long prototype) || prototype <= 0))
                        throw new QueryException("group_place prototype: give the positive ID of the reference group");
                    break;
                case "group_variant":
                    if (s.Ids.Count != 1 || s.Ids[0] <= 0 || s.Set != null || s.RefOps.Count != 0 ||
                        !(o["rules"] is Dictionary<string, object> groupRules) || groupRules.Count == 0 ||
                        groupRules.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                            !(pair.Value is string targetType) || string.IsNullOrWhiteSpace(targetType)))
                        throw new QueryException("group_variant: give the ID of a group and rules {old type: new type}");
                    break;
                case "view_create":
                    var vt = (Arg.GetString(o, "type") ?? "").Trim().ToLowerInvariant();
                    var allowed = new[] { "plan", "rcp", "section", "callout", "3d", "duplicate", "drafting" };
                    if (Array.IndexOf(allowed, vt) < 0) throw new QueryException("view_create type: " + string.Join(", ", allowed));
                    if ((vt == "plan" || vt == "rcp") && Arg.GetString(o, "level") == null) throw new QueryException("view_create " + vt + ": missing level");
                    if ((vt == "duplicate" || vt == "callout") && Arg.GetString(o, "source") == null) throw new QueryException("view_create " + vt + ": missing source (view)");
                    if ((vt == "section" || vt == "callout") && !o.ContainsKey("box")) throw new QueryException("view_create " + vt + ": missing box [[x0,y0,z0],[x1,y1,z1]]");
                    if (o.ContainsKey("box")) OpCatalog.Points(o["box"], "view_create box", 2, 2);
                    var option = Arg.GetString(o, "option");
                    if (option != null && option != "duplicate" && option != "with_detailing" && option != "dependent") throw new QueryException("view_create option: duplicate, with_detailing or dependent");
                    break;
                case "view_set":
                    if (!new[] { "template", "range", "scale", "detail", "name", "crop", "scope_box", "section_box" }.Any(o.ContainsKey))
                        throw new QueryException("view_set: give template, range, scale, detail, name, crop, scope_box or section_box");
                    if (o.TryGetValue("range", out var rg) && rg != null)
                    {
                        if (!(rg is Dictionary<string, object> rd) || rd.Count == 0) throw new QueryException("view_set range: {top:[\"LEVEL\",offset], cut:[…], bottom:[…], depth:[…]}");
                        foreach (var kv in rd)
                        {
                            if (Array.IndexOf(new[] { "top", "cut", "bottom", "depth" }, kv.Key) < 0) throw new QueryException("view_set range: keys top, cut, bottom, depth");
                            if (!(kv.Value is List<object> pair) || pair.Count != 2) throw new QueryException("view_set range " + kv.Key + ": [\"LEVEL\", offset] (level: name, actual, up, down or unlimited)");
                            if (!Lengths.TryFeet(pair[1], out _, out var err)) throw new QueryException("view_set range " + kv.Key + ": " + err);
                        }
                    }
                    if (o.ContainsKey("section_box")) OpCatalog.Points(o["section_box"], "view_set section_box", 2, 2);
                    var det = Arg.GetString(o, "detail");
                    if (det != null && det != "fine" && det != "medium" && det != "coarse") throw new QueryException("view_set detail: fine, medium or coarse");
                    OpCatalog.Number(o, "scale", "view_set");
                    break;
                case "view_hide":
                case "view_override":
                    if (!o.ContainsKey("ids") && !o.ContainsKey("cats")) throw new QueryException(s.Kind + ": give ids or cats");
                    OpCatalog.Number(o, "transparency", s.Kind);
                    OpCatalog.Number(o, "line_weight", s.Kind);
                    break;
                case "text_note":
                    OpCatalog.Point(o["point"], "text_note point", false);
                    var linked = o.ContainsKey("fixture");
                    if (linked && (!(Arg.GetLong(o, "fixture") is long lightId) || lightId <= 0 || o.ContainsKey("text")))
                        throw new QueryException("text_note: fixture requires its positive ID and generates the text from the model");
                    if (!linked && string.IsNullOrWhiteSpace(Arg.GetString(o, "text")))
                        throw new QueryException("text_note: give text or fixture");
                    foreach (var key in new[] { "code_param", "height_param", "subtract_type_param" })
                        if (o.TryGetValue(key, out var name) && (!linked || !(name is string param) || string.IsNullOrWhiteSpace(param)))
                            throw new QueryException("text_note: " + key + " requires a fixture and the name of a parameter");
                    foreach (var key in new[] { "bold", "underline" })
                        if (o.ContainsKey(key) && !Arg.GetBool(o, key).HasValue)
                            throw new QueryException("text_note " + key + ": give true or false");
                    break;
                case "annotation_rebind":
                    if (s.Ids.Count != 1 || s.Ids[0] <= 0 || s.Set != null || s.RefOps.Count != 0)
                        throw new QueryException("annotation_rebind: give the ID of a single annotation");
                    if (o.ContainsKey("target") == o.ContainsKey("stable_refs"))
                        throw new QueryException("annotation_rebind: give target or stable_refs");
                    if (o.ContainsKey("target") && (!(Arg.GetLong(o, "target") is long destination) || destination <= 0))
                        throw new QueryException("annotation_rebind: target must be a positive ID");
                    if (o.TryGetValue("stable_refs", out var stableRefs) &&
                        (!(stableRefs is List<object> stable) || stable.Count < 2 || stable.Count > 100 ||
                         stable.Any(x => !(x is string reference) || string.IsNullOrWhiteSpace(reference)) ||
                         stable.Distinct().Count() != stable.Count))
                        throw new QueryException("annotation_rebind: stable_refs requires 2 to 100 non-repeated references");
                    break;
                case "level_create":
                case "grid_create":
                case "wall_create":
                case "floor_create":
                case "ceiling_create":
                case "room_create":
                case "duct_create":
                case "pipe_create":
                case "conduit_create":
                case "conduit_route":
                case "circuit_create":
                case "schedule_create":
                    ModelOps.Validate(s.Kind, o, s);
                    break;
                case "filled_region":
                {
                    var fp = OpCatalog.Points(o["points"], "filled_region points", 3, 200, false);
                    var le = Draw2D.ValidateLoop(fp);
                    if (le != null) throw new QueryException("filled_region: " + le);
                    break;
                }
                case "detail_line":
                {
                    var lp = OpCatalog.Points(o["points"], "detail_line points", 2, Draw2D.MaxVertices, false);
                    Draw2D.Segments(lp, Arg.GetBool(o, "closed") == true, out var le);
                    if (le != null) throw new QueryException("detail_line: " + le);
                    break;
                }
                case "detail_rect":
                {
                    var ec = OpCatalog.Points(o["corners"], "detail_rect corners", 2, 2, false);
                    Draw2D.Rect(ec[0], ec[1], out var le);
                    if (le != null) throw new QueryException("detail_rect: " + le);
                    break;
                }
                case "detail_arc":
                {
                    OpCatalog.Point(o["center"], "detail_arc center", false);
                    if (!Lengths.TryFeet(o["radius"], out var rad, out var rerr)) throw new QueryException("detail_arc radius: " + rerr);
                    var a0 = OpCatalog.Number(o, "start", "detail_arc") ?? throw new QueryException("detail_arc: missing start (degrees)");
                    var a1 = OpCatalog.Number(o, "end", "detail_arc") ?? throw new QueryException("detail_arc: missing end (degrees)");
                    var ae = Draw2D.ValidateArc(rad, a0, a1);
                    if (ae != null) throw new QueryException("detail_arc: " + ae);
                    break;
                }
                case "detail_polygon":
                {
                    var pc = OpCatalog.Point(o["center"], "detail_polygon center", false);
                    if (!Lengths.TryFeet(o["radius"], out var rad, out var rerr)) throw new QueryException("detail_polygon radius: " + rerr);
                    var sides = OpCatalog.Number(o, "sides", "detail_polygon") ?? 0;
                    Draw2D.RegularPolygon(pc, rad, (int)sides, OpCatalog.Number(o, "rot", "detail_polygon") ?? 0, out var pe);
                    if (pe != null) throw new QueryException("detail_polygon: " + pe);
                    break;
                }
                case "detail_component":
                    OpCatalog.Point(o["point"], "detail_component point", false);
                    OpCatalog.Number(o, "rot", "detail_component");
                    break;
                case "dimension":
                {
                    var dl = OpCatalog.Points(o["line"], "dimension line", 2, 2, false);
                    Draw2D.Segments(dl, false, out var segErr);
                    if (segErr != null) throw new QueryException("dimension line: " + segErr);
                    var refs = (Arg.GetString(o, "refs") ?? "").Split(',').Where(x => x.Trim().Length > 0).ToList();
                    if (refs.Count < 2) throw new QueryException("dimension refs: at least 2 detail line ids");
                    if (refs.Any(x => x.Trim().StartsWith("@", StringComparison.Ordinal))) throw new QueryException("dimension refs: in 0.8.0 only existing ids (no @n of the same plan): create the lines in one plan and dimension in the next");
                    break;
                }
                case "sheet_place":
                    if (o.TryGetValue("point", out var sp) && sp != null) OpCatalog.Point(sp, "sheet_place point", false);
                    break;
                case "sheet_set":
                {
                    if (string.IsNullOrWhiteSpace(Arg.GetString(o, "name"))) throw new QueryException("sheet_set: missing name");
                    var hs = o.TryGetValue("sheets", out var hv) && hv is List<object> hl ? hl.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "").ToList() : null;
                    if (hs == null || hs.Count == 0 || hs.Count > 1000 || hs.Any(x => x.Length == 0)) throw new QueryException("sheet_set sheets: list of 1 to 1000 sheets (number, name or id)");
                    if (hs.Any(x => x.StartsWith("@", StringComparison.Ordinal))) throw new QueryException("sheet_set sheets: no @n; create the sheets in one plan and save the set in the next");
                    break;
                }
            }
            return s;
        }
    }

    public static class PlanOpsLimits
    {
        public const int MaxEntries = 20000;
    }

    /// <summary>A concrete change on an element, with the value it had when planned and the value it will have.</summary>
    public sealed class PlanEntry : IJsonWritable
    {
        /// <summary>Element that changes; 0 if the entry creates a new one.</summary>
        public long Id;
        public string Op;
        /// <summary>What changes: parameter name, "type", "elev", "ws", "exists", "loc", "new"…</summary>
        public string Key;
        /// <summary>Canonical value (text) when planned. On apply it is compared with the current one: if it differs, there is a conflict.</summary>
        public string Before;
        public string After;
        /// <summary>Typed value to apply (double/long/string/bool), target id or an object with the creation data.</summary>
        public object Target;
        /// <summary>Plan operation that generated it (from 1), to resolve @n.</summary>
        public int OpIndex;
        /// <summary>Readable text of the "after" when After is technical (e.g. a creation).</summary>
        public string Show;
        /// <summary>
        /// &gt; 0: the entry applies to what operation Ref of the same plan creates (ids=@n). When planning, those elements do not
        /// exist yet: there is no "before" to compare.
        /// </summary>
        public int Ref;

        public bool IsCreation => Id == 0 && Ref == 0;
        public bool IsTemplate => Ref > 0;

        public object ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id, ["op"] = Op, ["key"] = Key, ["before"] = Before, ["after"] = After, ["target"] = Target,
            };
            if (OpIndex > 0) d["n"] = (long)OpIndex;
            if (Show != null) d["show"] = Show;
            if (Ref > 0) d["ref"] = (long)Ref;
            return d;
        }

        /// <summary>Held plans written by 0.9.3 or earlier store view_create kinds with their legacy names.</summary>
        private static object LegacyTargetValues(string op, object t)
        {
            if (op == "view_create" && t is IDictionary<string, object> d)
                foreach (var k in new[] { "type", "option" })
                    if (d.TryGetValue(k, out var v) && v is string s) d[k] = LegacyAliases.Token(s);
            return t;
        }

        public static PlanEntry FromJson(Dictionary<string, object> o) => new PlanEntry
        {
            Id = Arg.GetLong(o, "id") ?? 0,
            Op = Arg.GetString(o, "op"),
            Key = LegacyAliases.EntryKey(Arg.GetString(o, "key")),
            Before = Arg.GetString(o, "before"),
            After = Arg.GetString(o, "after"),
            Target = o.TryGetValue("target", out var t) ? LegacyTargetValues(Arg.GetString(o, "op"), t) : null,
            OpIndex = (int)(Arg.GetLong(o, "n") ?? 0),
            Show = Arg.GetString(o, "show"),
            Ref = (int)(Arg.GetLong(o, "ref") ?? 0),
        };

        /// <summary>
        /// Are two canonical values the same? Equal text, or the same numbers separated by , | ; = + and spaces with a difference
        /// smaller than <paramref name="tol"/> (feet: 1e-5 ft = 0.003 mm). Avoids false conflicts from floating-point noise when chaining
        /// held plans and when verifying after apply.
        /// </summary>
        public static bool Same(string a, string b, double tol = 1e-5)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            if (a == null || b == null) return false;
            var ta = Tokens(a); var tb = Tokens(b);
            if (ta.Count != tb.Count) return false;
            for (var i = 0; i < ta.Count; i++)
            {
                if (string.Equals(ta[i], tb[i], StringComparison.Ordinal)) continue;
                if (!double.TryParse(ta[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
                if (!double.TryParse(tb[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
                if (Math.Abs(x - y) > tol)
                {
                    // Angles: 359.99999 and 0 are the same rotation.
                    if (!(Math.Abs(Math.Abs(x - y) - 360) <= tol)) return false;
                }
            }
            return true;
        }

        private static List<string> Tokens(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                if (ch == ',' || ch == '|' || ch == ';' || ch == '=' || ch == ' ' || (ch == '+' && sb.Length > 0))
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                    list.Add(ch.ToString());
                    continue;
                }
                sb.Append(ch);
            }
            list.Add(sb.ToString());
            return list;
        }

        /// <summary>Canonical form to compare values without rounding or formatting issues.</summary>
        public static string Canon(object v)
        {
            switch (v)
            {
                case null: return "";
                case double d: return Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture);
                case float f: return Math.Round(f, 6).ToString("R", CultureInfo.InvariantCulture);
                case bool b: return b ? "1" : "0";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }
    }

    public static class PlanStatus
    {
        public const string Ready = "ready";            // planned, waiting for commit
        public const string InProgress = "in_progress";
        public const string Held = "held";      // in the Revit queue, waiting for the user to apply it
        public const string Applied = "applied";
        public const string Conflict = "conflict";
        public const string RolledBack = "reverted";
        public const string Discarded = "discarded";
        public const string Expired = "expired";
        /// <summary>alpha3: result of commit modo=ensayo (applied, verified and REVERTED). Not a plan state:
        /// the plan goes back to "ready". Never equivalent to applied.</summary>
        public const string Trialed = "trialed";
        /// <summary>Uncertain result already checked and recorded. Terminal: never returns to listo.</summary>
        public const string Reconciled = "reconciled";
    }

    public sealed class Plan : IJsonWritable
    {
        public string Id;
        public string SessionId;
        public string Client;
        public string DocKey;
        public string DocTitle;
        /// <summary>Handle dN of the document in this Revit (commit applies to it even if the user switches tabs).</summary>
        public string DocId;
        /// <summary>Authorized task (tarea=…): another connection of the same task can commit after reconnecting.</summary>
        public string Task;
        /// <summary>Idempotency key of the commit (clave=…).</summary>
        public string IdemKey;
        /// <summary>AUD-19: link to the batch source (token, key and fingerprint derived from the content). null if the plan does not come from a source.
        /// The commit uses this key and this fingerprint in the operation journal even if the agent does not pass clave=.</summary>
        public string BoundToken, BoundKey, BoundHash;
        public string ReceiptAttempt, ReceiptSession, ReceiptAgent, ReceiptDocPrint;
        public string Summary;          // short text the AI gives (or is generated) to show in the queue
        public DateTime CreatedUtc;
        public string Status = PlanStatus.Ready;
        public string StatusDetail;
        public DateTime? HeldUtc;
        public DateTime? FinishedUtc;
        public List<PlanEntry> Entries = new List<PlanEntry>();
        public List<string> Warnings = new List<string>();
        public int AppliedBatches;
        public int TotalBatches;
        /// <summary>Ids created on apply (in order). Saved as $creados in the session.</summary>
        public List<long> Created = new List<long>();
        /// <summary>Created by each operation (n from 1): $creados_n in the session.</summary>
        public Dictionary<int, List<long>> CreatedByOp = new Dictionary<int, List<long>>();
        /// <summary>Re-read after apply: how many ended up as planned and which did not.</summary>
        public Dictionary<string, object> Verification;
        public Dictionary<string, object> Reconciliation;
        /// <summary>Last storage failure of this plan. Null if its state is on disk.</summary>
        public string StorageWarning;
        /// <summary>Copy read from disk: the truth of the lock is still in the file (it may be another process's live intent).</summary>
        internal bool FromDisk;

        public bool IsFinal => Status == PlanStatus.Applied || Status == PlanStatus.Conflict || Status == PlanStatus.RolledBack
                               || Status == PlanStatus.Discarded || Status == PlanStatus.Expired || Status == PlanStatus.Reconciled;

        public int ElementCount => Entries.Where(e => e.Id != 0).Select(e => e.Id).Distinct().Count();
        public int CreationCount => Entries.Count(e => e.IsCreation);

        public object ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["session"] = SessionId, ["client"] = Client, ["doc_key"] = DocKey, ["doc_title"] = DocTitle, ["doc_id"] = DocId, ["task"] = Task,
            ["summary"] = Summary, ["created_utc"] = CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
            ["status"] = Status, ["status_detail"] = StatusDetail,
            ["held_utc"] = HeldUtc?.ToString("o", CultureInfo.InvariantCulture),
            ["finished_utc"] = FinishedUtc?.ToString("o", CultureInfo.InvariantCulture),
            ["entries"] = Entries.Cast<object>().ToList(), ["warnings"] = Warnings.Cast<object>().ToList(),
            ["created"] = Created.Cast<object>().ToList(),
            ["created_by_op"] = CreatedByOp.ToDictionary(k => k.Key.ToString(CultureInfo.InvariantCulture), k => (object)k.Value.Cast<object>().ToList()),
            ["applied_batches"] = (long)AppliedBatches, ["total_batches"] = (long)TotalBatches,
            ["reconciliation"] = Reconciliation,
            ["origin"] = BoundToken, ["origin_key"] = BoundKey, ["origin_fingerprint"] = BoundHash,
            ["attempt_id"] = ReceiptAttempt, ["attempt_session"] = ReceiptSession, ["attempt_agent"] = ReceiptAgent, ["attempt_doc"] = ReceiptDocPrint,
        };

        public static Plan FromJson(string json)
        {
            var o = LegacyAliases.NormalizeRecord(Json.ParseObject(json));
            var p = new Plan
            {
                Id = Arg.GetString(o, "id"),
                SessionId = Arg.GetString(o, "session"),
                Client = Arg.GetString(o, "client"),
                DocKey = Arg.GetString(o, "doc_key"),
                DocTitle = Arg.GetString(o, "doc_title"),
                DocId = Arg.GetString(o, "doc_id"),
                Task = Arg.GetString(o, "task"),
                Summary = Arg.GetString(o, "summary"),
                CreatedUtc = DateTime.Parse(Arg.GetString(o, "created_utc") ?? DateTime.UtcNow.ToString("o"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Status = Arg.GetString(o, "status") ?? PlanStatus.Held,
                StatusDetail = Arg.GetString(o, "status_detail"),
            };
            var held = Arg.GetString(o, "held_utc");
            if (held != null) p.HeldUtc = DateTime.Parse(held, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (o.TryGetValue("entries", out var e) && e is List<object> list)
                foreach (var x in list.OfType<Dictionary<string, object>>()) p.Entries.Add(PlanEntry.FromJson(x));
            if (o.TryGetValue("warnings", out var w) && w is List<object> wl)
                foreach (var x in wl) p.Warnings.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
            if (o.TryGetValue("created", out var c) && c is List<object> cl)
                foreach (var x in cl) if (x is long l) p.Created.Add(l);
            if (o.TryGetValue("created_by_op", out var cbo) && cbo is Dictionary<string, object> cbod)
                foreach (var kv in cbod)
                    if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var opIndex) && kv.Value is List<object> ids)
                        p.CreatedByOp[opIndex] = ids.OfType<long>().ToList();
            var finished = Arg.GetString(o, "finished_utc");
            if (finished != null) p.FinishedUtc = DateTime.Parse(finished, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            p.AppliedBatches = (int)(Arg.GetLong(o, "applied_batches") ?? 0);
            p.TotalBatches = (int)(Arg.GetLong(o, "total_batches") ?? 0);
            if (o.TryGetValue("reconciliation", out var rec) && rec is Dictionary<string, object> recd) p.Reconciliation = recd;
            p.BoundToken = Arg.GetString(o, "origin"); p.BoundKey = Arg.GetString(o, "origin_key"); p.BoundHash = Arg.GetString(o, "origin_fingerprint");
            p.ReceiptAttempt = Arg.GetString(o, "attempt_id"); p.ReceiptSession = Arg.GetString(o, "attempt_session");
            p.ReceiptAgent = Arg.GetString(o, "attempt_agent"); p.ReceiptDocPrint = Arg.GetString(o, "attempt_doc");
            return p;
        }

        /// <summary>Compact diff for the AI and for the queue: header once and up to N rows.</summary>
        public Dictionary<string, object> Describe(int maxRows = 30)
        {
            var d = new Dictionary<string, object>
            {
                ["plan_id"] = Id,
                ["plan_status"] = Status,
                ["elements"] = (long)ElementCount,
                ["changes"] = (long)Entries.Count,
                ["per_op"] = Entries.GroupBy(x => x.Op).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["cols"] = new List<object> { "id", "op", "field", "before", "after" },
                ["rows"] = Entries.Take(maxRows).Select(x => (object)new object[]
                {
                    x.Ref > 0 ? "@" + x.Ref : x.Id == 0 ? (object)"new" : x.Id, x.Op, x.Key,
                    ResultTable.Trim(ShowValue(x, x.Before, true)), ResultTable.Trim(x.Show ?? ShowValue(x, x.After, false)),
                }).ToList(),
            };
            if (BoundKey != null) d["linked_key"] = BoundKey;   // AUD-19: the commit uses it even if clave= is not passed
            var created = CreationCount;
            if (created > 0) d["new_items"] = (long)created;
            if (Entries.Count > maxRows) d["skipped_rows"] = (long)(Entries.Count - maxRows);
            if (Warnings.Count > 0) d["warnings"] = Warnings.Cast<object>().ToList();
            if (!string.IsNullOrEmpty(StatusDetail)) d["detail"] = StatusDetail;
            if (Created.Count > 0)
            {
                d["created"] = (long)Created.Count;
                d["created_ids"] = Created.Take(50).Cast<object>().ToList();
            }
            if (Verification != null) d["verification"] = Verification;
            if (Reconciliation != null) d["reconciliation"] = Reconciliation;
            if (StorageWarning != null) d["storage"] = StorageWarning;
            return d;
        }

        /// <summary>The "before" values of creations are technical guards (what must still exist): shown as "—".</summary>
        public static string ShowValue(PlanEntry e, string v, bool before)
        {
            if (v == null) return "";
            if (before && e.Id == 0) return "—";
            if (v.StartsWith("\u0000", StringComparison.Ordinal)) return v.Substring(1);
            if (e.Key == "exists") return v == "1" ? "exists" : "deleted";
            if ((e.Key == "elev" || e.Key == "offset") && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ft))
                return Lengths.FeetInches16(ft) + " (" + Math.Round(ft, 4).ToString(CultureInfo.InvariantCulture) + " ft)";
            return v;
        }

        /// <summary>Entries whose current value is no longer the one seen when planning (someone changed it).</summary>
        public List<PlanEntry> FindConflicts(Func<PlanEntry, string> currentValue)
        {
            var list = new List<PlanEntry>();
            foreach (var e in Entries)
            {
                string now;
                try { now = currentValue(e); }
                catch (Exception) { now = "\u0000error"; }
                if (!PlanEntry.Same(now, e.Before)) list.Add(e);
            }
            return list;
        }
    }

    /// <summary>
    /// Plans in memory + queue of held plans on disk (%LOCALAPPDATA%\Bimbridge\cola\&lt;doc&gt;\P-xxxx.json).
    /// The on-disk queue survives a Revit close or crash; on apply every value is validated again.
    /// </summary>
    public sealed class PlanStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Plan> _plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly string _queueRoot;
        private readonly Func<DateTime> _now;
        private int _counter;
        /// <summary>Disk operations; replaceable to test storage failures.</summary>
        public Action<string, string> WriteFile = Home.WriteAtomic;
        public Action<string> RemoveFile = path => { if (File.Exists(path)) File.Delete(path); };
        public TimeSpan LockWait = TimeSpan.FromSeconds(5);

        public PlanStore(string queueRoot, Func<DateTime> now = null)
        {
            _queueRoot = queueRoot;
            _now = now ?? (() => DateTime.UtcNow);
        }

        public static string DefaultQueueRoot
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("BIMBRIDGE_HOME");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimbridge");
                return LegacyAliases.ResolveExisting(root, Path.Combine(root, "queue"));
            }
        }

        public string NewId()
        {
            lock (_lock)
            {
                _counter++;
                return "P-" + _now().ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + _counter.ToString("D3", CultureInfo.InvariantCulture);
            }
        }

        public void Add(Plan p) { lock (_lock) _plans[p.Id] = p; }

        public Plan Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock) return _plans.TryGetValue(id.Trim(), out var p) ? p : null;
        }

        /// <summary>Moves to "expired" the ready plans older than the limit. Held plans do not expire.</summary>
        public int ExpireOld(TimeSpan maxAge)
        {
            var n = 0;
            lock (_lock)
                foreach (var p in _plans.Values)
                    if (p.Status == PlanStatus.Ready && _now() - p.CreatedUtc > maxAge)
                    {
                        p.Status = PlanStatus.Expired;
                        p.StatusDetail = "more than " + (int)maxAge.TotalMinutes + " min passed; plan again";
                        n++;
                    }
            return n;
        }

        public void Hold(Plan p)
        {
            lock (_lock)
            {
                p.Status = PlanStatus.Held;
                p.HeldUtc = _now();
                p.StatusDetail = "in the Revit queue; the user will apply it with 'Apply queue'";
                _plans[p.Id] = p;
            }
            Persist(p);
        }

        /// <summary>Held plans of a document, in arrival order.</summary>
        public List<Plan> Held(string docKey)
        {
            lock (_lock)
                return _plans.Values
                    .Where(p => p.Status == PlanStatus.Held && string.Equals(p.DocKey, docKey, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.HeldUtc ?? p.CreatedUtc).ThenBy(p => p.Id, StringComparer.Ordinal)
                    .ToList();
        }

        /// <summary>
        /// Closes a plan. Returns false if its state did not reach disk: the failure is noted in the plan and the document stays locked.
        /// An unknown result always keeps evidence, even if the caller did not prepare it.
        /// </summary>
        public bool Finish(Plan p, string status, string detail)
        {
            lock (_lock)
            {
                p.Status = status;
                p.StatusDetail = detail;
                p.FinishedUtc = _now();
                p.StorageWarning = null;
                p.FromDisk = false;
                if (status == Status.Unknown) GroupAbort.PreserveInterrupted(p, detail);
            }
            // The evidence of an uncertain result must survive a Revit close: it is never deleted on finish.
            try
            {
                if (p.Reconciliation == null) DeleteFile(p);
                else Persist(p);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lock (_lock)
                    p.StorageWarning = (p.Reconciliation == null
                        ? "could not remove the write intent from disk (" + ex.Message + "); the document stays locked until reconciled."
                        : "could not save the evidence to disk (" + ex.Message + "); it is not durable and the document stays locked.");
                return false;
            }
        }

        /// <summary>Plans with evidence pending reconciliation in a document.</summary>
        public List<Plan> Unreconciled(string docKey)
        {
            lock (_lock)
                return _plans.Values
                    .Where(p => p.Reconciliation != null && p.Status != PlanStatus.Reconciled && string.Equals(p.DocKey, docKey, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.FinishedUtc ?? p.CreatedUtc).ThenBy(p => p.Id, StringComparer.Ordinal)
                    .ToList();
        }

        /// <summary>
        /// Can it write? Always reads the disk, so it sees what another process or a Revit close left behind.
        /// It also counts evidence that is only in this process's memory. A read doubt blocks.
        /// </summary>
        public DocWriteBlock CheckWritable(string docKey)
        {
            var block = new DocWriteBlock { DocKey = docKey };
            lock (_lock)
                foreach (var p in _plans.Values)
                    if (!p.FromDisk && p.Reconciliation != null && p.Status != PlanStatus.Reconciled && string.Equals(p.DocKey, docKey, StringComparison.OrdinalIgnoreCase))
                        block.Plans.Add(p.Id);
            try
            {
                var dir = DocDir(docKey);
                if (!Directory.Exists(dir)) return block;
                foreach (var f in Directory.GetFiles(dir, "P-*.json"))
                {
                    Plan p;
                    try { p = Plan.FromJson(ReadShared(f)); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { block.Errors.Add(Path.GetFileName(f) + ": " + ex.Message); continue; }
                    catch (Exception) { block.CorruptFiles.Add(Path.GetFileName(f)); continue; }
                    if (p.Status == PlanStatus.Reconciled || p.Id == null) { if (p.Id == null) block.CorruptFiles.Add(Path.GetFileName(f)); continue; }
                    if ((p.Reconciliation != null || p.Status == PlanStatus.InProgress || p.Status == Status.Unknown) && !block.Plans.Contains(p.Id)) block.Plans.Add(p.Id);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { block.Errors.Add(ex.Message); }
            return block;
        }

        /// <summary>
        /// Last barrier before touching the model: checks the document and leaves the write intent on disk, under a cross-process
        /// lock. If Revit dies afterwards, the file keeps the document locked. Throws without having changed anything if it cannot guarantee this.
        /// </summary>
        public void BeginWrite(Plan p)
        {
            WithDocLock(p.DocKey, () =>
            {
                var block = CheckWritable(p.DocKey);
                if (block.Blocked) throw new DocWriteBlockedException(block);
                WriteSnapshot(p, "write intent with no recorded result: the process may have ended during the operation");
            });
        }

        /// <summary>Checkpoint after each batch: keeps the created ids. Throws PlanStoreException if it did not reach disk.</summary>
        public void Checkpoint(Plan p) => WriteSnapshot(p, "interrupted after batch " + p.AppliedBatches + " of " + p.TotalBatches);

        private void WriteSnapshot(Plan p, string reason)
        {
            string text;
            lock (_lock)
            {
                var status = p.Status;
                var evidence = p.Reconciliation;
                try
                {
                    p.Status = PlanStatus.InProgress;
                    p.Reconciliation = null;
                    GroupAbort.PreserveInterrupted(p, reason);
                    text = Json.Write(p);
                }
                finally { p.Status = status; p.Reconciliation = evidence; }
            }
            try { WriteFile(Path.Combine(DocDir(p.DocKey), p.Id + ".json"), text); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new PlanStoreException("could not save the write intent of plan " + p.Id + ": " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Releases a plan only with a verified verdict. First archives evidence and verdict in reconciliados\ and then removes the active
        /// file: if anything fails, the document stays locked. An unverifiable verdict is noted and releases nothing.
        /// </summary>
        public bool Reconcile(Plan p, ReconcileCheck check, string author)
        {
            var released = false;
            WithDocLock(p.DocKey, () =>
            {
                var record = new Dictionary<string, object>
                {
                    ["utc"] = _now().ToString("o", CultureInfo.InvariantCulture), ["author"] = author, ["result"] = check.ToJson(),
                };
                if (!check.Verified && check.Verdict != ReconcileCheck.ManualHuman)
                {
                    lock (_lock)
                    {
                        if (p.Reconciliation == null) GroupAbort.PreserveInterrupted(p, "no prior evidence");
                        if (!(p.Reconciliation.TryGetValue("attempts", out var a) && a is List<object> attempts)) p.Reconciliation["attempts"] = attempts = new List<object>();
                        attempts.Add(record);
                    }
                    try { Persist(p); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { lock (_lock) p.StorageWarning = "could not note the attempt: " + ex.Message; }
                    return;
                }
                var archive = new Dictionary<string, object> { ["record"] = record, ["plan"] = Json.Parse(Json.Write(p)) };
                var stamp = _now().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                try
                {
                    WriteFile(Path.Combine(DocDir(p.DocKey), "reconciled", p.Id + "-" + stamp + ".json"), Json.Write(archive));
                    DeleteFile(p);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new PlanStoreException("could not record the reconciliation of " + p.Id + "; the document stays locked: " + ex.Message, ex);
                }
                lock (_lock)
                {
                    p.Status = PlanStatus.Reconciled;
                    p.StatusDetail = check.Verdict + " by " + author;
                    p.FinishedUtc = _now();
                    p.StorageWarning = null;
                    p.FromDisk = false;
                    _plans[p.Id] = p;
                }
                released = true;
            });
            return released;
        }

        /// <summary>Moves a damaged evidence file to cuarentena\ with a human record. Does not delete it. Only for the manual path with Revit closed.</summary>
        public void QuarantineCorrupt(string docDir, string fileName, string author, string justification)
        {
            if (string.IsNullOrWhiteSpace(justification)) throw new ArgumentException("missing justification");
            if (fileName != Path.GetFileName(fileName) || !fileName.StartsWith("P-", StringComparison.Ordinal)) throw new ArgumentException("invalid file: " + fileName);
            var src = Path.Combine(docDir, fileName);
            var dest = LegacyAliases.ResolveExisting(docDir, Path.Combine(docDir, "quarantine"));
            Directory.CreateDirectory(dest);
            var stamp = _now().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            WriteFile(Path.Combine(dest, fileName + "." + stamp + ".record.json"), Json.Write(new Dictionary<string, object>
            {
                ["utc"] = _now().ToString("o", CultureInfo.InvariantCulture), ["author"] = author, ["justification"] = justification, ["file"] = fileName,
                ["sha256"] = Home.FileSha256(src),
            }));
            File.Move(src, Path.Combine(dest, fileName + "." + stamp));
        }

        /// <summary>Cross-process lock per document. If it is not obtained, nothing is written.</summary>
        private void WithDocLock(string docKey, Action body)
        {
            FileStream handle = null;
            try
            {
                var dir = DocDir(docKey);
                Directory.CreateDirectory(dir);
                var limit = DateTime.UtcNow + LockWait;
                while (handle == null)
                {
                    try { handle = new FileStream(Path.Combine(dir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) when (DateTime.UtcNow < limit) { System.Threading.Thread.Sleep(25); }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new PlanStoreException("could not obtain the document lock: " + ex.Message, ex);
            }
            using (handle) body();
        }

        private static string ReadShared(string path)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(f, Encoding.UTF8)) return r.ReadToEnd();
                }
                catch (IOException) when (attempt < 4) { System.Threading.Thread.Sleep(20 * attempt); }
            }
        }

        /// <summary>
        /// Last value the queue will leave for (element, field), if any held plan touches it.
        /// That way a new plan chains onto the held ones instead of clashing with them on apply.
        /// </summary>
        public bool TryHeldAfter(string docKey, long id, string key, out string after, out string planId)
        {
            after = null; planId = null;
            foreach (var p in Held(docKey))
                foreach (var e in p.Entries)
                    if (e.Id == id && string.Equals(e.Key, key, StringComparison.Ordinal))
                    {
                        after = e.After;
                        planId = p.Id;
                    }
            return planId != null;
        }

        /// <summary>Plans of a session, most recent first.</summary>
        public List<Plan> ForSession(string sessionId, int max = 20)
        {
            lock (_lock)
                return _plans.Values.Where(p => p.SessionId == sessionId)
                    .OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id, StringComparer.Ordinal)
                    .Take(max).ToList();
        }

        /// <summary>Discards all held plans of a document. Returns how many.</summary>
        public int DiscardHeld(string docKey, string detail)
        {
            var list = Held(docKey);
            foreach (var p in list) Finish(p, PlanStatus.Discarded, detail);
            return list.Count;
        }

        /// <summary>Loads the held plans saved on disk for a document (for example, after restarting Revit).</summary>
        public int LoadPersisted(string docKey)
        {
            var dir = DocDir(docKey);
            if (!Directory.Exists(dir)) return 0;
            var n = 0;
            foreach (var f in Directory.GetFiles(dir, "P-*.json"))
            {
                try
                {
                    var p = Plan.FromJson(ReadShared(f));
                    if (p.Status != PlanStatus.Held && p.Status != PlanStatus.InProgress && p.Reconciliation == null) continue;
                    p.FromDisk = true;
                    // An intent without a result never turns into success nor goes back to listo.
                    if (p.Status == PlanStatus.InProgress)
                    {
                        p.Status = Status.Unknown;
                        p.StatusDetail = "the operation started and recorded no result; reconcile before writing";
                        GroupAbort.PreserveInterrupted(p, p.StatusDetail);
                    }
                    lock (_lock)
                    {
                        if (_plans.ContainsKey(p.Id)) continue;
                        _plans[p.Id] = p;
                    }
                    n++;
                }
                catch (Exception) { /* damaged file: kept, and CheckWritable locks the document */ }
            }
            return n;
        }

        private void Persist(Plan p)
        {
            var dir = DocDir(p.DocKey);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, p.Id + ".json");
            WriteFile(file, Json.Write(p));
        }

        private void DeleteFile(Plan p)
        {
            var file = Path.Combine(DocDir(p.DocKey), p.Id + ".json");
            RemoveFile(file);
        }

        /// <summary>Folder per document: short hash of the path (cloud paths have invalid characters).</summary>
        public string DocDir(string docKey)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes((docKey ?? "").ToLowerInvariant()));
                var hex = BitConverter.ToString(h, 0, 6).Replace("-", "").ToLowerInvariant();
                return Path.Combine(_queueRoot, hex);
            }
        }
    }
}
