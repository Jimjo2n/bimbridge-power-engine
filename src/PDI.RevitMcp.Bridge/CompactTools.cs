// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Bridge
{
    /// <summary>
    /// Perfil compacto (por defecto desde 0.4.0): 3 herramientas en vez de 14, órdenes de una línea y respuestas en texto
    /// compacto. Mismo motor y mismas protecciones que el perfil completo; sólo cambia lo que lee y escribe la IA.
    /// </summary>
    [McpServerToolType]
    public sealed class CompactTools
    {
        public const string Instructions =
            "Revit abierto (proyectos y familias, handle doc=dN). Lea con revit (órdenes de una línea; varias líneas = en paralelo). Cambie con " +
            "plan→commit (tipado, un Deshacer), exec (C# sobre la API de Revit) o act (familias, conectores, guardar, sync, PDF). " +
            "Si el usuario autorizó la tarea, aplique sin pedir confirmación por lote. Use otras herramientas que él autorice si algo no está cubierto. " +
            "Pausa del usuario: deténgase. retenido: espera en la cola, no repetir. clave= evita repetir una escritura. Ayuda: revit \"guide\". Documentos y geometría sin Revit (PDF, DOCX, registro plano–modelo → ops): power \"cap\"." ;

        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(180);
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(120);
        /// <summary>Guardar, sincronizar y PDF pueden tardar minutos: el add-in devuelve job_id si pasa de ~45 s.</summary>
        private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(180);
        /// <summary>Respuestas más largas se guardan y se devuelve el inicio + res &lt;id&gt; (R054/R055).</summary>
        public const int MaxTextChars = 12000;

        private static string Limit(string text) => ResultStore.Shorten(text, MaxTextChars, "b", out _);
        private readonly RevitConnection _revit;
        private readonly ConcurrentDictionary<string, (int n, string text, string id)> _last = new ConcurrentDictionary<string, (int, string, string)>();
        private int _seq;

        public CompactTools(RevitConnection revit) { _revit = revit; }

        [McpServerTool(Name = "revit", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Lee (no cambia el modelo). Una orden por línea: verbo + palabras + clave=valor; doc=dN fija el documento.\n" +
                     "ctx [instancia=2024] | docs | fam info|caras [familia=X] | res <id> desde= | " +
                     "cat [resumen|niveles|vistas|laminas|tipos|familias|plantillas|worksets|vinculos|filtros] | " +
                     "q <cat> lvl= t= ids= v= f= g=campos n=true digest=true timing=true s=$nombre elev>9 type~LT u=mm | get <ids|$x> [f=*] | " +
                     "chk lighting_audit | geo <receta> <cat> lvl= | sets | job <id> | show <ids> | cap [vista] colorear=categorias resaltar=$x | " +
                     "tarea estado|resumen|esperar|unidades|lista|recetas <id> [desde=rev max=s] | guide [tema]\n" +
                     "cat: LF luminarias, LD dispositivos, EF tomas, EE tableros, FA alarma. Texto compacto: fijo = valor común; col# = diccionario.")]
        public async Task<CallToolResult> Revit(
            McpServer server,
            [Description("Órdenes, p. ej. \"q LF g=lvl,type\"")] string c,
            CancellationToken ct = default)
        {
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await RevitInner(c, ct).ConfigureAwait(false);
            var outChars = result.Content.OfType<TextContentBlock>().Sum(t => (long)(t.Text?.Length ?? 0));
            UsageMetrics.Record(_revit.ClientName, "revit", FirstVerb(c), c?.Length ?? 0, outChars, sw.ElapsedMilliseconds, result.IsError == true ? "error" : "ok");
            return result;
        }

        private static string FirstVerb(string c)
        {
            var t = (c ?? "").TrimStart();
            var i = t.IndexOfAny(new[] { ' ', '\n', '\r' });
            return (i < 0 ? t : t.Substring(0, i)).ToLowerInvariant();
        }

        private async Task<CallToolResult> RevitInner(string c, CancellationToken ct)
        {
            var lines = Command.SplitLines(c);
            var result = new CallToolResult { Content = new List<ContentBlock>() };
            if (lines.Count == 0) { result.Content.Add(Text("orden vacía. Ej.: q LF g=lvl")); result.IsError = true; return result; }
            if (lines.Count > 16) { result.Content.Add(Text("máx. 16 órdenes por llamada")); result.IsError = true; return result; }
            var tasks = lines.Select(l => RunLine(l, ct)).ToList();
            var outs = await Task.WhenAll(tasks).ConfigureAwait(false);
            var texts = new List<string>();
            var failed = new List<int>();
            for (var i = 0; i < outs.Length; i++)
            {
                texts.Add(lines.Count > 1 ? "## " + (outs[i].error ? "ERROR " : "") + lines[i] + "\n" + outs[i].text : outs[i].text);
                if (outs[i].image != null) result.Content.Add(ImageContentBlock.FromBytes(outs[i].image, outs[i].mime));
                if (outs[i].error) failed.Add(i + 1);
            }
            // F15/R056: estado por orden. Todas fallaron → IsError; algunas → aviso al principio con cuáles.
            if (failed.Count == lines.Count) result.IsError = true;
            else if (failed.Count > 0) texts.Insert(0, "parcial: fallaron " + failed.Count + " de " + lines.Count + " órdenes (n.º " + string.Join(", ", failed) + "); las demás están bien");
            var notice = _revit.TakeRetargetNotice();
            if (notice != null) texts.Insert(0, "aviso_instancia: " + notice);
            result.Content.Insert(0, Text(string.Join("\n", texts)));
            return result;
        }

        [McpServerTool(Name = "plan", ReadOnly = true, Idempotent = false, OpenWorld = false)]
        [Description("Prepara cambios SIN aplicarlos → diff + plan_id. Una operación por línea: \"op ids clave=valor\" (o JSON). " +
                     "set (set_param) $x name=Comments value=REVISAR | snap $flotando | tag $sin_tag view=123 | type $x to=\"Fam: Tipo\" | elev | ws | " +
                     "move rotate mirror copy delete place tag_move set_values set_type_param set_level group_* view_* sheet_* text_note filled_region. " +
                     "@n = lo creado por la línea n. Un plan = un Deshacer. Argumentos: revit \"guide escritura op=tag\".")]
        public async Task<string> Plan(
            McpServer server,
            [Description("Operaciones")] string ops,
            [Description("Resumen para el usuario")] string summary = null,
            [Description("Documento (dN); por defecto el activo")] string doc = null,
            [Description("Tarea autorizada (permite commit tras reconectar)")] string tarea = null,
            CancellationToken ct = default)
        {
            List<object> parsed;
            try { parsed = Command.ParseOps(ops); }
            catch (Exception ex) { return "estado: invalido\naviso: ops no se entiende: " + ex.Message; }
            var a = new Dictionary<string, object> { ["ops"] = parsed };
            if (summary != null) a["summary"] = summary;
            if (doc != null) a[Wire.DocArg] = doc;
            if (tarea != null) a[Wire.TaskArg] = tarea;
            return await CallText(server, Wire.OpPlan, a, ReadTimeout, ct).ConfigureAwait(false);
        }

        // Idempotent = false: con ops= cada llamada planifica y aplica de nuevo. Sólo commit plan_id=… (o clave=) es repetible sin efecto.
        [McpServerTool(Name = "commit", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Aplica un plan sobre SU documento. retenido = espera en la cola de Revit (no repetir); conflicto = no se aplicó. Lo creado: $creados. " +
                     "clave= (idempotencia): con la misma clave no se aplica dos veces aunque se corte la conexión. " +
                     "Sin plan_id y con ops= (mismo formato que plan): planifica y aplica en un solo viaje, con las mismas verificaciones.")]
        public Task<string> Commit(McpServer server, string plan_id = null, string clave = null, string tarea = null, string reconciliar = null,
            [Description("aplicar (por defecto) | ensayo: aplica, verifica y REVIERTE; no es un commit")] string modo = null,
            [Description("Operaciones (como plan) para planificar y aplicar en un viaje, sin plan_id")] string ops = null,
            [Description("Resumen para el usuario (con ops)")] string summary = null,
            [Description("Documento dN (con ops)")] string doc = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (plan_id != null) a["plan_id"] = plan_id;
            if (ops != null)
            {
                try { a["ops"] = Command.ParseOps(ops); }
                catch (Exception ex) { return Task.FromResult("estado: invalido\naviso: ops no se entiende: " + ex.Message); }
                if (summary != null) a["summary"] = summary;
                if (doc != null) a[Wire.DocArg] = doc;
            }
            if (plan_id == null && ops == null) return Task.FromResult("estado: invalido\naviso: indique plan_id, u ops para planificar y aplicar en un viaje");
            if (modo != null) a["modo"] = modo;
            if (reconciliar != null) a["reconciliar"] = reconciliar;
            if (clave != null) a[Wire.KeyArg] = clave;
            if (tarea != null) a[Wire.TaskArg] = tarea;
            return CallText(server, Wire.OpCommit, a, WriteTimeout, ct);
        }

        [McpServerTool(Name = "exec", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("C# sobre la API pública de Revit (sin lista de operaciones). Código: expresión (doc.Title), cuerpo con return, o clase con " +
                     "static object Run(ScriptContext ctx). Ya existen doc, uidoc, app, uiapp, args, ctx (ctx.Mm(), ctx.E(id), ctx.Set(\"$x\"), ctx.SaveSet, ctx.Log). " +
                     "modo=consulta (por defecto: solo lectura, sin transacción) | ensayo (cambia y revierte; exige nivel W) | ejecutar (tx=auto: una transacción y un Deshacer; grupo; ninguna: el script abre las suyas, " +
                     "necesario para EditFamily/LoadFamily). Devuelve el valor en JSON, avisos de Revit y qué cambió. Un script largo bloquea Revit mientras corre.")]
        public Task<string> Exec(
            McpServer server,
            [Description("Código C# (o vacío con nombre= para repetir uno guardado)")] string codigo = null,
            [Description("consulta | ensayo | ejecutar")] string modo = "consulta",
            [Description("auto | grupo | ninguna")] string tx = null,
            [Description("Documento dN (por defecto el activo)")] string doc = null,
            [Description("Argumentos para el script (objeto JSON) → args")] System.Text.Json.JsonElement? args = null,
            [Description("Script guardado a repetir, o 'lista'")] string nombre = null,
            [Description("Guardar este código con ese nombre")] string guardar = null,
            [Description("Clave de idempotencia (escrituras)")] string clave = null,
            [Description("Tarea autorizada")] string tarea = null,
            [Description("Archivo .cs con el código (en vez de codigo=)")] string archivo = null,
            [Description("DLL extra separadas por coma (rutas)")] string refs = null,
            [Description("Otros documentos que toca el script: d2,d3")] string docs = null,
            [Description("Tras un interrumpido con clave=: no_aplicado (repetir) | aplicado (marcar hecho)")] string reconciliar = null,
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
            if (args.HasValue && args.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try { a["args"] = Json.Parse(args.Value.GetRawText()); } catch (Exception) { }
            }
            else if (args.HasValue && args.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                try { a["args"] = Json.Parse(args.Value.GetString()); } catch (Exception ex) { return Task.FromResult("estado: invalido\naviso: args no es JSON: " + ex.Message); }
            }
            return CallText(server, Wire.OpExec, a, WriteTimeout, ct);
        }

        [McpServerTool(Name = "act", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Acciones de documento, una por línea (doc=dN fija el destino; clave= evita repetir):\n" +
                     "abrir familia=\"X\" (EditFamily → dN del RFA) | conector nombre=fria sistema=fria|caliente|sanitario|<PipeSystemType> cara=elem:n " +
                     "radio=6.5mm|diametro= flujo=entrada|salida|bidireccional [cota=radio|diametro] (crea o reconcilia por nombre y verifica) | " +
                     "param nombre=X valor=6.5mm|formula= [tipo=T|todos_tipos=si] [crear=tipo|instancia dato=longitud] [asociar=elem:CONNECTOR_RADIUS] | " +
                     "cargar en=dN [sobrescribir=valores] | guardar [como=ruta] | sync [comentario=] | pdf conjunto=\"X\"|hojas=$x [carpeta= nombre=] | cerrar\n" +
                     "Tareas largas reanudables (una orden; el motor hace lotes, guarda y exporta como barreras): tarea iniciar receta=X|archivo=ruta.cs " +
                     "[id= objetivo= args.campo=valor | args={json}] [guardar_cada=N al_final=guardar,pdf en_error=continuar espera_s=10] | " +
                     "tarea reanudar|cancelar <id> | tarea reconciliar <id> decision=aplicada|no_aplicada [claves=a,b] | tarea anotar <id> texto=")]
        public async Task<string> Act(
            McpServer server,
            [Description("Acciones, p. ej. \"conector nombre=fria sistema=fria cara=812:3 radio=6.5mm doc=d3\"")] string c,
            CancellationToken ct = default)
        {
            if (server?.ClientInfo?.Name is string cn && cn.Length > 0) _revit.ClientName = cn; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = await ActInner(c, ct).ConfigureAwait(false);
            UsageMetrics.Record(_revit.ClientName, "act", FirstVerb(c), c?.Length ?? 0, text.Length, sw.ElapsedMilliseconds, text.StartsWith("parcial", StringComparison.Ordinal) || text.Contains("estado: ") ? "con_errores" : "ok");
            return text;
        }

        private async Task<string> ActInner(string c, CancellationToken ct)
        {
            var lines = Command.SplitLines(c);
            if (lines.Count == 0) return "orden vacía";
            // En orden (no en paralelo): abrir → conector → cargar dependen unas de otras.
            var outs = new List<string>();
            var failed = 0;
            foreach (var line in lines)
            {
                var cmd = Command.Parse(line);
                var a = cmd.Args;
                string op;
                var verb = cmd.Verb;
                switch (verb)
                {
                    case "abrir": case "conector": case "param": case "cargar": op = Wire.OpFam; break;
                    case "guardar": case "guardar_como": case "sync": case "sincronizar": case "pdf": case "cerrar": op = Wire.OpDoc; break;
                    case "tarea":
                    {
                        op = Wire.OpTask;
                        verb = (cmd.Positional.Count > 0 ? cmd.Positional[0] : "estado").ToLowerInvariant();
                        if (cmd.Positional.Count > 1 && !a.ContainsKey("id")) a["id"] = cmd.Positional[1];
                        if (a.TryGetValue("count", out var cnt)) { a["n"] = cnt; a.Remove("count"); }
                        var err = TaskArgs(a);
                        if (err != null) { outs.Add((lines.Count > 1 ? "## ERROR " + line + "\n" : "") + err); failed++; continue; }
                        break;
                    }
                    default:
                        outs.Add((lines.Count > 1 ? "## ERROR " + line + "\n" : "") + "acción desconocida: " + verb + ". Use abrir, conector, param, cargar, guardar, sync, pdf, cerrar o tarea");
                        failed++;
                        continue;
                }
                a["accion"] = verb;
                if (verb == "cerrar" && cmd.Positional.Count > 0 && !a.ContainsKey(Wire.DocArg)) a[Wire.DocArg] = cmd.Positional[0];
                if (verb == "abrir" && cmd.Positional.Count > 0 && !a.ContainsKey("familia")) a["familia"] = string.Join(" ", cmd.Positional);
                var resp = await _revit.CallAsync(op, a, op == Wire.OpDoc || op == Wire.OpTask ? LongTimeout : WriteTimeout, ct).ConfigureAwait(false);
                var bad = resp.Estado != Status.Ok && resp.Estado != Status.ConDiferencias;
                if (bad) failed++;
                var text = Limit(CompactText.Write(RevitTools.RenderDict(resp)));
                outs.Add(lines.Count > 1 ? "## " + (bad ? "ERROR " : "") + line + "\n" + text : text);
                // Si falla una acción, las siguientes (que suelen depender de ella) no se intentan.
                if (bad && lines.Count > 1 && outs.Count < lines.Count) { outs.Add("(no se ejecutaron las " + (lines.Count - outs.Count) + " acciones siguientes)"); break; }
            }
            if (failed > 0 && lines.Count > 1) outs.Insert(0, "parcial: fallaron " + failed + " de " + lines.Count);
            return string.Join("\n", outs);
        }

        /// <summary>args.campo=valor → args{campo}; args={json} → objeto. Devuelve el error o null.</summary>
        internal static string TaskArgs(Dictionary<string, object> a)
        {
            Dictionary<string, object> args = null;
            if (a.TryGetValue("args", out var raw))
            {
                if (raw is Dictionary<string, object> d) args = d;
                else if (raw is string js)
                {
                    try { args = Json.Parse(js) as Dictionary<string, object>; }
                    catch (Exception ex) { return "args no es JSON: " + ex.Message; }
                    if (args == null) return "args debe ser un objeto JSON {…}";
                }
                a.Remove("args");
            }
            foreach (var k in a.Keys.Where(k => k.StartsWith("args.", StringComparison.Ordinal)).ToList())
            {
                args = args ?? new Dictionary<string, object>(StringComparer.Ordinal);
                args[k.Substring(5)] = a[k];
                a.Remove(k);
            }
            if (args != null) a["args"] = args;
            return null;
        }

        private async Task<string> CallText(McpServer server, string op, Dictionary<string, object> a, TimeSpan timeout, CancellationToken ct)
        {
            if (server?.ClientInfo?.Name is string name && name.Length > 0) _revit.ClientName = name; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await _revit.CallAsync(op, a, timeout, ct).ConfigureAwait(false);
            var text = Limit(CompactText.Write(RevitTools.RenderDict(resp)));
            UsageMetrics.Record(_revit.ClientName, op, op, Json.Write(a).Length, text.Length, sw.ElapsedMilliseconds, resp.Estado);
            return text;
        }

        private static TextContentBlock Text(string s) => new TextContentBlock { Text = s };

        private async Task<(string text, byte[] image, string mime, bool error)> RunLine(string line, CancellationToken ct)
        {
            var cmd = Command.Parse(line);
            var a = cmd.Args;
            string P(int i) => i < cmd.Positional.Count ? cmd.Positional[i] : null;
            string op;
            var timeout = ReadTimeout;
            switch (cmd.Verb)
            {
                case "metricas": case "métricas":
                    return (CompactText.Write(UsageMetrics.Summary()), null, null, false);
                case "guide": case "ayuda": case "help":
                    return (Guides.Get(P(0), Arg.GetString(a, "op")), null, null, false);
                case "ctx": case "context":
                    op = Wire.OpContext;
                    var inst = Arg.GetString(a, "instancia");
                    if (!string.IsNullOrWhiteSpace(inst))
                    {
                        var why = await _revit.SwitchToAsync(inst.Trim(), ct).ConfigureAwait(false);
                        if (why != null) return (why, null, null, true);
                        a.Remove("instancia");
                    }
                    // Registro de identidad: el puente lo recuerda para repetirlo al reconectar (si no, la sesión nueva quedaría sin nombre).
                    if (Arg.GetString(a, "agente") is string ag && !string.IsNullOrWhiteSpace(ag)) { _revit.AgentName = ag; _revit.AgentSource = "registro"; }
                    if (Arg.GetString(a, "conversacion") is string cv0 && !string.IsNullOrWhiteSpace(cv0)) _revit.Conversation = cv0;
                    if (Arg.GetString(a, "modelo") is string md0 && !string.IsNullOrWhiteSpace(md0)) _revit.Model = md0;
                    break;
                case "docs": op = Wire.OpDocs; break;
                case "fam":
                    op = Wire.OpFam;
                    var fv = (P(0) ?? "info").ToLowerInvariant();
                    if (fv != "info" && fv != "caras") return ("revit fam sólo lee (info, caras). Para abrir, conector, param y cargar use la herramienta act.", null, null, true);
                    a["accion"] = fv;
                    if (P(1) != null && !a.ContainsKey("familia")) a["familia"] = P(1);
                    break;
                case "res":
                    {
                        var id = P(0) ?? Arg.GetString(a, "id");
                        var from = (int)(Arg.GetLong(a, "desde") ?? 0);
                        var n = (int)(Arg.GetLong(a, "count") ?? Arg.GetLong(a, "lineas") ?? 300);
                        var txt = ResultStore.Read(id, from, n, out var total);
                        if (txt == null) return ("no existe el resultado " + id + " (se guardan " + ResultStore.KeepDays + " días)", null, null, true);
                        var end = Math.Min(total, from + n);
                        return (txt + (end < total ? "\n… líneas " + from + "–" + end + " de " + total + "; sigue con: res " + id + " desde=" + end : ""), null, null, false);
                    }
                case "cat": case "catalog":
                    op = Wire.OpCatalog;
                    if (P(0) != null && !a.ContainsKey("que")) a["que"] = P(0);
                    break;
                case "q": case "query":
                    op = Wire.OpQuery;
                    if (P(0) != null && !a.ContainsKey("cat")) a["cat"] = string.Join(",", P(0).Split(',').Select(Command.CatAlias));
                    if (cmd.Where.Count > 0) a["where"] = cmd.Where;
                    break;
                case "get":
                    if (P(0) != null && !a.ContainsKey("ids")) a["ids"] = P(0);
                    a[Wire.ToolArg] = "get";
                    if (Arg.GetString(a, "f") == "*") { op = Wire.OpParams; a.Remove("f"); }
                    else { op = Wire.OpQuery; if (!a.ContainsKey("f")) a["f"] = "id,cat,fam,type,lvl,ws,elev,mark,host,x,y,z"; }
                    break;
                case "chk": case "check":
                    op = Wire.OpCheck;
                    if (!a.ContainsKey("name")) a["name"] = P(0) ?? "lighting_audit";
                    break;
                case "geo":
                    op = Wire.OpGeo;
                    if (!a.ContainsKey("name") && P(0) != null) a["name"] = P(0);
                    if (P(1) != null && !a.ContainsKey("cat")) a["cat"] = Command.CatAlias(P(1));
                    if (!a.ContainsKey("page_size")) a["page_size"] = 25L; // el resto queda en $conjuntos
                    break;
                case "sets": op = Wire.OpSets; if (!a.ContainsKey("op")) a["op"] = P(0) ?? "list"; if (P(1) != null) a["name"] = P(1); break;
                case "job":
                    op = Wire.OpJob;
                    if (P(0) != null) { if (P(0).StartsWith("P-", StringComparison.OrdinalIgnoreCase) || P(0).StartsWith("plan", StringComparison.OrdinalIgnoreCase)) a["plan_id"] = P(0); else a["job_id"] = P(0); }
                    break;
                case "tarea": case "tareas":
                {
                    op = Wire.OpTask;
                    var tv = (P(0) ?? (cmd.Verb == "tareas" ? "lista" : "estado")).ToLowerInvariant();
                    if (OpSemantics.TaskWrites.Contains(tv)) return ("revit sólo lee tareas (estado, resumen, esperar, unidades, lista, recetas). Para iniciar, reanudar, cancelar o reconciliar use act \"tarea " + tv + " …\".", null, null, true);
                    a["accion"] = tv;
                    if (P(1) != null && !a.ContainsKey("id")) a["id"] = P(1);
                    if (a.TryGetValue("count", out var cnt)) { a["n"] = cnt; a.Remove("count"); }
                    if (tv == "esperar") timeout = TimeSpan.FromSeconds(Math.Min(120, Arg.GetLong(a, "max") ?? 30) + 20);
                    break;
                }
                case "show": op = Wire.OpShow; if (P(0) != null && !a.ContainsKey("ids")) a["ids"] = P(0); break;
                case "cap": case "capture":
                    op = Wire.OpCapture; timeout = CaptureTimeout;
                    if (P(0) != null && !a.ContainsKey("vista") && long.TryParse(P(0), out var vid)) a["vista"] = vid;
                    if (!a.ContainsKey("px")) a["px"] = 1000L; // ~0,5× tokens de imagen frente a 1400; px=1600 para detalle
                    break;
                default:
                    return ("orden desconocida: " + cmd.Verb + ". Use: ctx, docs, fam, res, cat, q, get, chk, geo, sets, job, show, cap, tarea, guide (cambios: plan, exec, act)", null, null, true);
            }

            var ipc = System.Diagnostics.Stopwatch.StartNew();
            var resp = await _revit.CallAsync(op, a, timeout, ct).ConfigureAwait(false);
            ipc.Stop();
            if (op == Wire.OpQuery && Arg.GetBool(a, "digest") == true && resp.Estado == Status.Ok
                && (!(resp.Datos is Dictionary<string, object> digestData) || !digestData.ContainsKey("formato_digest")))
                return ("El add-in conectado no admite digest de consultas.", null, null, true);
            var o = RevitTools.RenderDict(resp);
            byte[] img = null; string mime = null;
            if (op == Wire.OpCapture)
            {
                mime = o.TryGetValue("mime", out var m) ? m as string ?? "image/png" : "image/png";
                if (resp.Estado == Status.Ok && o.TryGetValue("archivo", out var f) && f is string path)
                {
                    try { img = File.ReadAllBytes(path); } catch (Exception ex) { o["aviso"] = "imagen no legible: " + ex.Message; }
                }
                o.Remove("mime"); o.Remove("archivo"); o.Remove("bytes");
            }
            o.Remove("ms");
            o.TryGetValue("tiempos_consulta", out var queryTiming);
            o.Remove("tiempos_consulta");
            var text = CompactText.Write(o);

            // Misma orden y misma respuesta que antes en esta conversación → no repetir el texto, pero dejarlo recuperable (F14):
            // un cliente que compactó su contexto lo relee con res <id>.
            if (img == null && text.Length > 200)
            {
                var n = Interlocked.Increment(ref _seq);
                if (_last.TryGetValue(line, out var prev) && prev.text == text)
                {
                    var id = prev.id ?? ResultStore.Save(text, "b");
                    _last[line] = (prev.n, prev.text, id);
                    text = "sin cambios: igual que la respuesta anterior a esta misma orden" + (o.TryGetValue("rev", out var rv) ? " (rev " + rv + ")" : "") +
                           "; texto completo: res " + id;
                }
                else _last[line] = (n, text, null);
            }
            if (op == Wire.OpQuery && Arg.GetBool(a, "timing") == true)
            {
                var timing = new Dictionary<string, object> { ["puente_ipc_total_ms"] = ipc.Elapsed.TotalMilliseconds };
                if (queryTiming != null) timing["revit"] = queryTiming;
                text = CompactText.Write(timing) + "\n" + text;
            }
            text = Limit(text);
            return (text, img, mime, resp.Estado != Status.Ok && resp.Estado != Status.ConDiferencias);
        }
    }
}
