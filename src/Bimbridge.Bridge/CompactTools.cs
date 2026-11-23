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
    /// Commands, argument names and values from 0.9.3 and earlier (Spanish) are still accepted: they are normalized by
    /// <see cref="LegacyAliases"/> as each command is parsed (and tool arguments by <see cref="LegacyToolArgs"/>).
    /// </summary>
    [McpServerToolType]
    public sealed class CompactTools
    {
        public const string Instructions =
            "Revit is open (projects and families, handle doc=dN). Read with revit (one-line commands; several lines = in parallel). Change with " +
            "plan→commit (typed, one Undo), exec (C# against the Revit API) or act (families, connectors, save, sync, PDF). " +
            "If the user authorized the task, apply without asking for confirmation per batch. Use other tools the user authorizes if something is not covered. " +
            "User pause: stop. held: waiting in the Revit queue, do not repeat. key= prevents repeating a write. Help: revit \"guide\". Documents and geometry without Revit (PDF, DOCX, plan–model registration → ops): power \"cap\". " +
            "$sel-xxxxxxxxxxxx = elements the user selected in Revit (Copy selection ID button): use it wherever ids or a set are accepted.";

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
                     "ctx [instance=2024] | docs | fam info|faces|search [family=X] | res <id> from= | " +
                     "cat [summary|levels|views|sheets|types|families|templates|worksets|links|filters|categories] | " +
                     "q <cat> lvl= t= ids= v= f= g=fields n=true digest=true timing=true s=$name elev>9 type~LT u=mm | get <ids|$x> [f=*] | " +
                     "chk lighting_audit | chk schedule_audit schedule=<id or name> | geo <recipe> <cat> lvl= | sets | job <id> | show <ids> | cap [view] color_by=categories highlight=$x | " +
                     "task status|summary|wait|units|list|recipes <id> [from=rev max=s] | guide [topic]\n" +
                     "cat: LF fixtures, LD devices, EF receptacles, EE panels, FA fire alarm. Compact text: fixed = shared value; col# = dictionary.")]
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
            return LegacyAliases.Token((i < 0 ? t : t.Substring(0, i)).ToLowerInvariant());
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
            else if (failed.Count > 0) texts.Insert(0, "partial: " + failed.Count + " of " + lines.Count + " commands failed (no. " + string.Join(", ", failed) + "); the rest are fine");
            var notice = _revit.TakeRetargetNotice();
            if (notice != null) texts.Insert(0, "instance_notice: " + notice);
            result.Content.Insert(0, Text(string.Join("\n", texts)));
            return result;
        }

        [McpServerTool(Name = "plan", ReadOnly = true, Idempotent = false, OpenWorld = false)]
        [Description("Prepares changes WITHOUT applying them → diff + plan_id. One operation per line: \"op ids key=value\" (or JSON). " +
                     "set (set_param) $x name=Comments value=REVIEW | snap $floating | tag $untagged view=123 | type $x to=\"Fam: Type\" | elev | ws | " +
                     "move rotate mirror copy delete place tag_move set_values set_type_param set_level group_* view_* sheet_* text_note filled_region. " +
                     "@n = what line n created. One plan = one Undo. Arguments: revit \"guide write op=tag\".")]
        public async Task<string> Plan(
            McpServer server,
            [Description("Operations")] string ops,
            [Description("Summary for the user")] string summary = null,
            [Description("Document (dN); default: the active one")] string doc = null,
            [Description("Authorized task (allows commit after reconnecting)")] string task = null,
            [Description("true = breakdown of wait and work in Revit (diagnostics)")] bool timing = false,
            CancellationToken ct = default)
        {
            List<object> parsed;
            try { parsed = Command.ParseOps(ops); }
            catch (Exception ex) { return "status: invalid\nwarning: ops could not be understood: " + ex.Message; }
            var a = new Dictionary<string, object> { ["ops"] = parsed };
            if (summary != null) a["summary"] = summary;
            if (doc != null) a[Wire.DocArg] = doc;
            if (task != null) a[Wire.TaskArg] = task;
            if (timing) a["timing"] = true;
            LegacyAliases.NormalizeArgs(Wire.OpPlan, a);
            return await CallText(server, Wire.OpPlan, a, ReadTimeout, ct).ConfigureAwait(false);
        }

        // Idempotent = false: with ops= each call plans and applies again. Only commit plan_id=… (or key=) is repeatable without effect.
        [McpServerTool(Name = "commit", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Applies a plan to ITS document. held = waiting in the Revit queue (do not repeat); conflict = not applied. What was created: $created. " +
                     "key= (idempotency): the same key is never applied twice, even if the connection drops. " +
                     "Without plan_id and with ops= (same format as plan): plans and applies in a single round trip, with the same verifications.")]
        public Task<string> Commit(McpServer server, string plan_id = null, string key = null, string task = null, string reconcile = null,
            [Description("apply (default) | trial: applies, verifies and ROLLS BACK; it is not a commit")] string mode = null,
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
                catch (Exception ex) { return Task.FromResult("status: invalid\nwarning: ops could not be understood: " + ex.Message); }
                if (summary != null) a["summary"] = summary;
                if (doc != null) a[Wire.DocArg] = doc;
            }
            if (plan_id == null && ops == null) return Task.FromResult("status: invalid\nwarning: give plan_id, or ops to plan and apply in one round trip");
            if (mode != null) a["mode"] = mode;
            if (reconcile != null) a["reconcile"] = reconcile;
            if (key != null) a[Wire.KeyArg] = key;
            if (task != null) a[Wire.TaskArg] = task;
            LegacyAliases.NormalizeArgs(Wire.OpCommit, a);
            return CallText(server, Wire.OpCommit, a, WriteTimeout, ct);
        }

        [McpServerTool(Name = "exec", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("C# against the public Revit API (no operation list). Code: expression (doc.Title), body with return, or class with " +
                     "static object Run(ScriptContext ctx). doc, uidoc, app, uiapp, args, ctx already exist (ctx.Mm(), ctx.E(id), ctx.Set(\"$x\"), ctx.SaveSet, ctx.Log). " +
                     "mode=query (default: read-only, no transaction) | trial (changes and rolls back; requires level W) | run (tx=auto: one transaction and one Undo; group; none: the script opens its own, " +
                     "required for EditFamily/LoadFamily). Returns the value as JSON, Revit warnings and what changed. A long script blocks Revit while it runs.")]
        public Task<string> Exec(
            McpServer server,
            [Description("C# code (or empty with name= to repeat a saved one)")] string code = null,
            [Description("query | trial | run")] string mode = "query",
            [Description("auto | group | none")] string tx = null,
            [Description("Document dN (default: the active one)")] string doc = null,
            [Description("Arguments for the script (JSON object) → args")] System.Text.Json.JsonElement? args = null,
            [Description("Saved script to repeat, or 'list'")] string name = null,
            [Description("Save this code under that name")] string save_as = null,
            [Description("Idempotency key (writes)")] string key = null,
            [Description("Authorized task")] string task = null,
            [Description(".cs file with the code (instead of code=)")] string file = null,
            [Description("Extra DLLs, comma-separated (paths)")] string refs = null,
            [Description("Other documents the script touches: d2,d3")] string docs = null,
            [Description("After an interrupted with key=: not_applied (repeat) | applied (mark done)")] string reconcile = null,
            CancellationToken ct = default)
        {
            var a = new Dictionary<string, object>();
            if (file != null) a["file"] = file;
            if (refs != null) a["refs"] = refs.Split(',').Select(x => (object)x.Trim()).Where(x => ((string)x).Length > 0).ToList();
            if (docs != null) a["docs"] = docs.Split(',').Select(x => (object)x.Trim()).Where(x => ((string)x).Length > 0).ToList();
            if (reconcile != null) a["reconcile"] = reconcile;
            if (name != null && code == null && LegacyAliases.Token(name.Trim().ToLowerInvariant()) == "list") a["action"] = "list";
            else if (name != null) a["name"] = name;
            if (code != null) a["code"] = code;
            if (mode != null) a["mode"] = mode;
            if (tx != null) a["tx"] = tx;
            if (doc != null) a[Wire.DocArg] = doc;
            if (save_as != null) a["save_as"] = save_as;
            if (key != null) a[Wire.KeyArg] = key;
            if (task != null) a[Wire.TaskArg] = task;
            if (args.HasValue && args.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try { a["args"] = Json.Parse(args.Value.GetRawText()); } catch (Exception) { }
            }
            else if (args.HasValue && args.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                try { a["args"] = Json.Parse(args.Value.GetString()); } catch (Exception ex) { return Task.FromResult("status: invalid\nwarning: args is not JSON: " + ex.Message); }
            }
            LegacyAliases.NormalizeArgs(Wire.OpExec, a);
            return CallText(server, Wire.OpExec, a, WriteTimeout, ct);
        }

        [McpServerTool(Name = "act", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Document actions, one per line (doc=dN pins the target; key= prevents repeats):\n" +
                     "open family=\"X\" (EditFamily → dN of the RFA) | connector name=cold system=cold|hot|sanitary|<PipeSystemType> face=elem:n " +
                     "radius=6.5mm|diameter= flow=in|out|bidirectional [dimension=radius|diameter] (creates or reconciles by name and verifies) | " +
                     "param name=X value=6.5mm|formula= [type=T|all_types=yes] [create=type|instance data_type=length] [associate=elem:CONNECTOR_RADIUS] | " +
                     "load into=dN [overwrite=values] | save [as=path] | sync [comment=] | pdf sheet_set=\"X\"|sheets=$x [folder= name=] | close\n" +
                     "Long resumable tasks (one command; the engine batches, saves and exports as barriers): task start recipe=X|file=path.cs " +
                     "[id= target= args.field=value | args={json}] [save_every=N at_end=save,pdf on_error=continue wait_s=10] | " +
                     "task resume|cancel <id> | task reconcile <id> decision=applied|not_applied [keys=a,b] | task note <id> text=")]
        public async Task<string> Act(
            McpServer server,
            [Description("Actions, e.g. \"connector name=cold system=cold face=812:3 radius=6.5mm doc=d3\"")] string c,
            CancellationToken ct = default)
        {
            if (server?.ClientInfo?.Name is string cn && cn.Length > 0) _revit.ClientName = cn; if (server?.ClientInfo?.Version is string clientVer && clientVer.Length > 0) _revit.ClientVersion = clientVer;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = await ActInner(c, ct).ConfigureAwait(false);
            UsageMetrics.Record(_revit.ClientName, "act", FirstVerb(c), c?.Length ?? 0, text.Length, sw.ElapsedMilliseconds, text.StartsWith("partial", StringComparison.Ordinal) || text.Contains("status: ") ? "with_errors" : "ok");
            return text;
        }

        private async Task<string> ActInner(string c, CancellationToken ct)
        {
            var lines = Command.SplitLines(c);
            if (lines.Count == 0) return "empty command";
            // In order (not in parallel): open → connector → load depend on each other.
            var outs = new List<string>();
            var failed = 0;
            foreach (var line in lines)
            {
                var cmd = Command.Parse(line);
                var a = cmd.Args;
                string op;
                var verb = LegacyAliases.Token(cmd.Verb);
                switch (verb)
                {
                    case "open": case "connector": case "param": case "load": op = Wire.OpFam; break;
                    case "save": case "save_as": case "sync": case "pdf": case "close": op = Wire.OpDoc; break;
                    case "task":
                    {
                        op = Wire.OpTask;
                        verb = LegacyAliases.Token((cmd.Positional.Count > 0 ? cmd.Positional[0] : "status").ToLowerInvariant());
                        if (cmd.Positional.Count > 1 && !a.ContainsKey("id")) a["id"] = cmd.Positional[1];
                        if (a.TryGetValue("count", out var cnt)) { a["n"] = cnt; a.Remove("count"); }
                        LegacyAliases.NormalizeArgs(op, a);
                        var err = TaskArgs(a);
                        if (err != null) { outs.Add((lines.Count > 1 ? "## ERROR " + line + "\n" : "") + err); failed++; continue; }
                        break;
                    }
                    default:
                        outs.Add((lines.Count > 1 ? "## ERROR " + line + "\n" : "") + "unknown action: " + verb + ". Use open, connector, param, load, save, sync, pdf, close or task");
                        failed++;
                        continue;
                }
                LegacyAliases.NormalizeArgs(op, a);
                a["action"] = verb;
                if (verb == "close" && cmd.Positional.Count > 0 && !a.ContainsKey(Wire.DocArg)) a[Wire.DocArg] = cmd.Positional[0];
                if (verb == "open" && cmd.Positional.Count > 0 && !a.ContainsKey("family")) a["family"] = string.Join(" ", cmd.Positional);
                var resp = await _revit.CallAsync(op, a, op == Wire.OpDoc || op == Wire.OpTask ? LongTimeout : WriteTimeout, ct).ConfigureAwait(false);
                var bad = resp.State != Status.Ok && resp.State != Status.CompletedWithDifferences;
                if (bad) failed++;
                var text = Limit(CompactText.Write(RevitTools.RenderDict(resp)));
                outs.Add(lines.Count > 1 ? "## " + (bad ? "ERROR " : "") + line + "\n" + text : text);
                // If an action fails, the following ones (which usually depend on it) are not attempted.
                if (bad && lines.Count > 1 && outs.Count < lines.Count) { outs.Add("(skipped the remaining " + (lines.Count - outs.Count) + " actions)"); break; }
            }
            if (failed > 0 && lines.Count > 1) outs.Insert(0, "partial: " + failed + " of " + lines.Count + " failed");
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
            if (Arg.GetBool(a, "timing") == true) text = CompactText.Write(new Dictionary<string, object> { ["bridge_ipc_total_ms"] = ipcMs }) + "\n" + text;
            text = Limit(text);
            UsageMetrics.Record(_revit.ClientName, op, op, Json.Write(a).Length, text.Length, sw.ElapsedMilliseconds, resp.State);
            return text;
        }

        private static TextContentBlock Text(string s) => new TextContentBlock { Text = s };

        private async Task<(string text, byte[] image, string mime, bool error)> RunLine(string line, CancellationToken ct)
        {
            var cmd = Command.Parse(line);
            var a = cmd.Args;
            LegacyAliases.NormalizeArgs(null, a);
            string P(int i) => i < cmd.Positional.Count ? cmd.Positional[i] : null;
            // First positional word of verbs whose sub-command or kind is a vocabulary token (fam faces, cat levels, geo clearance…).
            string Sub(int i) => P(i) == null ? null : LegacyAliases.Token(P(i).ToLowerInvariant());
            string op;
            var timeout = ReadTimeout;
            var verb = LegacyAliases.Token(cmd.Verb);
            switch (verb)
            {
                case "metrics":
                    return (CompactText.Write(UsageMetrics.Summary()), null, null, false);
                case "guide": case "help":
                    return (Guides.Get(Sub(0), Arg.GetString(a, "op")), null, null, false);
                case "ctx": case "context":
                    op = Wire.OpContext;
                    var inst = Arg.GetString(a, "instance");
                    if (!string.IsNullOrWhiteSpace(inst))
                    {
                        var why = await _revit.SwitchToAsync(inst.Trim(), ct).ConfigureAwait(false);
                        if (why != null) return (why, null, null, true);
                        a.Remove("instance");
                    }
                    // Identity registration: the bridge remembers it to repeat it on reconnect (otherwise the new session would have no name).
                    if (Arg.GetString(a, "agent") is string ag && !string.IsNullOrWhiteSpace(ag)) { _revit.AgentName = ag; _revit.AgentSource = AgentIdentity.SourceRegister; }
                    if (Arg.GetString(a, "conversation") is string cv0 && !string.IsNullOrWhiteSpace(cv0)) _revit.Conversation = cv0;
                    if (Arg.GetString(a, "model") is string md0 && !string.IsNullOrWhiteSpace(md0)) _revit.Model = md0;
                    break;
                case "docs": op = Wire.OpDocs; break;
                case "fam":
                    op = Wire.OpFam;
                    var fv = Sub(0) ?? "info";
                    if (!OpSemantics.FamReads.Contains(fv)) return ("revit fam only reads (" + string.Join(", ", OpSemantics.FamReads) + "). For open, connector, param and load use the act tool.", null, null, true);
                    a["action"] = fv;
                    if (P(1) != null && !a.ContainsKey("family")) a["family"] = P(1);
                    break;
                case "res":
                    {
                        var id = P(0) ?? Arg.GetString(a, "id");
                        var from = (int)(Arg.GetLong(a, "from") ?? 0);
                        var n = (int)(Arg.GetLong(a, "count") ?? Arg.GetLong(a, "lines") ?? 300);
                        var txt = ResultStore.Read(id, from, n, out var total);
                        if (txt == null) return ("result does not exist: " + id + " (results are kept for " + ResultStore.KeepDays + " days)", null, null, true);
                        var end = Math.Min(total, from + n);
                        return (txt + (end < total ? "\n… lines " + from + "–" + end + " of " + total + "; continue with: res " + id + " from=" + end + " n=" + n : ""), null, null, false);
                    }
                case "cat": case "catalog":
                    op = Wire.OpCatalog;
                    if (P(0) != null && !a.ContainsKey("kind")) a["kind"] = Sub(0);
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
                    if (!a.ContainsKey("name")) a["name"] = Sub(0) ?? "lighting_audit";
                    else a["name"] = LegacyAliases.Token(Arg.GetString(a, "name"));
                    break;
                case "geo":
                    op = Wire.OpGeo;
                    if (!a.ContainsKey("name") && P(0) != null) a["name"] = Sub(0);
                    if (P(1) != null && !a.ContainsKey("cat")) a["cat"] = Command.CatAlias(P(1));
                    if (!a.ContainsKey("page_size")) a["page_size"] = 25L; // the rest stays in $sets
                    break;
                case "sets": op = Wire.OpSets; if (!a.ContainsKey("op")) a["op"] = Sub(0) ?? "list"; if (P(1) != null) a["name"] = P(1); break;
                case "job":
                    op = Wire.OpJob;
                    if (P(0) != null) { if (P(0).StartsWith("P-", StringComparison.OrdinalIgnoreCase) || P(0).StartsWith("plan", StringComparison.OrdinalIgnoreCase)) a["plan_id"] = P(0); else a["job_id"] = P(0); }
                    break;
                case "task": case "tasks":
                {
                    op = Wire.OpTask;
                    var tv = Sub(0) ?? (verb == "tasks" ? "list" : "status");
                    if (OpSemantics.TaskWrites.Contains(tv)) return ("revit only reads tasks (" + string.Join(", ", OpSemantics.TaskReads) + "). For start, resume, cancel or reconcile use act \"task " + tv + " …\".", null, null, true);
                    a["action"] = tv;
                    if (P(1) != null && !a.ContainsKey("id")) a["id"] = P(1);
                    if (a.TryGetValue("count", out var cnt)) { a["n"] = cnt; a.Remove("count"); }
                    if (tv == "wait") timeout = TimeSpan.FromSeconds(Math.Min(120, Arg.GetLong(a, "max") ?? 30) + 20);
                    break;
                }
                case "show": op = Wire.OpShow; if (P(0) != null && !a.ContainsKey("ids")) a["ids"] = P(0); break;
                case "cap": case "capture":
                    op = Wire.OpCapture; timeout = CaptureTimeout;
                    if (P(0) != null && !a.ContainsKey("view") && long.TryParse(P(0), out var vid)) a["view"] = vid;
                    if (!a.ContainsKey("px")) a["px"] = 1000L; // ~0.5× image tokens compared with 1400; px=1600 for detail
                    break;
                default:
                    return ("unknown command: " + cmd.Verb + ". Use: ctx, docs, fam, res, cat, q, get, chk, geo, sets, job, show, cap, task, guide (changes: plan, exec, act)", null, null, true);
            }

            var ipc = System.Diagnostics.Stopwatch.StartNew();
            var resp = await _revit.CallAsync(op, a, timeout, ct).ConfigureAwait(false);
            ipc.Stop();
            if (op == Wire.OpQuery && Arg.GetBool(a, "digest") == true && resp.State == Status.Ok
                && (!(resp.Data is Dictionary<string, object> digestData) || !digestData.ContainsKey("digest_format")))
                return ("The connected add-in does not support query digest.", null, null, true);
            var o = RevitTools.RenderDict(resp);
            byte[] img = null; string mime = null;
            if (op == Wire.OpCapture)
            {
                mime = o.TryGetValue("mime", out var m) ? m as string ?? "image/png" : "image/png";
                if (resp.State == Status.Ok && o.TryGetValue("file", out var f) && f is string path)
                {
                    try { img = File.ReadAllBytes(path); } catch (Exception ex) { o["warning"] = "image not readable: " + ex.Message; }
                }
                o.Remove("mime"); o.Remove("file"); o.Remove("bytes");
            }
            o.Remove("ms");
            o.TryGetValue("query_timings", out var queryTiming);
            o.Remove("query_timings");
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
                var timing = new Dictionary<string, object> { ["bridge_ipc_total_ms"] = ipc.Elapsed.TotalMilliseconds };
                if (queryTiming != null) timing["revit"] = queryTiming;
                text = CompactText.Write(timing) + "\n" + text;
            }
            text = Limit(text);
            return (text, img, mime, resp.State != Status.Ok && resp.State != Status.CompletedWithDifferences);
        }
    }

    /// <summary>
    /// LEGACY: MCP tool arguments sent with their 0.9.3 (Spanish) names (clave=, tarea=, modo=, codigo=…) are renamed to the current
    /// ones before the SDK binds them. Remove after 0.9.5.
    /// </summary>
    public static class LegacyToolArgs
    {
        public static void Normalize(CallToolRequestParams p)
        {
            if (p?.Arguments == null || p.Arguments.Count == 0) return;
            var overrides = LegacyAliases.ToolArgOverrides(p.Name);
            foreach (var k in p.Arguments.Keys.ToList())
            {
                var target = overrides != null && overrides.TryGetValue(k, out var o) ? o : LegacyAliases.Token(k);
                if (target == k) continue;
                if (!p.Arguments.ContainsKey(target)) p.Arguments[target] = p.Arguments[k];
                p.Arguments.Remove(k);
            }
        }
    }
}
