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
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Bridge
{
    /// <summary>
    /// Compact profile (default since 0.4.0): 3 tools instead of 14, one-line commands and replies in compact
    /// text. Same engine and same protections as the full profile; only what the AI reads and writes changes.
    /// </summary>
    [McpServerToolType]
    public sealed class CompactTools
    {
        public const string Instructions =
            "Revit is open (projects and families, handle doc=dN). Read with revit (one-line commands; several lines = in parallel). Change with " +
            "plan→commit (typed, one Undo), exec (C# against the Revit API) or act (families, connectors, save, sync, PDF). " +
            "If the user authorized the task, apply without asking for confirmation per batch. Use other tools the user authorizes if something is not covered. " +
            "User pause: stop. retenido: waiting in the queue, do not repeat. clave= prevents repeating a write. Help: revit \"guide\". Documents and geometry without Revit (PDF, DOCX, plan–model registration → ops): power \"cap\"." ;

        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(180);
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(120);
        /// <summary>Save, sync and PDF can take minutes: the add-in returns job_id if it goes past ~45 s.</summary>
        private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(180);
        /// <summary>Longer replies are saved and the beginning + res &lt;id&gt; is returned (R054/R055).</summary>
        public const int MaxTextChars = 12000;

        private static string Limit(string text) => ResultStore.Shorten(text, MaxTextChars, "b", out _);
        private readonly RevitConnection _revit;
        private readonly ConcurrentDictionary<string, (int n, string text, string id)> _last = new ConcurrentDictionary<string, (int, string, string)>();
        private int _seq;

        public CompactTools(RevitConnection revit) { _revit = revit; }

        [McpServerTool(Name = "revit", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description("Reads (does not change the model). One command per line: verb + words + key=value; doc=dN pins the document.\n" +
                     "ctx [instancia=2024] | docs | fam info|caras [familia=X] | res <id> desde= | " +
                     "cat [resumen|niveles|vistas|laminas|tipos|familias|plantillas|worksets|vinculos|filtros] | " +
                     "q <cat> lvl= t= ids= v= f= g=fields n=true digest=true timing=true s=$name elev>9 type~LT u=mm | get <ids|$x> [f=*] | " +
                     "chk lighting_audit | chk tabla_audit tabla=<id or name> | geo <recipe> <cat> lvl= | sets | job <id> | show <ids> | cap [vista] colorear=categorias resaltar=$x | " +
                     "tarea estado|resumen|esperar|unidades|lista|recetas <id> [desde=rev max=s] | guide [tema]\n" +
                     "cat: LF fixtures, LD devices, EF receptacles, EE panels, FA fire alarm. Compact text: fijo = shared value; col# = dictionary.")]
        public async Task<CallToolResult> Revit(
            McpServer server,
            [Description("Commands, e.g. \"q LF g=lvl,type\"")] string c,
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
            if (lines.Count == 0) { result.Content.Add(Text("empty command. E.g.: q LF g=lvl")); result.IsError = true; return result; }
            if (lines.Count > 16) { result.Content.Add(Text("max 16 commands per call")); result.IsError = true; return result; }
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
            // F15/R056: status per command. All failed → IsError; some → a warning at the top saying which.
            if (failed.Count == lines.Count) result.IsError = true;
            else if (failed.Count > 0) texts.Insert(0, "parcial: " + failed.Count + " of " + lines.Count + " commands failed (no. " + string.Join(", ", failed) + "); the rest are fine");
            var notice = _revit.TakeRetargetNotice();
            if (notice != null) texts.Insert(0, "aviso_instancia: " + notice);
            result.Content.Insert(0, Text(string.Join("\n", texts)));
            return result;
        }

        [McpServerTool(Name = "plan", ReadOnly = true, Idempotent = false, OpenWorld = false)]
        [Description("Prepares changes WITHOUT applying them → diff + plan_id. One operation per line: \"op ids key=value\" (or JSON). " +
                     "set (set_param) $x name=Comments value=REVISAR | snap $flotando | tag $sin_tag view=123 | type $x to=\"Fam: Type\" | elev | ws | " +
                     "move rotate mirror copy delete place tag_move set_values set_type_param set_level group_* view_* sheet_* text_note filled_region. " +
                     "@n = what line n created. One plan = one Undo. Arguments: revit \"guide escritura op=tag\".")]
        public async Task<string> Plan(
            McpServer server,
            [Description("Operations")] string ops,
            [Description("Summary for the user")] string summary = null,
            [Description("Document (dN); default: the active one")] string doc = null,
            [Description("Authorized task (allows commit after reconnecting)")] string tarea = null,
            [Description("true = breakdown of wait and work in Revit (diagnostics)")] bool timing = false,
            CancellationToken ct = default)
        {
            List<object> parsed;
            try { parsed = Command.ParseOps(ops); }
            catch (Exception ex) { return "estado: invalido\naviso: ops could not be understood: " + ex.Message; }
            var a = new Dictionary<string, object> { ["ops"] = parsed };
            if (summary != null) a["summary"] = summary;
            if (doc != null) a[Wire.DocArg] = doc;
            if (tarea != null) a[Wire.TaskArg] = tarea;
            if (timing) a["timing"] = true;
            return await CallText(server, Wire.OpPlan, a, ReadTimeout, ct).ConfigureAwait(false);
        }

        // Idempotent = false: with ops= each call plans and applies again. Only commit plan_id=… (or clave=) is repeatable without effect.
        [McpServerTool(Name = "commit", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Applies a plan to ITS document. retenido = waiting in the Revit queue (do not repeat); conflicto = not applied. What was created: $creados. " +
                     "clave= (idempotency): the same key is never applied twice, even if the connection drops. " +
                     "Without plan_id and with ops= (same format as plan): plans and applies in a single round trip, with the same verifications.")]
        public Task<string> Commit(McpServer server, string plan_id = null, string clave = null, string tarea = null, string reconciliar = null,
            [Description("aplicar (default) | ensayo: applies, verifies and ROLLS BACK; it is not a commit")] string modo = null,
            [Description("Operations (as in plan) to plan and apply in one round trip, without plan_id")] string ops = null,
            [Description("Summary for the user (with ops)")] string summary = null,
            [Description("Document dN (with ops)")] string doc = null,
            [Description("true = breakdown of wait and commit phases (diagnostics)")] bool timing = false,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (timing) a["timing"] = true;
            if (plan_id != null) a["plan_id"] = plan_id;
            if (ops != null)
            {
                try { a["ops"] = Command.ParseOps(ops); }
                catch (Exception ex) { return Task.FromResult("estado: invalido\naviso: ops could not be understood: " + ex.Message); }
                if (summary != null) a["summary"] = summary;
                if (doc != null) a[Wire.DocArg] = doc;
            }
            if (plan_id == null && ops == null) return Task.FromResult("estado: invalido\naviso: give plan_id, or ops to plan and apply in one round trip");
            if (modo != null) a["modo"] = modo;
            if (reconciliar != null) a["reconciliar"] = reconciliar;
            if (clave != null) a[Wire.KeyArg] = clave;
            if (tarea != null) a[Wire.TaskArg] = tarea;
            return CallText(server, Wire.OpCommit, a, WriteTimeout, ct);
        }

        [McpServerTool(Name = "exec", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("C# against the public Revit API (no operation list). Code: expression (doc.Title), body with return, or class with " +
                     "static object Run(ScriptContext ctx). doc, uidoc, app, uiapp, args, ctx already exist (ctx.Mm(), ctx.E(id), ctx.Set(\"$x\"), ctx.SaveSet, ctx.Log). " +
                     "modo=consulta (default: read-only, no transaction) | ensayo (changes and rolls back; requires level W) | ejecutar (tx=auto: one transaction and one Undo; grupo; ninguna: the script opens its own, " +
                     "required for EditFamily/LoadFamily). Returns the value as JSON, Revit warnings and what changed. A long script blocks Revit while it runs.")]
        public Task<string> Exec(
            McpServer server,
            [Description("C# code (or empty with nombre= to repeat a saved one)")] string codigo = null,
            [Description("consulta | ensayo | ejecutar")] string modo = "consulta",
            [Description("auto | grupo | ninguna")] string tx = null,
            [Description("Document dN (default: the active one)")] string doc = null,
            [Description("Arguments for the script (JSON object) → args")] System.Text.Json.JsonElement? args = null,
            [Description("Saved script to repeat, or 'lista'")] string nombre = null,
            [Description("Save this code under that name")] string guardar = null,
            [Description("Idempotency key (writes)")] string clave = null,
            [Description("Authorized task")] string tarea = null,
            [Description(".cs file with the code (instead of codigo=)")] string archivo = null,
            [Description("Extra DLLs, comma-separated (paths)")] string refs = null,
            [Description("Other documents the script touches: d2,d3")] string docs = null,
            [Description("After an interrumpido with clave=: no_aplicado (repeat) | aplicado (mark done)")] string reconciliar = null,
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
                try { a["args"] = Json.Parse(args.Value.GetString()); } catch (Exception ex) { return Task.FromResult("estado: invalido\naviso: args is not JSON: " + ex.Message); }
            }
            return CallText(server, Wire.OpExec, a, WriteTimeout, ct);
        }

        [McpServerTool(Name = "act", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Document actions, one per line (doc=dN pins the target; clave= prevents repeats):\n" +
                     "abrir familia=\"X\" (EditFamily → dN of the RFA) | conector nombre=fria sistema=fria|caliente|sanitario|<PipeSystemType> cara=elem:n " +
                     "radio=6.5mm|diametro= flujo=entrada|salida|bidireccional [cota=radio|diametro] (creates or reconciles by name and verifies) | " +
                     "param nombre=X valor=6.5mm|formula= [tipo=T|todos_tipos=si] [crear=tipo|instancia dato=longitud] [asociar=elem:CONNECTOR_RADIUS] | " +
                     "cargar en=dN [sobrescribir=valores] | guardar [como=path] | sync [comentario=] | pdf conjunto=\"X\"|hojas=$x [carpeta= nombre=] | cerrar\n" +
                     "Long resumable tasks (one command; the engine batches, saves and exports as barriers): tarea iniciar receta=X|archivo=path.cs " +
                     "[id= objetivo= args.field=value | args={json}] [guardar_cada=N al_final=guardar,pdf en_error=continuar espera_s=10] | " +
                     "tarea reanudar|cancelar <id> | tarea reconciliar <id> decision=aplicada|no_aplicada [claves=a,b] | tarea anotar <id> texto=")]
        public async Task<string> Act(
            McpServer server,
            [Description("Actions, e.g. \"conector nombre=fria sistema=fria cara=812:3 radio=6.5mm doc=d3\"")] string c,
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
            if (lines.Count == 0) return "empty command";
            // In order (not in parallel): abrir → conector → cargar depend on each other.
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
                        outs.Add((lines.Count > 1 ? "## ERROR " + line + "\n" : "") + "unknown action: " + verb + ". Use abrir, conector, param, cargar, guardar, sync, pdf, cerrar or tarea");
                        failed++;
                        continue;
                }
                a["accion"] = verb;
                if (verb == "cerrar" && cmd.Positional.Count > 0 && !a.ContainsKey(Wire.DocArg)) a[Wire.DocArg] = cmd.Positional[0];
                if (verb == "abrir" && cmd.Positional.Count > 0 && !a.ContainsKey("familia")) a["familia"] = string.Join(" ", cmd.Positional);
                var resp = await _revit.CallAsync(op, a, op == Wire.OpDoc || op == Wire.OpTask ? LongTimeout : WriteTimeout, ct).ConfigureAwait(false);
                var bad = resp.State != Status.Ok && resp.State != Status.CompletedWithDifferences;
                if (bad) failed++;
                var text = Limit(CompactText.Write(RevitTools.RenderDict(resp)));
                outs.Add(lines.Count > 1 ? "## " + (bad ? "ERROR " : "") + line + "\n" + text : text);
                // If an action fails, the following ones (which usually depend on it) are not attempted.
                if (bad && lines.Count > 1 && outs.Count < lines.Count) { outs.Add("(skipped the remaining " + (lines.Count - outs.Count) + " actions)"); break; }
            }
            if (failed > 0 && lines.Count > 1) outs.Insert(0, "parcial: " + failed + " of " + lines.Count + " failed");
            return string.Join("\n", outs);
        }

        /// <summary>args.field=value → args{field}; args={json} → object. Returns the error or null.</summary>
        internal static string TaskArgs(Dictionary<string, object> a)
        {
            Dictionary<string, object> args = null;
            if (a.TryGetValue("args", out var raw))
            {
                if (raw is Dictionary<string, object> d) args = d;
                else if (raw is string js)
                {
                    try { args = Json.Parse(js) as Dictionary<string, object>; }
                    catch (Exception ex) { return "args is not JSON: " + ex.Message; }
                    if (args == null) return "args must be a JSON object {…}";
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
            var ipcMs = sw.Elapsed.TotalMilliseconds;
            var text = CompactText.Write(RevitTools.RenderDict(resp));
            // What the add-in does not see: named pipe, serialization and the wait until the add-in receives the request.
            if (Arg.GetBool(a, "timing") == true) text = CompactText.Write(new Dictionary<string, object> { ["puente_ipc_total_ms"] = ipcMs }) + "\n" + text;
            text = Limit(text);
            UsageMetrics.Record(_revit.ClientName, op, op, Json.Write(a).Length, text.Length, sw.ElapsedMilliseconds, resp.State);
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
                    // Identity registration: the bridge remembers it to repeat it on reconnect (otherwise the new session would have no name).
                    if (Arg.GetString(a, "agente") is string ag && !string.IsNullOrWhiteSpace(ag)) { _revit.AgentName = ag; _revit.AgentSource = "registro"; }
                    if (Arg.GetString(a, "conversacion") is string cv0 && !string.IsNullOrWhiteSpace(cv0)) _revit.Conversation = cv0;
                    if (Arg.GetString(a, "modelo") is string md0 && !string.IsNullOrWhiteSpace(md0)) _revit.Model = md0;
                    break;
                case "docs": op = Wire.OpDocs; break;
                case "fam":
                    op = Wire.OpFam;
                    var fv = (P(0) ?? "info").ToLowerInvariant();
                    if (fv != "info" && fv != "caras") return ("revit fam only reads (info, caras). For abrir, conector, param and cargar use the act tool.", null, null, true);
                    a["accion"] = fv;
                    if (P(1) != null && !a.ContainsKey("familia")) a["familia"] = P(1);
                    break;
                case "res":
                    {
                        var id = P(0) ?? Arg.GetString(a, "id");
                        var from = (int)(Arg.GetLong(a, "desde") ?? 0);
                        var n = (int)(Arg.GetLong(a, "count") ?? Arg.GetLong(a, "lineas") ?? 300);
                        var txt = ResultStore.Read(id, from, n, out var total);
                        if (txt == null) return ("result does not exist: " + id + " (results are kept for " + ResultStore.KeepDays + " days)", null, null, true);
                        var end = Math.Min(total, from + n);
                        return (txt + (end < total ? "\n… lines " + from + "–" + end + " of " + total + "; continue with: res " + id + " desde=" + end + " n=" + n : ""), null, null, false);
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
                    if (!a.ContainsKey("page_size")) a["page_size"] = 25L; // the rest stays in $sets
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
                    if (OpSemantics.TaskWrites.Contains(tv)) return ("revit only reads tasks (estado, resumen, esperar, unidades, lista, recetas). For iniciar, reanudar, cancelar or reconciliar use act \"tarea " + tv + " …\".", null, null, true);
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
                    if (!a.ContainsKey("px")) a["px"] = 1000L; // ~0.5× image tokens compared with 1400; px=1600 for detail
                    break;
                default:
                    return ("unknown command: " + cmd.Verb + ". Use: ctx, docs, fam, res, cat, q, get, chk, geo, sets, job, show, cap, tarea, guide (changes: plan, exec, act)", null, null, true);
            }

            var ipc = System.Diagnostics.Stopwatch.StartNew();
            var resp = await _revit.CallAsync(op, a, timeout, ct).ConfigureAwait(false);
            ipc.Stop();
            if (op == Wire.OpQuery && Arg.GetBool(a, "digest") == true && resp.State == Status.Ok
                && (!(resp.Data is Dictionary<string, object> digestData) || !digestData.ContainsKey("formato_digest")))
                return ("The connected add-in does not support query digest.", null, null, true);
            var o = RevitTools.RenderDict(resp);
            byte[] img = null; string mime = null;
            if (op == Wire.OpCapture)
            {
                mime = o.TryGetValue("mime", out var m) ? m as string ?? "image/png" : "image/png";
                if (resp.State == Status.Ok && o.TryGetValue("archivo", out var f) && f is string path)
                {
                    try { img = File.ReadAllBytes(path); } catch (Exception ex) { o["aviso"] = "image not readable: " + ex.Message; }
                }
                o.Remove("mime"); o.Remove("archivo"); o.Remove("bytes");
            }
            o.Remove("ms");
            o.TryGetValue("tiempos_consulta", out var queryTiming);
            o.Remove("tiempos_consulta");
            var text = CompactText.Write(o);

            // Same command and same reply as before in this conversation → do not repeat the text, but keep it retrievable (F14):
            // a client that compacted its context rereads it with res <id>.
            if (img == null && text.Length > 200)
            {
                var n = Interlocked.Increment(ref _seq);
                if (_last.TryGetValue(line, out var prev) && prev.text == text)
                {
                    var id = prev.id ?? ResultStore.Save(text, "b");
                    _last[line] = (prev.n, prev.text, id);
                    text = "unchanged: same as the previous reply to this same command" + (o.TryGetValue("rev", out var rv) ? " (rev " + rv + ")" : "") +
                           "; full text: res " + id;
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
            return (text, img, mime, resp.State != Status.Ok && resp.State != Status.CompletedWithDifferences);
        }
    }
}
