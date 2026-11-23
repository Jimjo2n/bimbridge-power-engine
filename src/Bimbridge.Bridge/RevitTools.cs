// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Bridge
{
    /// <summary>
    /// Tools the AI sees (section 7). Reads + typed writes (plan → commit → job).
    /// Descriptions are short on purpose: they are loaded into the AI context in every session.
    /// None mentions ports, sessions or queues.
    /// </summary>
    [McpServerToolType]
    public sealed class RevitTools
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(180);
        private readonly RevitConnection _revit;

        public RevitTools(RevitConnection revit) { _revit = revit; }

        [McpServerTool(Name = "revit_context", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Revit state: version, active document, user, whether it is modified, worksharing and rev. Use it first. " +
                     "rev counts the changes seen since Revit opened the model (0 = none yet); use it for since. " +
                     "With several Revit open it lists 'instances'; instance=<pid or version, e.g. 2024> switches to another. Usage details: guide.")]
        public async Task<string> RevitContext(
            McpServer server,
            [Description("pid or version of the Revit to use when several are open (optional)")] string instance = null,
            [Description("Name of this agent for the BIMBRIDGE panel, e.g. the chat title (optional; register it once)")] string agent = null,
            [Description("true: includes the capability table")] bool? detail = null,
            CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(instance))
            {
                var why = await _revit.SwitchToAsync(instance.Trim(), ct).ConfigureAwait(false);
                if (why != null) return Json.Write(new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = why });
            }
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var ctxArgs = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(agent)) { ctxArgs["agent"] = agent; _revit.AgentName = agent; _revit.AgentSource = AgentIdentity.SourceRegister; }
            if (detail == true) ctxArgs["detail"] = true;
            var resp = await _revit.CallAsync(Wire.OpContext, ctxArgs, ReadTimeout, ct).ConfigureAwait(false);
            var o = RenderDict(resp);
            var all = RevitConnection.Instances();
            if (all.Count > 1)
            {
                o["instances"] = all.Select(d => (object)new Dictionary<string, object>
                {
                    ["pid"] = (long)d.Pid, ["revit"] = d.RevitVersion, ["addin"] = d.AddinVersion,
                    ["in_use"] = _revit.Instance != null && _revit.Instance.Pid == d.Pid,
                }).ToList();
            }
            o["bridge"] = Program.Version;
            return Json.Write(o);
        }

        [McpServerTool(Name = "guide", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("On-demand usage guide (does not touch Revit). topic: workflow, query, catalog, geo, write, capture, batch. " +
                     "For write, op=tag,place… returns only those operations.")]
        public string Guide(
            [Description("workflow (default), query, catalog, geo, write, capture or batch")] string topic = null,
            [Description("Only with topic=write: comma-separated operations")] string op = null)
            => Guides.Get(topic, op);

        [McpServerTool(Name = "catalog", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Model catalog in one call, table {total, cols, rows}. Details: guide topic=catalog.")]
        public Task<string> Catalog(
            McpServer server,
            [Description("summary, categories, types, families, levels, views, templates, sheets, worksets, links or filters")] string kind = null,
            [Description("Categories (types and families), e.g. LightingFixtures")] string cat = null,
            [Description("Levels (views)")] string lvl = null,
            [Description("View type: plan, rcp, section, elevation, 3d, legend, schedule…")] string type = null,
            [Description("Text to search for in any column")] string search = null,
            [Description("Extra parameters (views, sheets), e.g. Sub-Discipline,Set Group")] string f = null,
            int? page = null,
            [Description("max 200")] int? page_size = null,
            [Description("ft or mm")] string units = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            void Put(string k, object v) { if (v != null) a[k] = v; }
            Put("kind", kind); Put("cat", cat); Put("lvl", lvl); Put("type", type); Put("search", search); Put("f", f); Put("units", units);
            Put("page", page.HasValue ? (long?)page.Value : null);
            Put("page_size", page_size.HasValue ? (long?)page_size.Value : null);
            return Call(server, Wire.OpCatalog, a, ct);
        }

        [McpServerTool(Name = "show", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Shows elements to the user in Revit: selects them and frames them in the active view (view=id switches view first). Does not modify the model. " +
                     "If they are not visible in the active view, suggests views where to find them.")]
        public Task<string> Show(
            McpServer server,
            [Description("Comma-separated ids or $set")] string ids = null,
            [Description("View to open first (id, optional)")] long? view = null,
            [Description("false = do not change the selection")] bool? select = null,
            [Description("false = do not move the zoom")] bool? zoom = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (ids != null) a["ids"] = ids;
            if (view != null) a["view"] = view;
            if (select != null) a["select"] = select;
            if (zoom != null) a["zoom"] = zoom;
            return Call(server, Wire.OpShow, a, ct);
        }

        [McpServerTool(Name = "capture", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Image of a Revit view so you can SEE it (does not modify the model). color_by=categories tells apart fixtures (orange), switches (green), receptacles (blue), panels (magenta)… " +
                     "highlight=$set (or \"$a=red;$b=blue\") marks elements; framing=ids|$set|highlighted crops to them. Returns the image and a legend of what is visible per category.")]
        public async Task<CallToolResult> Capture(
            McpServer server,
            [Description("View id (default: the active one)")] long? view = null,
            [Description("categories = one color per category")] string color_by = null,
            [Description("Ids/$set to highlight; several groups with ; and color with =, e.g. $untagged=red;$ok=green")] string highlight = null,
            [Description("Crop to these ids/$set, or highlighted")] string framing = null,
            [Description("Longest side in pixels, 400–2400 (default 1400)")] int? px = null,
            CancellationToken ct = default)
        {
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var a = new Dictionary<string, object>();
            if (view != null) a["view"] = view;
            if (color_by != null) a["color_by"] = color_by;
            if (highlight != null) a["highlight"] = highlight;
            if (framing != null) a["framing"] = framing;
            if (px != null) a["px"] = (long)px.Value;
            var resp = await _revit.CallAsync(Wire.OpCapture, a, CaptureTimeout, ct).ConfigureAwait(false);
            var o = RenderDict(resp);
            var result = new CallToolResult { Content = new List<ContentBlock>() };
            byte[] bytes = null;
            var mime = o.TryGetValue("mime", out var m) ? m as string ?? "image/png" : "image/png";
            if (resp.State == Status.Ok && o.TryGetValue("file", out var f) && f is string path)
            {
                try { bytes = File.ReadAllBytes(path); }
                catch (Exception ex) { o["warning"] = "the image was generated but could not be read: " + ex.Message; }
            }
            o.Remove("mime");
            result.Content.Add(new TextContentBlock { Text = Json.Write(o) });
            if (bytes != null) result.Content.Add(ImageContentBlock.FromBytes(bytes, mime));
            result.IsError = resp.State != Status.Ok;
            return result;
        }

        [McpServerTool(Name = "batch", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Several reads in ONE call (they run concurrently): calls=[{\"tool\":\"query\",\"cat\":\"LightingFixtures\",\"count\":true},{\"tool\":\"catalog\",\"kind\":\"levels\"}]. " +
                     "Accepts revit_context, query, get, catalog, check, geo and sets; max 8. Returns results in the same order.")]
        public async Task<string> Batch(
            McpServer server,
            [Description("List of calls: each with tool and its arguments")] JsonElement calls,
            CancellationToken ct = default)
        {
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            object parsed;
            try { parsed = Json.Parse(calls.ValueKind == JsonValueKind.String ? calls.GetString() : calls.GetRawText()); }
            catch (Exception ex) { return Json.Write(new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = "calls is not valid JSON: " + ex.Message }); }
            if (parsed is Dictionary<string, object> one) parsed = new List<object> { one };
            if (!(parsed is List<object> list) || list.Count == 0)
                return Json.Write(new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = "calls: list of {tool, …}" });
            if (list.Count > MaxBatch)
                return Json.Write(new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = "batch accepts up to " + MaxBatch + " calls" });

            var tasks = list.Select(item => RunBatchItem(item, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return Json.Write(new Dictionary<string, object> { ["results"] = results.Cast<object>().ToList() });
        }

        private const int MaxBatch = 8;
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(120);

        private async Task<Dictionary<string, object>> RunBatchItem(object item, CancellationToken ct)
        {
            if (!(item is Dictionary<string, object> d))
                return new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = "each call is an object {tool, …}" };
            var tool = (Arg.GetString(d, "tool") ?? "").Trim().ToLowerInvariant();
            var args = new Dictionary<string, object>(d, StringComparer.Ordinal);
            args.Remove("tool");
            string op;
            switch (tool)
            {
                case "revit_context": op = Wire.OpContext; break;
                case "query": op = Wire.OpQuery; break;
                case "get":
                    op = string.Equals(Arg.GetString(args, "f"), "*", StringComparison.Ordinal) ? Wire.OpParams : Wire.OpQuery;
                    if (op == Wire.OpQuery && !args.ContainsKey("f")) args["f"] = DefaultGetFields;
                    if (op == Wire.OpParams) args.Remove("f");
                    args[Wire.ToolArg] = "get";
                    break;
                case "catalog": op = Wire.OpCatalog; break;
                case "check": op = Wire.OpCheck; break;
                case "geo": op = Wire.OpGeo; break;
                case "sets":
                    op = Wire.OpSets;
                    if (!string.Equals(Arg.GetString(args, "op") ?? "list", "list", StringComparison.OrdinalIgnoreCase))
                        return new Dictionary<string, object> { ["tool"] = tool, ["status"] = Status.Invalid, ["warning"] = "in batch, sets only accepts op=list" };
                    break;
                default:
                    return new Dictionary<string, object> { ["tool"] = tool, ["status"] = Status.Invalid, ["warning"] = "batch accepts revit_context, query, get, catalog, check, geo and sets (no writes)" };
            }
            var resp = await _revit.CallAsync(op, args, ReadTimeout, ct).ConfigureAwait(false);
            var o = new Dictionary<string, object> { ["tool"] = tool };
            foreach (var kv in RenderDict(resp)) o[kv.Key] = kv.Value;
            return o;
        }

        private const string DefaultGetFields = "id,cat,fam,type,lvl,ws,elev,mark,host,x,y,z";

        [McpServerTool(Name = "query", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Queries elements without modifying anything: table {total, cols, rows} or {cols, groups} with group_by. Lengths in feet (units=mm). Give cat, ids or view. Details: guide topic=query.")]
        public Task<string> Query(
            McpServer server,
            [Description("Categories, e.g. LightingFixtures,LightingDevices")] string cat = null,
            [Description("Levels by name, e.g. LEVEL05")] string lvl = null,
            [Description("Filter by family or type (contains)")] string type = null,
            [Description("Worksets by name")] string ws = null,
            [Description("Ids \"1,2\" or $set")] string ids = null,
            [Description("Only elements visible in this view (id)")] long? view = null,
            [Description("Conditions: field=value, !=, >, <, >=, <=, ~ (contains), field? (not empty), !field (empty)")] string[] where = null,
            [Description("Fields (default id,fam,type,lvl): cat,ws,elev,mark,comments,host,x,y,z or a parameter")] string f = null,
            [Description("Group and count by these fields (max 3)")] string group_by = null,
            [Description("true = total only")] bool? count = null,
            int? page = null,
            [Description("max 200")] int? page_size = null,
            [Description("Save ids as $name")] string save_as = null,
            [Description("Only elements changed after this rev")] long? since = null,
            [Description("ft or mm (elev, x, y, z, where)")] string units = null,
            [Description("untruncated text")] bool? full = null,
            [Description("Cursor returned by the first page: with page=N it serves the next ones without rereading the model (rejected if the document changed)")] string cursor = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (cursor != null) a["cursor"] = cursor;
            if (units != null) a["units"] = units;
            if (full != null) a["full"] = full;
            void Put(string k, object v) { if (v != null) a[k] = v; }
            Put("cat", cat); Put("lvl", lvl); Put("type", type); Put("ws", ws); Put("ids", ids);
            Put("view", view); Put("f", f); Put("group_by", group_by); Put("count", count);
            Put("page", page.HasValue ? (long?)page.Value : null);
            Put("page_size", page_size.HasValue ? (long?)page_size.Value : null);
            Put("save_as", save_as); Put("since", since);
            if (where != null && where.Length > 0) a["where"] = new List<object>(where);
            return Call(server, Wire.OpQuery, a, ct);
        }

        [McpServerTool(Name = "get", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Details of specific elements: ids or $set. Default id,cat,fam,type,lvl,ws,elev,mark,host,x,y,z; f to request other parameters by name. " +
                     "f=* = ALL instance and type parameters (up to 10 elements; shared values once; search filters names).")]
        public Task<string> Get(
            McpServer server,
            [Description("Comma-separated ids or $set")] string ids,
            [Description("Fields or parameter names, comma-separated; * = all")] string f = null,
            [Description("Page (from 1)")] int? page = null,
            [Description("ft (default) or mm")] string units = null,
            [Description("untruncated text")] bool? full = null,
            [Description("With f=*: only parameters whose name contains this text")] string search = null,
            CancellationToken ct = default)
        {
            if (f != null && f.Trim() == "*")
            {
                var p = new Dictionary<string, object> { ["ids"] = ids, [Wire.ToolArg] = "get" };
                if (units != null) p["units"] = units;
                if (full != null) p["full"] = full;
                if (search != null) p["search"] = search;
                return Call(server, Wire.OpParams, p, ct);
            }
            var a = new Dictionary<string, object>
            {
                ["ids"] = ids,
                ["f"] = f ?? DefaultGetFields,
                [Wire.ToolArg] = "get",
            };
            if (page.HasValue) a["page"] = (long)page.Value;
            if (units != null) a["units"] = units;
            if (full != null) a["full"] = full;
            return Call(server, Wire.OpQuery, a, ct);
        }

        [McpServerTool(Name = "check", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Complete checks in one call. name=lighting_audit: fixtures by level, workset and type; untagged, orphan tags and duplicates per view " +
                     "(saves $untagged, $orphan_tags, $leftover_tags). views=rcp adds, for each lighting ceiling plan (those that already have tags), " +
                     "fixtures, tags and untagged_in_view, estimated by level and without regenerating. exact=true counts what is really visible but regenerates each view: only with few views (max 12).")]
        public Task<string> Check(
            McpServer server,
            [Description("lighting_audit, coverage, schedule_audit or view_audit")] string name,
            [Description("Levels by name, comma-separated (optional)")] string lvl = null,
            [Description("rcp (ceiling plans with fixture tags), rcp_all, or comma-separated view ids (optional)")] string views = null,
            [Description("true = fixtures really visible per view (slow: max 12 views)")] bool? exact = null,
            [Description("JSON arguments. lighting_audit: inventory manifest in mm; coverage: q, mode, id and filters; schedule_audit: schedule, expected_ids, expected_count, expected_filters, expected_cells; view_audit: views, ids and optional expectations per view-element pair.")] string args = null,
            CancellationToken ct = default)
        {
            Dictionary<string, object> a;
            try { a = args == null ? new Dictionary<string, object>() : Json.ParseObject(args); }
            catch (FormatException ex) { return Task.FromResult(Json.Write(Response.Fail(0, Status.Invalid, ex.Message))); }
            a["name"] = name;
            if (exact != null) a["exact"] = exact;
            if (lvl != null) a["lvl"] = lvl;
            if (views != null) a["views"] = views;
            return Call(server, Wire.OpCheck, a, ct);
        }

        [McpServerTool(Name = "geo", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Real geometry (meshes of the model and its links), without modifying anything. name: mounting (9 points against the bottom face of what is above: ok, embedded, floating, no_surface), " +
                     "clearance, clashes, near, wall, actual_level, duplicates, room, view_range, index. Returns counts, problems first (clearances in inches) and $sets per class for plan. " +
                     "If slow: job_id → job. Repeating without changes is instant. Details: guide topic=geo.")]
        public Task<string> Geo(
            McpServer server,
            [Description("Recipe")] string name,
            [Description("Categories (LightingFixtures)")] string cat = null,
            string lvl = null,
            [Description("Ids or $set")] string ids = null,
            string ws = null,
            [Description("Family or type (contains)")] string type = null,
            [Description("slabs, beams, columns, ramps, stairs, walls, ceilings, roofs or categories")] string against = null,
            [Description("Links (contains), all or none")] string links = null,
            bool? host = null,
            [Description("up or down")] string side = null,
            [Description("1, 5 or 9")] int? points = null,
            [Description("1/16\" default")] string tol = null,
            [Description("30' default")] string max = null,
            [Description("near/wall, e.g. 2'")] string radius = null,
            [Description("view_range: rcp, rcp_all, floor_plans or ids")] string views = null,
            [Description("room: both, rooms or spaces")] string source = null,
            [Description("Subcategories to ignore, e.g. JBox")] string sub_exclude = null,
            [Description("Class to list, or all")] string only = null,
            int? page = null,
            int? page_size = null,
            [Description("ft or mm")] string units = null,
            bool? reconstruir = null,
            CancellationToken ct = default,
            [Description("mounting/clearance: source IDs and face point on the host")] bool? references = null)
        {
            var a = new Dictionary<string, object> { ["name"] = name };
            void Put(string k, object v) { if (v != null) a[k] = v; }
            Put("cat", cat); Put("lvl", lvl); Put("ids", ids); Put("ws", ws); Put("type", type); Put("against", against);
            Put("links", links); Put("host", host); Put("side", side);
            Put("points", points.HasValue ? (long?)points.Value : null);
            Put("tol", tol); Put("max", max); Put("radius", radius); Put("views", views); Put("source", source);
            Put("sub_exclude", sub_exclude); Put("only", only);
            Put("page", page.HasValue ? (long?)page.Value : null);
            Put("page_size", page_size.HasValue ? (long?)page_size.Value : null);
            Put("units", units); Put("rebuild", reconstruir); Put("references", references);
            return Call(server, Wire.OpGeo, a, ct);
        }

        [McpServerTool(Name = "sets", ReadOnly = true, OpenWorld = false)]
        [Description("Sets saved with save_as. op=list (default) or op=drop with name.")]
        public Task<string> Sets(
            McpServer server,
            [Description("list or drop")] string op = "list",
            [Description("Set name, e.g. $l5_perimeter")] string name = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object> { ["op"] = op ?? "list" };
            if (name != null) a["name"] = name;
            return Call(server, Wire.OpSets, a, ct);
        }

        [McpServerTool(Name = "plan", ReadOnly = true, OpenWorld = false)]
        [Description("Prepares changes WITHOUT applying them and returns the diff (before → after) with plan_id. Typed operations, max 200, " +
                     "one plan = one Undo. ops: set_param, set_values, set_type_param, change_type, set_elevation, set_workset, set_level, " +
                     "snap_to_surface, move, rotate, mirror, copy, delete, place, " +
                     "tag, tag_move, group_create, ungroup, group_place, view_create, " +
                     "view_set, view_hide, view_override, sheet_create, sheet_place, text_note, filled_region. " +
                     "ids: \"1,2\", $set or @n (what op n created). Arguments: guide topic=write op=<op>. job_id → job.")]
        public Task<string> Plan(
            McpServer server,
            [Description("[{\"op\":\"set_param\",\"ids\":\"$x\",\"name\":\"Comments\",\"value\":\"REVIEW\"}]")] JsonElement ops,
            [Description("Short summary for the user (shown in the Revit queue)")] string summary = null,
            [Description("Document dN (default: the active one)")] string doc = null,
            [Description("Authorized task (allows commit after reconnecting)")] string task = null,
            CancellationToken ct = default)
        {
            object parsed;
            try { parsed = Json.Parse(ops.ValueKind == JsonValueKind.String ? ops.GetString() : ops.GetRawText()); }
            catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = "ops is not valid JSON: " + ex.Message })); }
            if (parsed is Dictionary<string, object> single) parsed = new List<object> { single };
            var a = new Dictionary<string, object> { ["ops"] = parsed };
            if (summary != null) a["summary"] = summary;
            if (doc != null) a[Wire.DocArg] = doc;
            if (task != null) a[Wire.TaskArg] = task;
            return Call(server, Wire.OpPlan, a, ct);
        }

        [McpServerTool(Name = "commit", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
        [Description("Applies a plan already reviewed by the user. If the user has the queue on, it answers status=held: " +
                     "the change waits for the user to apply it from Revit; do not repeat it. If something changed since the plan: status=conflict and nothing is applied. " +
                     "What was created is in $created (and $created_n per operation); 'verification' rereads what was touched.")]
        public Task<string> Commit(
            McpServer server,
            [Description("plan_id returned by plan")] string plan_id,
            [Description("Idempotency key: the same key is never applied twice")] string key = null,
            [Description("Authorized task")] string task = null,
            [Description("After interrupted with key=: not_applied | applied")] string reconcile = null,
            [Description("apply (default) | trial: applies, verifies and ROLLS BACK; it is not a commit")] string mode = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object> { ["plan_id"] = plan_id };
            if (mode != null) a["mode"] = mode;
            if (reconcile != null) a["reconcile"] = reconcile;
            if (key != null) a[Wire.KeyArg] = key;
            if (task != null) a[Wire.TaskArg] = task;
            return Call(server, Wire.OpCommit, a, ct, WriteTimeout);
        }

        [McpServerTool(Name = "docs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Documents open in this Revit (projects and families) with their dN handle, type, path, whether modified, and last save/sync.")]
        public Task<string> Docs(McpServer server, CancellationToken ct = default)
            => Call(server, Wire.OpDocs, new Dictionary<string, object>(), ct);

        [McpServerTool(Name = "res", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Reads a saved long result (id r… or b…) in line ranges.")]
        public Task<string> Res(
            [Description("Result id")] string id,
            [Description("Start line")] int from = 0,
            [Description("Number of lines")] int lines = 300)
        {
            var txt = ResultStore.Read(id, from, lines, out var total);
            if (txt == null) return Task.FromResult(Json.Write(new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = "result does not exist: " + id }));
            return Task.FromResult(Json.Write(new Dictionary<string, object> { ["text"] = txt, ["total_lines"] = (long)total, ["from"] = (long)from }));
        }

        [McpServerTool(Name = "exec", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("C# against the public Revit API. Code: expression, body with return, or class with static object Run(ScriptContext ctx). " +
                     "doc, uidoc, app, uiapp, args, ctx already exist. mode=query (read-only, no transaction) | trial (changes and rolls back; level W) | run (tx=auto|group|none; none for EditFamily/LoadFamily). " +
                     "Returns the value as JSON, Revit warnings and what changed.")]
        public Task<string> Exec(
            McpServer server,
            [Description("C# code")] string code = null,
            [Description("query | trial | run")] string mode = "query",
            [Description("auto | group | none")] string tx = null,
            [Description("Document dN")] string doc = null,
            [Description("Arguments (JSON object) → args")] JsonElement? args = null,
            [Description("Saved script to repeat, or 'list'")] string name = null,
            [Description("Save the code under that name")] string save_as = null,
            [Description("Idempotency key")] string key = null,
            [Description("Authorized task")] string task = null,
            [Description(".cs file with the code")] string file = null,
            [Description("Extra DLLs, comma-separated")] string refs = null,
            [Description("Other documents it touches: d2,d3")] string docs = null,
            [Description("After interrupted with key=: not_applied | applied")] string reconcile = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (file != null) a["file"] = file;
            if (refs != null) a["refs"] = refs.Split(',').Select(x => (object)x.Trim()).Where(x => ((string)x).Length > 0).ToList();
            if (docs != null) a["docs"] = docs.Split(',').Select(x => (object)x.Trim()).Where(x => ((string)x).Length > 0).ToList();
            if (reconcile != null) a["reconcile"] = reconcile;
            if (string.Equals(name, "list", StringComparison.OrdinalIgnoreCase) && code == null) a["action"] = "list";
            else if (name != null) a["name"] = name;
            if (code != null) a["code"] = code;
            if (mode != null) a["mode"] = mode;
            if (tx != null) a["tx"] = tx;
            if (doc != null) a[Wire.DocArg] = doc;
            if (save_as != null) a["save"] = save_as;
            if (key != null) a[Wire.KeyArg] = key;
            if (task != null) a[Wire.TaskArg] = task;
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object) a["args"] = Json.Parse(args.Value.GetRawText());
            else if (args.HasValue && args.Value.ValueKind == JsonValueKind.String)
            {
                try { a["args"] = Json.Parse(args.Value.GetString()); }
                catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = "args is not JSON: " + ex.Message })); }
            }
            return Call(server, Wire.OpExec, a, ct, WriteTimeout);
        }

        [McpServerTool(Name = "fam", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Families and connectors. action=info|faces|search (read) | open (EditFamily → dN) | connector (creates or reconciles by name: system, face, radius/diameter with unit, flow) | " +
                     "param (value/formula, create, associate) | load (into=dN, overwrite=no|values). Arguments in 'args' as a JSON object.")]
        public Task<string> Fam(
            McpServer server,
            [Description("info | faces | search | open | connector | param | load")] string action,
            [Description("Arguments, e.g. {\"name\":\"cold\",\"system\":\"cold\",\"face\":\"812:3\",\"radius\":\"6.5mm\"}")] JsonElement? args = null,
            [Description("Document dN")] string doc = null,
            [Description("Idempotency key")] string key = null,
            CancellationToken ct = default)
            => CallWithArgs(server, Wire.OpFam, action, args, doc, key, ct, WriteTimeout);

        [McpServerTool(Name = "doc_op", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Document operations: save | save_as (as=path) | sync (comment=) | pdf (sheet_set= or sheets=, folder=, name=) | close (save=yes|discard=yes). " +
                     "Verifies the result (file, hash, pages). Arguments in 'args' as a JSON object.")]
        public Task<string> DocOp(
            McpServer server,
            [Description("save | save_as | sync | pdf | close")] string action,
            [Description("Arguments (JSON object)")] JsonElement? args = null,
            [Description("Document dN")] string doc = null,
            [Description("Idempotency key")] string key = null,
            CancellationToken ct = default)
            => CallWithArgs(server, Wire.OpDoc, action, args, doc, key, ct, WriteTimeout);

        private Task<string> CallWithArgs(McpServer server, string op, string action, JsonElement? args, string doc, string key, CancellationToken ct, TimeSpan timeout)
        {
            var a = new Dictionary<string, object>();
            if (args.HasValue && args.Value.ValueKind != JsonValueKind.Null && args.Value.ValueKind != JsonValueKind.Undefined)
            {
                object parsed;
                try { parsed = Json.Parse(args.Value.ValueKind == JsonValueKind.String ? args.Value.GetString() : args.Value.GetRawText()); }
                catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = "args is not JSON: " + ex.Message })); }
                if (parsed is Dictionary<string, object> d) foreach (var kv in d) a[kv.Key] = kv.Value is string || kv.Value is bool ? kv.Value : Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            a["action"] = action;
            if (doc != null) a[Wire.DocArg] = doc;
            if (key != null) a[Wire.KeyArg] = key;
            return Call(server, op, a, ct, timeout);
        }

        [McpServerTool(Name = "task", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Long resumable tasks: a recipe (class inheriting from Recipe, by registered name, .cs file or code) walks units in batches, " +
                     "with checkpoints, verified save/sync/pdf barriers and final QC. Any client can continue it by id. " +
                     "action=start | status | summary | wait (from=rev, max=s) | units | list | recipes | resume | cancel | reconcile (decision=applied|not_applied, keys) | note (text) | forget.")]
        public Task<string> TaskTool(
            McpServer server,
            [Description("start | status | summary | wait | units | list | recipes | resume | cancel | reconcile | note | forget")] string action = "status",
            [Description("Task id (on start: optional, so it can be repeated without duplicating)")] string id = null,
            [Description("Registered recipe by name")] string recipe = null,
            [Description(".cs file with the recipe")] string file = null,
            [Description("Recipe code")] string code = null,
            [Description("Recipe arguments (JSON object)")] JsonElement? args = null,
            [Description("Goal in one sentence (task memory)")] string target = null,
            [Description("Options: \"save_every=200 at_end=save,pdf on_error=continue batch_max=200 wait_s=10 from=N max=30 decision=applied keys=a,b text=...\"")] string options = null,
            [Description("Document dN")] string doc = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object> { ["action"] = action ?? "status" };
            if (id != null) a["id"] = id;
            if (recipe != null) a["recipe"] = recipe;
            if (file != null) a["file"] = file;
            if (code != null) a["code"] = code;
            if (target != null) a["target"] = target;
            if (doc != null) a[Wire.DocArg] = doc;
            if (!string.IsNullOrWhiteSpace(options))
            {
                var o = Command.Parse("x " + options);
                if (o != null) foreach (var kv in o.Args) a[kv.Key == "count" ? "n" : kv.Key] = kv.Value;
            }
            if (args.HasValue && args.Value.ValueKind != JsonValueKind.Null && args.Value.ValueKind != JsonValueKind.Undefined)
            {
                try { a["args"] = Json.Parse(args.Value.ValueKind == JsonValueKind.String ? args.Value.GetString() : args.Value.GetRawText()); }
                catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["status"] = Status.Invalid, ["warning"] = "args is not JSON: " + ex.Message })); }
            }
            return Call(server, Wire.OpTask, a, ct, TimeSpan.FromSeconds(180));
        }

        [McpServerTool(Name = "job", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Tracking. job_id: waits for the result of a long check (up to ~40 s per call) or op=cancel. " +
                     "plan_id: plan status (ready, held, applied, conflict, reverted, expired, discarded, unknown, reconciled) or op=drop. " +
                     "op=pending_items: document lock. op=reconcile plan_id: compares with the model and releases only if proven. Without ids: list for this conversation.")]
        public Task<string> Job(
            McpServer server,
            [Description("job_id returned by geo or plan (optional)")] string job_id = null,
            [Description("plan_id (optional)")] string plan_id = null,
            [Description("status (default), cancel (job), drop (plan), pending or reconcile")] string op = null,
            [Description("Write attempt ID for a read-only query")] string attempt_id = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (job_id != null) a["job_id"] = job_id;
            if (plan_id != null) a["plan_id"] = plan_id;
            if (attempt_id != null) a["attempt_id"] = attempt_id;
            if (op != null) a["op"] = op;
            return Call(server, Wire.OpJob, a, ct);
        }

        private async Task<string> Call(McpServer server, string op, Dictionary<string, object> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await _revit.CallAsync(op, args, timeout ?? ReadTimeout, ct).ConfigureAwait(false);
            var text = Render(resp);
            UsageMetrics.Record(_revit.ClientName, op, op, Json.Write(args ?? new Dictionary<string, object>()).Length, text.Length, sw.ElapsedMilliseconds, resp.State);
            return text;
        }

        /// <summary>
        /// Response for the AI: if all went well, the data with the rev; otherwise the status and an actionable warning.
        /// </summary>
        internal static string Render(Response resp) => Json.Write(RenderDict(resp));

        internal static Dictionary<string, object> RenderDict(Response resp)
        {
            var o = new Dictionary<string, object>();
            if (resp.State != Status.Ok) o["status"] = resp.State;
            if (resp.Rev.HasValue) o["rev"] = resp.Rev.Value;
            if (resp.Data is Dictionary<string, object> d) foreach (var kv in d) o[kv.Key] = kv.Value;
            else if (resp.Data != null) o["data"] = resp.Data;
            if (!string.IsNullOrEmpty(resp.Warning)) o["warning"] = resp.Warning;
            return o;
        }
    }
}
