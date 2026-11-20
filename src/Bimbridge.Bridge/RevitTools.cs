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
                     "With several Revit open it lists 'instancias'; instancia=<pid or version, e.g. 2024> switches to another. Usage details: guide.")]
        public async Task<string> RevitContext(
            McpServer server,
            [Description("pid or version of the Revit to use when several are open (optional)")] string instancia = null,
            [Description("Name of this agent for the BIMBRIDGE panel, e.g. the chat title (optional; register it once)")] string agente = null,
            [Description("true: includes the capability table")] bool? detalle = null,
            CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(instancia))
            {
                var why = await _revit.SwitchToAsync(instancia.Trim(), ct).ConfigureAwait(false);
                if (why != null) return Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = why });
            }
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var ctxArgs = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(agente)) { ctxArgs["agente"] = agente; _revit.AgentName = agente; _revit.AgentSource = "registro"; }
            if (detalle == true) ctxArgs["detalle"] = true;
            var resp = await _revit.CallAsync(Wire.OpContext, ctxArgs, ReadTimeout, ct).ConfigureAwait(false);
            var o = RenderDict(resp);
            var all = RevitConnection.Instances();
            if (all.Count > 1)
            {
                o["instancias"] = all.Select(d => (object)new Dictionary<string, object>
                {
                    ["pid"] = (long)d.Pid, ["revit"] = d.RevitVersion, ["addin"] = d.AddinVersion,
                    ["en_uso"] = _revit.Instance != null && _revit.Instance.Pid == d.Pid,
                }).ToList();
            }
            o["puente"] = Program.Version;
            return Json.Write(o);
        }

        [McpServerTool(Name = "guide", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("On-demand usage guide (does not touch Revit). tema: flujo, consulta, catalogo, geo, escritura, captura, lote. " +
                     "For escritura, op=tag,place… returns only those operations.")]
        public string Guide(
            [Description("flujo (default), consulta, catalogo, geo, escritura, captura or lote")] string tema = null,
            [Description("Only with tema=escritura: comma-separated operations")] string op = null)
            => Guides.Get(tema, op);

        [McpServerTool(Name = "catalog", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Model catalog in one call, table {total, cols, rows}. Details: guide tema=catalogo.")]
        public Task<string> Catalog(
            McpServer server,
            [Description("resumen, categorias, tipos, familias, niveles, vistas, plantillas, laminas, worksets, vinculos or filtros")] string que = null,
            [Description("Categories (types and families), e.g. LightingFixtures")] string cat = null,
            [Description("Levels (views)")] string lvl = null,
            [Description("View type: plano, rcp, seccion, alzado, 3d, leyenda, tabla…")] string tipo = null,
            [Description("Text to search for in any column")] string buscar = null,
            [Description("Extra parameters (views, sheets), e.g. Sub-Discipline,Set Group")] string f = null,
            int? page = null,
            [Description("max 200")] int? page_size = null,
            [Description("ft or mm")] string units = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            void Put(string k, object v) { if (v != null) a[k] = v; }
            Put("que", que); Put("cat", cat); Put("lvl", lvl); Put("tipo", tipo); Put("buscar", buscar); Put("f", f); Put("units", units);
            Put("page", page.HasValue ? (long?)page.Value : null);
            Put("page_size", page_size.HasValue ? (long?)page_size.Value : null);
            return Call(server, Wire.OpCatalog, a, ct);
        }

        [McpServerTool(Name = "show", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Shows elements to the user in Revit: selects them and frames them in the active view (vista=id switches view first). Does not modify the model. " +
                     "If they are not visible in the active view, suggests views where to find them.")]
        public Task<string> Show(
            McpServer server,
            [Description("Comma-separated ids or $set")] string ids = null,
            [Description("View to open first (id, optional)")] long? vista = null,
            [Description("false = do not change the selection")] bool? seleccionar = null,
            [Description("false = do not move the zoom")] bool? zoom = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (ids != null) a["ids"] = ids;
            if (vista != null) a["vista"] = vista;
            if (seleccionar != null) a["seleccionar"] = seleccionar;
            if (zoom != null) a["zoom"] = zoom;
            return Call(server, Wire.OpShow, a, ct);
        }

        [McpServerTool(Name = "capture", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Image of a Revit view so you can SEE it (does not modify the model). colorear=categorias tells apart fixtures (orange), switches (green), receptacles (blue), panels (magenta)… " +
                     "resaltar=$set (or \"$a=rojo;$b=azul\") marks elements; encuadre=ids|$set|resaltado crops to them. Returns the image and a legend of what is visible per category.")]
        public async Task<CallToolResult> Capture(
            McpServer server,
            [Description("View id (default: the active one)")] long? vista = null,
            [Description("categorias = one color per category")] string colorear = null,
            [Description("Ids/$set to highlight; several groups with ; and color with =, e.g. $sin_tag=rojo;$ok=verde")] string resaltar = null,
            [Description("Crop to these ids/$set, or resaltado")] string encuadre = null,
            [Description("Longest side in pixels, 400–2400 (default 1400)")] int? px = null,
            CancellationToken ct = default)
        {
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var a = new Dictionary<string, object>();
            if (vista != null) a["vista"] = vista;
            if (colorear != null) a["colorear"] = colorear;
            if (resaltar != null) a["resaltar"] = resaltar;
            if (encuadre != null) a["encuadre"] = encuadre;
            if (px != null) a["px"] = (long)px.Value;
            var resp = await _revit.CallAsync(Wire.OpCapture, a, CaptureTimeout, ct).ConfigureAwait(false);
            var o = RenderDict(resp);
            var result = new CallToolResult { Content = new List<ContentBlock>() };
            byte[] bytes = null;
            var mime = o.TryGetValue("mime", out var m) ? m as string ?? "image/png" : "image/png";
            if (resp.Estado == Status.Ok && o.TryGetValue("archivo", out var f) && f is string path)
            {
                try { bytes = File.ReadAllBytes(path); }
                catch (Exception ex) { o["aviso"] = "the image was generated but could not be read: " + ex.Message; }
            }
            o.Remove("mime");
            result.Content.Add(new TextContentBlock { Text = Json.Write(o) });
            if (bytes != null) result.Content.Add(ImageContentBlock.FromBytes(bytes, mime));
            result.IsError = resp.Estado != Status.Ok;
            return result;
        }

        [McpServerTool(Name = "lote", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Several reads in ONE call (they run concurrently): llamadas=[{\"tool\":\"query\",\"cat\":\"LightingFixtures\",\"count\":true},{\"tool\":\"catalog\",\"que\":\"niveles\"}]. " +
                     "Accepts revit_context, query, get, catalog, check, geo and sets; max 8. Returns results in the same order.")]
        public async Task<string> Lote(
            McpServer server,
            [Description("List of calls: each with tool and its arguments")] JsonElement llamadas,
            CancellationToken ct = default)
        {
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            object parsed;
            try { parsed = Json.Parse(llamadas.ValueKind == JsonValueKind.String ? llamadas.GetString() : llamadas.GetRawText()); }
            catch (Exception ex) { return Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "llamadas is not valid JSON: " + ex.Message }); }
            if (parsed is Dictionary<string, object> one) parsed = new List<object> { one };
            if (!(parsed is List<object> list) || list.Count == 0)
                return Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "llamadas: list of {tool, …}" });
            if (list.Count > MaxBatch)
                return Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "lote accepts up to " + MaxBatch + " calls" });

            var tasks = list.Select(item => RunBatchItem(item, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return Json.Write(new Dictionary<string, object> { ["resultados"] = results.Cast<object>().ToList() });
        }

        private const int MaxBatch = 8;
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(120);

        private async Task<Dictionary<string, object>> RunBatchItem(object item, CancellationToken ct)
        {
            if (!(item is Dictionary<string, object> d))
                return new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "each call is an object {tool, …}" };
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
                        return new Dictionary<string, object> { ["tool"] = tool, ["estado"] = Status.Invalido, ["aviso"] = "in lote, sets only accepts op=list" };
                    break;
                default:
                    return new Dictionary<string, object> { ["tool"] = tool, ["estado"] = Status.Invalido, ["aviso"] = "lote accepts revit_context, query, get, catalog, check, geo and sets (no writes)" };
            }
            var resp = await _revit.CallAsync(op, args, ReadTimeout, ct).ConfigureAwait(false);
            var o = new Dictionary<string, object> { ["tool"] = tool };
            foreach (var kv in RenderDict(resp)) o[kv.Key] = kv.Value;
            return o;
        }

        private const string DefaultGetFields = "id,cat,fam,type,lvl,ws,elev,mark,host,x,y,z";

        [McpServerTool(Name = "query", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Queries elements without modifying anything: table {total, cols, rows} or {cols, groups} with group_by. Lengths in feet (units=mm). Give cat, ids or view. Details: guide tema=consulta.")]
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
                     "f=* = ALL instance and type parameters (up to 10 elements; shared values once; buscar filters names).")]
        public Task<string> Get(
            McpServer server,
            [Description("Comma-separated ids or $set")] string ids,
            [Description("Fields or parameter names, comma-separated; * = all")] string f = null,
            [Description("Page (from 1)")] int? page = null,
            [Description("ft (default) or mm")] string units = null,
            [Description("untruncated text")] bool? full = null,
            [Description("With f=*: only parameters whose name contains this text")] string buscar = null,
            CancellationToken ct = default)
        {
            if (f != null && f.Trim() == "*")
            {
                var p = new Dictionary<string, object> { ["ids"] = ids, [Wire.ToolArg] = "get" };
                if (units != null) p["units"] = units;
                if (full != null) p["full"] = full;
                if (buscar != null) p["buscar"] = buscar;
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
                     "(saves $sin_tag, $tags_huerfanos, $tags_sobrantes). views=rcp adds, for each lighting ceiling plan (those that already have tags), " +
                     "fixtures, tags and sin_tag_en_vista, estimated by level and without regenerating. exacto=true counts what is really visible but regenerates each view: only with few views (max 12).")]
        public Task<string> Check(
            McpServer server,
            [Description("lighting_audit, cobertura, tabla_audit or vista_audit")] string name,
            [Description("Levels by name, comma-separated (optional)")] string lvl = null,
            [Description("rcp (ceiling plans with fixture tags), rcp_todos, or comma-separated view ids (optional)")] string views = null,
            [Description("true = fixtures really visible per view (slow: max 12 views)")] bool? exacto = null,
            [Description("JSON arguments. lighting_audit: inventory manifest in mm; cobertura: q, modo, id and filters; tabla_audit: tabla, ids_esperados, cantidad_esperada, filtros_esperados, celdas_esperadas; vista_audit: vistas, ids and optional expectations per view-element pair.")] string args = null,
            CancellationToken ct = default)
        {
            Dictionary<string, object> a;
            try { a = args == null ? new Dictionary<string, object>() : Json.ParseObject(args); }
            catch (FormatException ex) { return Task.FromResult(Json.Write(Response.Fail(0, Status.Invalido, ex.Message))); }
            a["name"] = name;
            if (exacto != null) a["exacto"] = exacto;
            if (lvl != null) a["lvl"] = lvl;
            if (views != null) a["views"] = views;
            return Call(server, Wire.OpCheck, a, ct);
        }

        [McpServerTool(Name = "geo", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Real geometry (meshes of the model and its links), without modifying anything. name: montaje (9 points against the bottom face of what is above: ok, empotrada, flotando, sin_superficie), " +
                     "holgura, choques, cerca, muro, nivel_real, duplicados, cuarto, rango_vista, indice. Returns counts, problems first (clearances in inches) and $sets per class for plan. " +
                     "If slow: job_id → job. Repeating without changes is instant. Details: guide tema=geo.")]
        public Task<string> Geo(
            McpServer server,
            [Description("Recipe")] string name,
            [Description("Categories (LightingFixtures)")] string cat = null,
            string lvl = null,
            [Description("Ids or $set")] string ids = null,
            string ws = null,
            [Description("Family or type (contains)")] string type = null,
            [Description("losas, vigas, columnas, rampas, escaleras, muros, cielos, techos or categories")] string contra = null,
            [Description("Links (contains), todos or ninguno")] string vinculos = null,
            bool? anfitrion = null,
            [Description("arriba or abajo")] string lado = null,
            [Description("1, 5 or 9")] int? puntos = null,
            [Description("1/16\" default")] string tol = null,
            [Description("30' default")] string max = null,
            [Description("cerca/muro, e.g. 2'")] string radio = null,
            [Description("rango_vista: rcp, rcp_todos, plantas or ids")] string views = null,
            [Description("cuarto: ambos, cuartos or espacios")] string fuente = null,
            [Description("Subcategories to ignore, e.g. JBox")] string sub_excluir = null,
            [Description("Class to list, or todos")] string solo = null,
            int? page = null,
            int? page_size = null,
            [Description("ft or mm")] string units = null,
            bool? reconstruir = null,
            CancellationToken ct = default,
            [Description("montaje/holgura: source IDs and face point on the host")] bool? referencias = null)
        {
            var a = new Dictionary<string, object> { ["name"] = name };
            void Put(string k, object v) { if (v != null) a[k] = v; }
            Put("cat", cat); Put("lvl", lvl); Put("ids", ids); Put("ws", ws); Put("type", type); Put("contra", contra);
            Put("vinculos", vinculos); Put("anfitrion", anfitrion); Put("lado", lado);
            Put("puntos", puntos.HasValue ? (long?)puntos.Value : null);
            Put("tol", tol); Put("max", max); Put("radio", radio); Put("views", views); Put("fuente", fuente);
            Put("sub_excluir", sub_excluir); Put("solo", solo);
            Put("page", page.HasValue ? (long?)page.Value : null);
            Put("page_size", page_size.HasValue ? (long?)page_size.Value : null);
            Put("units", units); Put("reconstruir", reconstruir); Put("referencias", referencias);
            return Call(server, Wire.OpGeo, a, ct);
        }

        [McpServerTool(Name = "sets", ReadOnly = true, OpenWorld = false)]
        [Description("Sets saved with save_as. op=list (default) or op=drop with name.")]
        public Task<string> Sets(
            McpServer server,
            [Description("list or drop")] string op = "list",
            [Description("Set name, e.g. $l5_perimetro")] string name = null,
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
                     "ids: \"1,2\", $set or @n (what op n created). Arguments: guide tema=escritura op=<op>. job_id → job.")]
        public Task<string> Plan(
            McpServer server,
            [Description("[{\"op\":\"set_param\",\"ids\":\"$x\",\"name\":\"Comments\",\"value\":\"REVISAR\"}]")] JsonElement ops,
            [Description("Short summary for the user (shown in the Revit queue)")] string summary = null,
            [Description("Document dN (default: the active one)")] string doc = null,
            [Description("Authorized task (allows commit after reconnecting)")] string tarea = null,
            CancellationToken ct = default)
        {
            object parsed;
            try { parsed = Json.Parse(ops.ValueKind == JsonValueKind.String ? ops.GetString() : ops.GetRawText()); }
            catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "ops is not valid JSON: " + ex.Message })); }
            if (parsed is Dictionary<string, object> single) parsed = new List<object> { single };
            var a = new Dictionary<string, object> { ["ops"] = parsed };
            if (summary != null) a["summary"] = summary;
            if (doc != null) a[Wire.DocArg] = doc;
            if (tarea != null) a[Wire.TaskArg] = tarea;
            return Call(server, Wire.OpPlan, a, ct);
        }

        [McpServerTool(Name = "commit", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
        [Description("Applies a plan already reviewed by the user. If the user has the queue on, it answers estado=retenido: " +
                     "the change waits for the user to apply it from Revit; do not repeat it. If something changed since the plan: estado=conflicto and nothing is applied. " +
                     "What was created is in $creados (and $creados_n per operation); 'verificacion' rereads what was touched.")]
        public Task<string> Commit(
            McpServer server,
            [Description("plan_id returned by plan")] string plan_id,
            [Description("Idempotency key: the same key is never applied twice")] string clave = null,
            [Description("Authorized task")] string tarea = null,
            [Description("After interrumpido with clave=: no_aplicado | aplicado")] string reconciliar = null,
            [Description("aplicar (default) | ensayo: applies, verifies and ROLLS BACK; it is not a commit")] string modo = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object> { ["plan_id"] = plan_id };
            if (modo != null) a["modo"] = modo;
            if (reconciliar != null) a["reconciliar"] = reconciliar;
            if (clave != null) a[Wire.KeyArg] = clave;
            if (tarea != null) a[Wire.TaskArg] = tarea;
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
            [Description("Start line")] int desde = 0,
            [Description("Number of lines")] int lineas = 300)
        {
            var txt = ResultStore.Read(id, desde, lineas, out var total);
            if (txt == null) return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "result does not exist: " + id }));
            return Task.FromResult(Json.Write(new Dictionary<string, object> { ["texto"] = txt, ["total_lineas"] = (long)total, ["desde"] = (long)desde }));
        }

        [McpServerTool(Name = "exec", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("C# against the public Revit API. Code: expression, body with return, or class with static object Run(ScriptContext ctx). " +
                     "doc, uidoc, app, uiapp, args, ctx already exist. modo=consulta (read-only, no transaction) | ensayo (changes and rolls back; level W) | ejecutar (tx=auto|grupo|ninguna; ninguna for EditFamily/LoadFamily). " +
                     "Returns the value as JSON, Revit warnings and what changed.")]
        public Task<string> Exec(
            McpServer server,
            [Description("C# code")] string codigo = null,
            [Description("consulta | ensayo | ejecutar")] string modo = "consulta",
            [Description("auto | grupo | ninguna")] string tx = null,
            [Description("Document dN")] string doc = null,
            [Description("Arguments (JSON object) → args")] JsonElement? args = null,
            [Description("Saved script to repeat, or 'lista'")] string nombre = null,
            [Description("Save the code under that name")] string guardar = null,
            [Description("Idempotency key")] string clave = null,
            [Description("Authorized task")] string tarea = null,
            [Description(".cs file with the code")] string archivo = null,
            [Description("Extra DLLs, comma-separated")] string refs = null,
            [Description("Other documents it touches: d2,d3")] string docs = null,
            [Description("After interrumpido with clave=: no_aplicado | aplicado")] string reconciliar = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (archivo != null) a["archivo"] = archivo;
            if (refs != null) a["refs"] = refs.Split(',').Select(x => (object)x.Trim()).Where(x => ((string)x).Length > 0).ToList();
            if (docs != null) a["docs"] = docs.Split(',').Select(x => (object)x.Trim()).Where(x => ((string)x).Length > 0).ToList();
            if (reconciliar != null) a["reconciliar"] = reconciliar;
            if (string.Equals(nombre, "lista", StringComparison.OrdinalIgnoreCase) && codigo == null) a["accion"] = "lista";
            else if (nombre != null) a["nombre"] = nombre;
            if (codigo != null) a["codigo"] = codigo;
            if (modo != null) a["modo"] = modo;
            if (tx != null) a["tx"] = tx;
            if (doc != null) a[Wire.DocArg] = doc;
            if (guardar != null) a["guardar"] = guardar;
            if (clave != null) a[Wire.KeyArg] = clave;
            if (tarea != null) a[Wire.TaskArg] = tarea;
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object) a["args"] = Json.Parse(args.Value.GetRawText());
            else if (args.HasValue && args.Value.ValueKind == JsonValueKind.String)
            {
                try { a["args"] = Json.Parse(args.Value.GetString()); }
                catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "args is not JSON: " + ex.Message })); }
            }
            return Call(server, Wire.OpExec, a, ct, WriteTimeout);
        }

        [McpServerTool(Name = "fam", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Families and connectors. accion=info|caras (read) | abrir (EditFamily → dN) | conector (creates or reconciles by name: sistema, cara, radio/diametro with unit, flujo) | " +
                     "param (valor/formula, crear, asociar) | cargar (en=dN, sobrescribir=no|valores). Arguments in 'args' as a JSON object.")]
        public Task<string> Fam(
            McpServer server,
            [Description("info | caras | abrir | conector | param | cargar")] string accion,
            [Description("Arguments, e.g. {\"nombre\":\"fria\",\"sistema\":\"fria\",\"cara\":\"812:3\",\"radio\":\"6.5mm\"}")] JsonElement? args = null,
            [Description("Document dN")] string doc = null,
            [Description("Idempotency key")] string clave = null,
            CancellationToken ct = default)
            => CallWithArgs(server, Wire.OpFam, accion, args, doc, clave, ct, WriteTimeout);

        [McpServerTool(Name = "doc_op", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Document operations: guardar | guardar_como (como=path) | sync (comentario=) | pdf (conjunto= or hojas=, carpeta=, nombre=) | cerrar (guardar=si|descartar=si). " +
                     "Verifies the result (file, hash, pages). Arguments in 'args' as a JSON object.")]
        public Task<string> DocOp(
            McpServer server,
            [Description("guardar | guardar_como | sync | pdf | cerrar")] string accion,
            [Description("Arguments (JSON object)")] JsonElement? args = null,
            [Description("Document dN")] string doc = null,
            [Description("Idempotency key")] string clave = null,
            CancellationToken ct = default)
            => CallWithArgs(server, Wire.OpDoc, accion, args, doc, clave, ct, WriteTimeout);

        private Task<string> CallWithArgs(McpServer server, string op, string accion, JsonElement? args, string doc, string clave, CancellationToken ct, TimeSpan timeout)
        {
            var a = new Dictionary<string, object>();
            if (args.HasValue && args.Value.ValueKind != JsonValueKind.Null && args.Value.ValueKind != JsonValueKind.Undefined)
            {
                object parsed;
                try { parsed = Json.Parse(args.Value.ValueKind == JsonValueKind.String ? args.Value.GetString() : args.Value.GetRawText()); }
                catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "args is not JSON: " + ex.Message })); }
                if (parsed is Dictionary<string, object> d) foreach (var kv in d) a[kv.Key] = kv.Value is string || kv.Value is bool ? kv.Value : Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            a["accion"] = accion;
            if (doc != null) a[Wire.DocArg] = doc;
            if (clave != null) a[Wire.KeyArg] = clave;
            return Call(server, op, a, ct, timeout);
        }

        [McpServerTool(Name = "tarea", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Long resumable tasks: a recipe (class inheriting from Receta, by registered name, .cs file or code) walks units in batches, " +
                     "with checkpoints, verified guardar/sync/pdf barriers and final QC. Any client can continue it by id. " +
                     "accion=iniciar | estado | resumen | esperar (desde=rev, max=s) | unidades | lista | recetas | reanudar | cancelar | reconciliar (decision=aplicada|no_aplicada, claves) | anotar (texto) | olvidar.")]
        public Task<string> Tarea(
            McpServer server,
            [Description("iniciar | estado | resumen | esperar | unidades | lista | recetas | reanudar | cancelar | reconciliar | anotar | olvidar")] string accion = "estado",
            [Description("Task id (on iniciar: optional, so it can be repeated without duplicating)")] string id = null,
            [Description("Registered recipe by name")] string receta = null,
            [Description(".cs file with the recipe")] string archivo = null,
            [Description("Recipe code")] string codigo = null,
            [Description("Recipe arguments (JSON object)")] JsonElement? args = null,
            [Description("Goal in one sentence (task memory)")] string objetivo = null,
            [Description("Options: \"guardar_cada=200 al_final=guardar,pdf en_error=continuar lote_max=200 espera_s=10 desde=N max=30 decision=aplicada claves=a,b texto=...\"")] string opciones = null,
            [Description("Document dN")] string doc = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object> { ["accion"] = accion ?? "estado" };
            if (id != null) a["id"] = id;
            if (receta != null) a["receta"] = receta;
            if (archivo != null) a["archivo"] = archivo;
            if (codigo != null) a["codigo"] = codigo;
            if (objetivo != null) a["objetivo"] = objetivo;
            if (doc != null) a[Wire.DocArg] = doc;
            if (!string.IsNullOrWhiteSpace(opciones))
            {
                var o = Command.Parse("x " + opciones);
                if (o != null) foreach (var kv in o.Args) a[kv.Key == "count" ? "n" : kv.Key] = kv.Value;
            }
            if (args.HasValue && args.Value.ValueKind != JsonValueKind.Null && args.Value.ValueKind != JsonValueKind.Undefined)
            {
                try { a["args"] = Json.Parse(args.Value.ValueKind == JsonValueKind.String ? args.Value.GetString() : args.Value.GetRawText()); }
                catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "args is not JSON: " + ex.Message })); }
            }
            return Call(server, Wire.OpTask, a, ct, TimeSpan.FromSeconds(180));
        }

        [McpServerTool(Name = "job", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Tracking. job_id: waits for the result of a long check (up to ~40 s per call) or op=cancel. " +
                     "plan_id: plan status (listo, retenido, aplicado, conflicto, revertido, vencido, descartado, desconocido, reconciliado) or op=drop. " +
                     "op=pendientes: document lock. op=reconciliar plan_id: compares with the model and releases only if proven. Without ids: list for this conversation.")]
        public Task<string> Job(
            McpServer server,
            [Description("job_id returned by geo or plan (optional)")] string job_id = null,
            [Description("plan_id (optional)")] string plan_id = null,
            [Description("status (default), cancel (job), drop (plan), pendientes or reconciliar")] string op = null,
            [Description("Write attempt ID for a read-only query")] string intento_id = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (job_id != null) a["job_id"] = job_id;
            if (plan_id != null) a["plan_id"] = plan_id;
            if (intento_id != null) a["intento_id"] = intento_id;
            if (op != null) a["op"] = op;
            return Call(server, Wire.OpJob, a, ct);
        }

        private async Task<string> Call(McpServer server, string op, Dictionary<string, object> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await _revit.CallAsync(op, args, timeout ?? ReadTimeout, ct).ConfigureAwait(false);
            var text = Render(resp);
            UsageMetrics.Record(_revit.ClientName, op, op, Json.Write(args ?? new Dictionary<string, object>()).Length, text.Length, sw.ElapsedMilliseconds, resp.Estado);
            return text;
        }

        /// <summary>
        /// Response for the AI: if all went well, the data with the rev; otherwise the status and an actionable warning.
        /// </summary>
        internal static string Render(Response resp) => Json.Write(RenderDict(resp));

        internal static Dictionary<string, object> RenderDict(Response resp)
        {
            var o = new Dictionary<string, object>();
            if (resp.Estado != Status.Ok) o["estado"] = resp.Estado;
            if (resp.Rev.HasValue) o["rev"] = resp.Rev.Value;
            if (resp.Datos is Dictionary<string, object> d) foreach (var kv in d) o[kv.Key] = kv.Value;
            else if (resp.Datos != null) o["datos"] = resp.Datos;
            if (!string.IsNullOrEmpty(resp.Aviso)) o["aviso"] = resp.Aviso;
            return o;
        }
    }
}
