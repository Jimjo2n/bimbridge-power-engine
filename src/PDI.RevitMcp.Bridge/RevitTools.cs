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
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Bridge
{
    /// <summary>
    /// Herramientas que ve la IA (sección 7). Lectura + escritura tipada (plan → commit → job).
    /// Las descripciones son cortas a propósito: se cargan en el contexto de la IA en cada sesión.
    /// Ninguna menciona puertos, sesiones ni colas.
    /// </summary>
    [McpServerToolType]
    public sealed class RevitTools
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(180);
        private readonly RevitConnection _revit;

        public RevitTools(RevitConnection revit) { _revit = revit; }

        [McpServerTool(Name = "revit_context", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Estado de Revit: versión, documento activo, usuario, si está modificado, worksharing y rev. Úsela primero. " +
                     "rev cuenta los cambios vistos desde que Revit abrió el modelo (0 = ninguno todavía); sirve para since. " +
                     "Con varios Revit abiertos lista 'instancias'; instancia=<pid o versión, p. ej. 2024> cambia a otro. Detalle de uso: guide.")]
        public async Task<string> RevitContext(
            McpServer server,
            [Description("pid o versión del Revit a usar cuando hay varios abiertos (opcional)")] string instancia = null,
            [Description("Nombre de este agente para el panel de BIMBRIDGE, p. ej. el título del chat (opcional; regístrelo una vez)")] string agente = null,
            [Description("true: incluye la tabla de capacidades")] bool? detalle = null,
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
        [Description("Guía de uso bajo demanda (no toca Revit). tema: flujo, consulta, catalogo, geo, escritura, captura, lote. " +
                     "Para escritura, op=tag,place… devuelve sólo esas operaciones.")]
        public string Guide(
            [Description("flujo (por defecto), consulta, catalogo, geo, escritura, captura o lote")] string tema = null,
            [Description("Sólo con tema=escritura: operaciones separadas por coma")] string op = null)
            => Guides.Get(tema, op);

        [McpServerTool(Name = "catalog", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Catálogo del modelo en una llamada, tabla {total, cols, rows}. Detalle: guide tema=catalogo.")]
        public Task<string> Catalog(
            McpServer server,
            [Description("resumen, categorias, tipos, familias, niveles, vistas, plantillas, laminas, worksets, vinculos o filtros")] string que = null,
            [Description("Categorías (tipos y familias), p. ej. LightingFixtures")] string cat = null,
            [Description("Niveles (vistas)")] string lvl = null,
            [Description("Tipo de vista: plano, rcp, seccion, alzado, 3d, leyenda, tabla…")] string tipo = null,
            [Description("Texto a buscar en cualquier columna")] string buscar = null,
            [Description("Parámetros extra (vistas, láminas), p. ej. Sub-Discipline,Set Group")] string f = null,
            int? page = null,
            [Description("máx. 200")] int? page_size = null,
            [Description("ft o mm")] string units = null,
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
        [Description("Muestra elementos al usuario en Revit: los selecciona y encuadra en la vista activa (vista=id cambia antes de vista). No modifica el modelo. " +
                     "Si no se ven en la vista activa, sugiere vistas donde buscarlos.")]
        public Task<string> Show(
            McpServer server,
            [Description("Ids separados por coma o $conjunto")] string ids = null,
            [Description("Vista a abrir antes (id, opcional)")] long? vista = null,
            [Description("false = no cambiar la selección")] bool? seleccionar = null,
            [Description("false = no mover el zoom")] bool? zoom = null,
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
        [Description("Imagen de una vista de Revit para VERLA (no modifica el modelo). colorear=categorias distingue luminarias (naranja), interruptores (verde), tomas (azul), tableros (magenta)… " +
                     "resaltar=$conjunto (o \"$a=rojo;$b=azul\") marca elementos; encuadre=ids|$conjunto|resaltado recorta a ellos. Devuelve la imagen y una leyenda con lo visible por categoría.")]
        public async Task<CallToolResult> Capture(
            McpServer server,
            [Description("Id de la vista (por defecto la activa)")] long? vista = null,
            [Description("categorias = un color por categoría")] string colorear = null,
            [Description("Ids/$conjunto a resaltar; varios grupos con ; y color con =, p. ej. $sin_tag=rojo;$ok=verde")] string resaltar = null,
            [Description("Recortar a estos ids/$conjunto, o resaltado")] string encuadre = null,
            [Description("Lado mayor en píxeles, 400–2400 (por defecto 1400)")] int? px = null,
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
                catch (Exception ex) { o["aviso"] = "la imagen se generó pero no se pudo leer: " + ex.Message; }
            }
            o.Remove("mime");
            result.Content.Add(new TextContentBlock { Text = Json.Write(o) });
            if (bytes != null) result.Content.Add(ImageContentBlock.FromBytes(bytes, mime));
            result.IsError = resp.Estado != Status.Ok;
            return result;
        }

        [McpServerTool(Name = "lote", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Varias lecturas en UNA llamada (se ejecutan a la vez): llamadas=[{\"tool\":\"query\",\"cat\":\"LightingFixtures\",\"count\":true},{\"tool\":\"catalog\",\"que\":\"niveles\"}]. " +
                     "Admite revit_context, query, get, catalog, check, geo y sets; máx. 8. Devuelve resultados en el mismo orden.")]
        public async Task<string> Lote(
            McpServer server,
            [Description("Lista de llamadas: cada una con tool y sus argumentos")] JsonElement llamadas,
            CancellationToken ct = default)
        {
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            object parsed;
            try { parsed = Json.Parse(llamadas.ValueKind == JsonValueKind.String ? llamadas.GetString() : llamadas.GetRawText()); }
            catch (Exception ex) { return Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "llamadas no es JSON válido: " + ex.Message }); }
            if (parsed is Dictionary<string, object> one) parsed = new List<object> { one };
            if (!(parsed is List<object> list) || list.Count == 0)
                return Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "llamadas: lista de {tool, …}" });
            if (list.Count > MaxBatch)
                return Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "lote admite hasta " + MaxBatch + " llamadas" });

            var tasks = list.Select(item => RunBatchItem(item, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return Json.Write(new Dictionary<string, object> { ["resultados"] = results.Cast<object>().ToList() });
        }

        private const int MaxBatch = 8;
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(120);

        private async Task<Dictionary<string, object>> RunBatchItem(object item, CancellationToken ct)
        {
            if (!(item is Dictionary<string, object> d))
                return new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "cada llamada es un objeto {tool, …}" };
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
                        return new Dictionary<string, object> { ["tool"] = tool, ["estado"] = Status.Invalido, ["aviso"] = "en lote, sets sólo admite op=list" };
                    break;
                default:
                    return new Dictionary<string, object> { ["tool"] = tool, ["estado"] = Status.Invalido, ["aviso"] = "lote admite revit_context, query, get, catalog, check, geo y sets (no escritura)" };
            }
            var resp = await _revit.CallAsync(op, args, ReadTimeout, ct).ConfigureAwait(false);
            var o = new Dictionary<string, object> { ["tool"] = tool };
            foreach (var kv in RenderDict(resp)) o[kv.Key] = kv.Value;
            return o;
        }

        private const string DefaultGetFields = "id,cat,fam,type,lvl,ws,elev,mark,host,x,y,z";

        [McpServerTool(Name = "query", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Consulta elementos sin modificar nada: tabla {total, cols, rows} o {cols, groups} con group_by. Longitudes en pies (units=mm). Indique cat, ids o view. Detalle: guide tema=consulta.")]
        public Task<string> Query(
            McpServer server,
            [Description("Categorías, p. ej. LightingFixtures,LightingDevices")] string cat = null,
            [Description("Niveles por nombre, p. ej. LEVEL05")] string lvl = null,
            [Description("Filtro por familia o tipo (contiene)")] string type = null,
            [Description("Worksets por nombre")] string ws = null,
            [Description("Ids \"1,2\" o $conjunto")] string ids = null,
            [Description("Sólo elementos visibles en esta vista (id)")] long? view = null,
            [Description("Condiciones: campo=valor, !=, >, <, >=, <=, ~ (contiene), campo? (no vacío), !campo (vacío)")] string[] where = null,
            [Description("Campos (por defecto id,fam,type,lvl): cat,ws,elev,mark,comments,host,x,y,z o un parámetro")] string f = null,
            [Description("Agrupar y contar por estos campos (máx. 3)")] string group_by = null,
            [Description("true = sólo el total")] bool? count = null,
            int? page = null,
            [Description("máx. 200")] int? page_size = null,
            [Description("Guardar ids como $nombre")] string save_as = null,
            [Description("Sólo elementos cambiados después de esta rev")] long? since = null,
            [Description("ft o mm (elev, x, y, z, where)")] string units = null,
            [Description("textos sin recortar")] bool? full = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
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
        [Description("Detalle de elementos concretos: ids o $conjunto. Por defecto id,cat,fam,type,lvl,ws,elev,mark,host,x,y,z; f para pedir otros parámetros por nombre. " +
                     "f=* = TODOS los parámetros de instancia y de tipo (hasta 10 elementos; lo común una sola vez; buscar filtra nombres).")]
        public Task<string> Get(
            McpServer server,
            [Description("Ids separados por coma o $conjunto")] string ids,
            [Description("Campos o nombres de parámetro, separados por coma; * = todos")] string f = null,
            [Description("Página (desde 1)")] int? page = null,
            [Description("ft (por defecto) o mm")] string units = null,
            [Description("textos sin recortar")] bool? full = null,
            [Description("Con f=*: sólo parámetros cuyo nombre contenga este texto")] string buscar = null,
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
        [Description("Revisiones completas en una llamada. name=lighting_audit: luminarias por nivel, workset y tipo; sin tag, tags huérfanos y duplicados por vista " +
                     "(guarda $sin_tag, $tags_huerfanos, $tags_sobrantes). views=rcp añade por cada plano de techo de iluminación (los que ya tienen tags) " +
                     "luminarias, tags y sin_tag_en_vista, estimado por nivel y sin regenerar. exacto=true cuenta lo realmente visible pero regenera cada vista: sólo con pocas vistas (máx. 12).")]
        public Task<string> Check(
            McpServer server,
            [Description("lighting_audit")] string name,
            [Description("Niveles por nombre, separados por coma (opcional)")] string lvl = null,
            [Description("rcp (planos de techo con tags de luminarias), rcp_todos, o ids de vista separados por coma (opcional)")] string views = null,
            [Description("true = luminarias realmente visibles por vista (lento: máx. 12 vistas)")] bool? exacto = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object> { ["name"] = name };
            if (exacto != null) a["exacto"] = exacto;
            if (lvl != null) a["lvl"] = lvl;
            if (views != null) a["views"] = views;
            return Call(server, Wire.OpCheck, a, ct);
        }

        [McpServerTool(Name = "geo", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Geometría real (mallas del modelo y de los vínculos), sin modificar nada. name: montaje (9 puntos contra la cara inferior de lo de arriba: ok, empotrada, flotando, sin_superficie), " +
                     "holgura, choques, cerca, muro, nivel_real, duplicados, cuarto, rango_vista, indice. Devuelve conteos, problemas primero (holguras en pulgadas) y $conjuntos por clase para plan. " +
                     "Si tarda: job_id → job. Repetir sin cambios es instantáneo. Detalle: guide tema=geo.")]
        public Task<string> Geo(
            McpServer server,
            [Description("Receta")] string name,
            [Description("Categorías (LightingFixtures)")] string cat = null,
            string lvl = null,
            [Description("Ids o $conjunto")] string ids = null,
            string ws = null,
            [Description("Familia o tipo (contiene)")] string type = null,
            [Description("losas, vigas, columnas, rampas, escaleras, muros, cielos, techos o categorías")] string contra = null,
            [Description("Vínculos (contiene), todos o ninguno")] string vinculos = null,
            bool? anfitrion = null,
            [Description("arriba o abajo")] string lado = null,
            [Description("1, 5 o 9")] int? puntos = null,
            [Description("1/16\" por defecto")] string tol = null,
            [Description("30' por defecto")] string max = null,
            [Description("cerca/muro, p. ej. 2'")] string radio = null,
            [Description("rango_vista: rcp, rcp_todos, plantas o ids")] string views = null,
            [Description("cuarto: ambos, cuartos o espacios")] string fuente = null,
            [Description("Subcategorías a ignorar, p. ej. JBox")] string sub_excluir = null,
            [Description("Clase a listar o todos")] string solo = null,
            int? page = null,
            int? page_size = null,
            [Description("ft o mm")] string units = null,
            bool? reconstruir = null,
            CancellationToken ct = default)
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
            Put("units", units); Put("reconstruir", reconstruir);
            return Call(server, Wire.OpGeo, a, ct);
        }

        [McpServerTool(Name = "sets", ReadOnly = true, OpenWorld = false)]
        [Description("Conjuntos guardados con save_as. op=list (por defecto) o op=drop con name.")]
        public Task<string> Sets(
            McpServer server,
            [Description("list o drop")] string op = "list",
            [Description("Nombre del conjunto, p. ej. $l5_perimetro")] string name = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object> { ["op"] = op ?? "list" };
            if (name != null) a["name"] = name;
            return Call(server, Wire.OpSets, a, ct);
        }

        [McpServerTool(Name = "plan", ReadOnly = true, OpenWorld = false)]
        [Description("Prepara cambios SIN aplicarlos y devuelve el diff (antes → después) con plan_id. Operaciones tipadas, máx. 200, " +
                     "un plan = un Deshacer. ops: set_param, set_values, set_type_param, change_type, set_elevation, set_workset, set_level, " +
                     "snap_to_surface, move, rotate, mirror, copy, delete, place, " +
                     "tag, tag_move, group_create, ungroup, group_place, view_create, " +
                     "view_set, view_hide, view_override, sheet_create, sheet_place, text_note, filled_region. " +
                     "ids: \"1,2\", $conjunto o @n (lo creado por la op n). Argumentos: guide tema=escritura op=<op>. job_id → job.")]
        public Task<string> Plan(
            McpServer server,
            [Description("[{\"op\":\"set_param\",\"ids\":\"$x\",\"name\":\"Comments\",\"value\":\"REVISAR\"}]")] JsonElement ops,
            [Description("Resumen corto para el usuario (aparece en la cola de Revit)")] string summary = null,
            [Description("Documento dN (por defecto el activo)")] string doc = null,
            [Description("Tarea autorizada (permite commit tras reconectar)")] string tarea = null,
            CancellationToken ct = default)
        {
            object parsed;
            try { parsed = Json.Parse(ops.ValueKind == JsonValueKind.String ? ops.GetString() : ops.GetRawText()); }
            catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "ops no es JSON válido: " + ex.Message })); }
            if (parsed is Dictionary<string, object> single) parsed = new List<object> { single };
            var a = new Dictionary<string, object> { ["ops"] = parsed };
            if (summary != null) a["summary"] = summary;
            if (doc != null) a[Wire.DocArg] = doc;
            if (tarea != null) a[Wire.TaskArg] = tarea;
            return Call(server, Wire.OpPlan, a, ct);
        }

        [McpServerTool(Name = "commit", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
        [Description("Aplica un plan ya revisado por el usuario. Si el usuario tiene la cola activa, responde estado=retenido: " +
                     "el cambio espera a que el usuario lo aplique desde Revit; no lo repita. Si algo cambió desde el plan: estado=conflicto y no se aplica nada. " +
                     "Lo creado queda en $creados (y $creados_n por operación); 'verificacion' relee lo tocado.")]
        public Task<string> Commit(
            McpServer server,
            [Description("plan_id devuelto por plan")] string plan_id,
            [Description("Clave de idempotencia: con la misma clave no se aplica dos veces")] string clave = null,
            [Description("Tarea autorizada")] string tarea = null,
            [Description("Tras interrumpido con clave=: no_aplicado | aplicado")] string reconciliar = null,
            [Description("aplicar (por defecto) | ensayo: aplica, verifica y REVIERTE; no es un commit")] string modo = null,
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
        [Description("Documentos abiertos en este Revit (proyectos y familias) con su handle dN, tipo, ruta, si está modificado y último guardado/sync.")]
        public Task<string> Docs(McpServer server, CancellationToken ct = default)
            => Call(server, Wire.OpDocs, new Dictionary<string, object>(), ct);

        [McpServerTool(Name = "res", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Lee un resultado largo guardado (id r… o b…) por tramos de líneas.")]
        public Task<string> Res(
            [Description("Id del resultado")] string id,
            [Description("Línea inicial")] int desde = 0,
            [Description("Número de líneas")] int lineas = 300)
        {
            var txt = ResultStore.Read(id, desde, lineas, out var total);
            if (txt == null) return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "no existe el resultado " + id }));
            return Task.FromResult(Json.Write(new Dictionary<string, object> { ["texto"] = txt, ["total_lineas"] = (long)total, ["desde"] = (long)desde }));
        }

        [McpServerTool(Name = "exec", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("C# sobre la API pública de Revit. Código: expresión, cuerpo con return, o clase con static object Run(ScriptContext ctx). " +
                     "Ya existen doc, uidoc, app, uiapp, args, ctx. modo=consulta (solo lectura, sin transacción) | ensayo (cambia y revierte; nivel W) | ejecutar (tx=auto|grupo|ninguna; ninguna para EditFamily/LoadFamily). " +
                     "Devuelve el valor en JSON, avisos de Revit y qué cambió.")]
        public Task<string> Exec(
            McpServer server,
            [Description("Código C#")] string codigo = null,
            [Description("consulta | ensayo | ejecutar")] string modo = "consulta",
            [Description("auto | grupo | ninguna")] string tx = null,
            [Description("Documento dN")] string doc = null,
            [Description("Argumentos (objeto JSON) → args")] JsonElement? args = null,
            [Description("Script guardado a repetir, o 'lista'")] string nombre = null,
            [Description("Guardar el código con ese nombre")] string guardar = null,
            [Description("Clave de idempotencia")] string clave = null,
            [Description("Tarea autorizada")] string tarea = null,
            [Description("Archivo .cs con el código")] string archivo = null,
            [Description("DLL extra separadas por coma")] string refs = null,
            [Description("Otros documentos que toca: d2,d3")] string docs = null,
            [Description("Tras interrumpido con clave=: no_aplicado | aplicado")] string reconciliar = null,
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
                catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "args no es JSON: " + ex.Message })); }
            }
            return Call(server, Wire.OpExec, a, ct, WriteTimeout);
        }

        [McpServerTool(Name = "fam", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Familias y conectores. accion=info|caras (leen) | abrir (EditFamily → dN) | conector (crea o reconcilia por nombre: sistema, cara, radio/diametro con unidad, flujo) | " +
                     "param (valor/formula, crear, asociar) | cargar (en=dN, sobrescribir=no|valores). Argumentos en 'args' como objeto JSON.")]
        public Task<string> Fam(
            McpServer server,
            [Description("info | caras | abrir | conector | param | cargar")] string accion,
            [Description("Argumentos, p. ej. {\"nombre\":\"fria\",\"sistema\":\"fria\",\"cara\":\"812:3\",\"radio\":\"6.5mm\"}")] JsonElement? args = null,
            [Description("Documento dN")] string doc = null,
            [Description("Clave de idempotencia")] string clave = null,
            CancellationToken ct = default)
            => CallWithArgs(server, Wire.OpFam, accion, args, doc, clave, ct, WriteTimeout);

        [McpServerTool(Name = "doc_op", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Operaciones de documento: guardar | guardar_como (como=ruta) | sync (comentario=) | pdf (conjunto= u hojas=, carpeta=, nombre=) | cerrar (guardar=si|descartar=si). " +
                     "Verifica el resultado (archivo, hash, páginas). Argumentos en 'args' como objeto JSON.")]
        public Task<string> DocOp(
            McpServer server,
            [Description("guardar | guardar_como | sync | pdf | cerrar")] string accion,
            [Description("Argumentos (objeto JSON)")] JsonElement? args = null,
            [Description("Documento dN")] string doc = null,
            [Description("Clave de idempotencia")] string clave = null,
            CancellationToken ct = default)
            => CallWithArgs(server, Wire.OpDoc, accion, args, doc, clave, ct, WriteTimeout);

        private Task<string> CallWithArgs(McpServer server, string op, string accion, JsonElement? args, string doc, string clave, CancellationToken ct, TimeSpan timeout)
        {
            var a = new Dictionary<string, object>();
            if (args.HasValue && args.Value.ValueKind != JsonValueKind.Null && args.Value.ValueKind != JsonValueKind.Undefined)
            {
                object parsed;
                try { parsed = Json.Parse(args.Value.ValueKind == JsonValueKind.String ? args.Value.GetString() : args.Value.GetRawText()); }
                catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "args no es JSON: " + ex.Message })); }
                if (parsed is Dictionary<string, object> d) foreach (var kv in d) a[kv.Key] = kv.Value is string || kv.Value is bool ? kv.Value : Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            a["accion"] = accion;
            if (doc != null) a[Wire.DocArg] = doc;
            if (clave != null) a[Wire.KeyArg] = clave;
            return Call(server, op, a, ct, timeout);
        }

        [McpServerTool(Name = "tarea", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Tareas largas reanudables: una receta (clase que hereda de Receta, por nombre registrado, archivo .cs o código) recorre unidades por lotes, " +
                     "con checkpoints, barreras guardar/sync/pdf verificadas y QC final. Cualquier cliente la continúa por id. " +
                     "accion=iniciar | estado | resumen | esperar (desde=rev, max=s) | unidades | lista | recetas | reanudar | cancelar | reconciliar (decision=aplicada|no_aplicada, claves) | anotar (texto) | olvidar.")]
        public Task<string> Tarea(
            McpServer server,
            [Description("iniciar | estado | resumen | esperar | unidades | lista | recetas | reanudar | cancelar | reconciliar | anotar | olvidar")] string accion = "estado",
            [Description("Id de la tarea (al iniciar: opcional, para poder repetir sin duplicar)")] string id = null,
            [Description("Receta registrada por nombre")] string receta = null,
            [Description("Archivo .cs con la receta")] string archivo = null,
            [Description("Código de la receta")] string codigo = null,
            [Description("Argumentos de la receta (objeto JSON)")] JsonElement? args = null,
            [Description("Objetivo en una frase (memoria de la tarea)")] string objetivo = null,
            [Description("Opciones: \"guardar_cada=200 al_final=guardar,pdf en_error=continuar lote_max=200 espera_s=10 desde=N max=30 decision=aplicada claves=a,b texto=...\"")] string opciones = null,
            [Description("Documento dN")] string doc = null,
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
                catch (Exception ex) { return Task.FromResult(Json.Write(new Dictionary<string, object> { ["estado"] = Status.Invalido, ["aviso"] = "args no es JSON: " + ex.Message })); }
            }
            return Call(server, Wire.OpTask, a, ct, TimeSpan.FromSeconds(180));
        }

        [McpServerTool(Name = "job", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Seguimiento. job_id: espera el resultado de una revisión larga (hasta ~40 s por llamada) u op=cancel. " +
                     "plan_id: estado del plan (listo, retenido, aplicado, conflicto, revertido, vencido, descartado, desconocido, reconciliado) u op=drop. " +
                     "op=pendientes: bloqueo del documento. op=reconciliar plan_id: compara con el modelo y libera sólo si queda demostrado. Sin ids: lista de esta conversación.")]
        public Task<string> Job(
            McpServer server,
            [Description("job_id devuelto por geo o plan (opcional)")] string job_id = null,
            [Description("plan_id (opcional)")] string plan_id = null,
            [Description("status (por defecto), cancel (job), drop (plan), pendientes o reconciliar")] string op = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (job_id != null) a["job_id"] = job_id;
            if (plan_id != null) a["plan_id"] = plan_id;
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
        /// Respuesta para la IA: si todo fue bien, los datos con la rev; si no, el estado y un aviso accionable.
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
