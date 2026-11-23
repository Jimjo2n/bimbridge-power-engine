// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// On-demand guides (guide tool). Tool descriptions are short because they are loaded in every session;
    /// the detail lives here and the AI only requests it when needed (and only the topic or operation it needs).
    /// </summary>
    public static class Guides
    {
        public static readonly string[] Topics = { "workflow", "query", "catalog", "geo", "write", "exec", "families", "document", "recovery", "tasks", "recipes", "capture", "batch" };

        public static string Get(string topic, string op = null)
        {
            switch (LegacyAliases.Token((topic ?? "workflow").Trim().ToLowerInvariant()))
            {
                case "workflow": return Flow;
                case "query":  return Query;
                case "catalog":  return Catalog;
                case "geo": case "geometry":  return Geo;
                case "write": case "plan": return OpCatalog.Guide(op);
                case "capture":  return CaptureText;
                case "batch": return Batch;
                case "exec": case "code":  return Exec;
                case "families": case "family": case "fam": case "connector": return Families;
                case "document": case "doc": case "pdf": case "sync": case "save": return DocOps;
                case "recovery":  case "key": return Recovery;
                case "task": case "tasks": return Tasks;
                case "recipe": case "recipes": return Recipes;
                default: return "Topics: " + string.Join(", ", Topics) + ". write accepts op=tag,place… to show only those operations.";
            }
        }

        private const string Flow =
            "RECOMMENDED WORKFLOW (fewer calls, less text):\n" +
            "1. revit_context: version, document, rev. If several Revit instances are open, they appear in 'instances' (switch with instance=<pid>).\n" +
            "2. catalog kind=summary: levels, views, sheets, links and categories with counts, in one call.\n" +
            "3. query with count=true or group_by=lvl,type to summarize; request rows only if you will use them and only the fields you need (f=id,type).\n" +
            "4. Save results with save_as=$name and reuse them (ids=$name) in query, get, geo, plan, show and capture.\n" +
            "5. Several independent reads → batch (a single call).\n" +
            "6. To SEE the view: capture (image) with color_by=categories to tell fixtures from receptacles and switches; highlight=$set.\n" +
            "7. Real physics (heights, embedded, floating, clashes): geo. Repeating without model changes is instant (cache).\n" +
            "8. Changes: plan → commit (typed, diff, a single Undo) for what its operations cover; exec (C#) for everything else; act/fam and doc for families, " +
            "connectors, save, sync and PDF. If the task is authorized, apply without asking for confirmation on each batch; otherwise, show the diff first. " +
            "status=held = waiting in the Revit queue; do not repeat. User pause = stop.\n" +
            "9. show ids=… to point the user at the elements on screen (selects and zooms).\n" +
            "Statuses: read 'warning' and follow its instruction. job waits for long jobs (job_id).";

        private const string Exec =
            "EXEC (general C# over the public Revit API; compiled with Roslyn 4.8 inside Revit):\n" +
            "Forms: expression (doc.Title) | body with return | class with public static object Run(ScriptContext ctx). using directives at the top are hoisted automatically.\n" +
            "Already available: doc, uidoc, app, uiapp, args (input JSON object), ctx. ctx.Mm(6.5) mm→feet, ctx.ToMm(feet), ctx.E(id), ctx.Set(\"$x\"), " +
            "ctx.SaveSet(\"$y\", ids), ctx.Log(x), ctx.DocOf(\"d3\"), ctx.ThrowIfCancelled().\n" +
            "mode=query (default): runs inside a transaction that is ALWAYS rolled back; use it to measure or test without touching the model.\n" +
            "mode=run tx=auto: one transaction, one Undo | tx=group: several script transactions in a group | tx=none: the script opens its own " +
            "(required for EditFamily, LoadFamily, Save, SaveAs). Requires permission W (or R for a query).\n" +
            "Returns: value as JSON (ElementId → number, XYZ → [x,y,z] in feet, Element → id/cat/name), log, Revit warnings grouped by type, " +
            "elements created/changed/deleted per document ($exec_cambiados). Errors include the script line. Every script is audited (hash + file).\n" +
            "save=name saves it; name=X repeats it; name=list. A script runs on the Revit thread: Revit does not respond while it runs.";

        private const string Families =
            "FAMILIES (act / fam). Typical washbasin workflow:\n" +
            "1. revit \"fam info family=X\" (types, parameters, formulas, connectors) and \"fam faces family=X\" (planar faces element:n with normal and center).\n" +
            "2. act \"open family=X\" → dN handle of the RFA (reuses the open one).\n" +
            "3. act \"connector name=cold system=cold face=812:3 radius=6.5mm doc=dN\" (radius or diameter ALWAYS with a unit; none is assumed). " +
            "Searches by name (connector description), adopts an unnamed one on that face or creates it; never duplicates. flow=in|out|bidirectional only if it was decided.\n" +
            "4. act \"param name=X value=6.5mm\" | formula= | create=type|instance data_type=length | associate=elem:CONNECTOR_RADIUS. If a family parameter drives " +
            "the radius, it is reported as pending: the association is not broken.\n" +
            "5. act \"save doc=dN\" and \"load en=d1 doc=dN overwrite=values\": reloads into the project and re-reads the connectors from the instances.\n" +
            "Repeating 3–5 with the same key= does not apply twice.";

        private const string DocOps =
            "DOCUMENT (act or doc_op): save | save_as as=path [overwrite=yes] | sync [comment=] [relinquish=si] | " +
            "pdf sheet_set=\"NON-UNIT LIGHTING\" or sheets=$x|10E.01.NUL,10E.02.NUL [folder=] [name=] | " +
            "ifc [version=ifc2x3|ifc4|ifc4dtv] [view=] [quantities=si] [folder=] [name=] | schedule_csv schedule=<name|id> [folder=] [name=] | close [save=si|discard=si].\n" +
            "Each one runs as a job (job_id if it takes a while) and is VERIFIED: file exists, new timestamp, size, SHA-256, PDF pages; sync confirms by event. " +
            "Does not close the active document (Revit does not allow it through the API). Permission D or W.";

        private const string Recovery =
            "RECOVERY: key=<text> on commit, exec run, act and doc_op records it in an on-disk journal (operations.jsonl).\n" +
            "Same key already done → returns the previous result without repeating. Interrupted (Revit closed midway) → status=interrupted: review the model " +
            "and repeat with reconcile=si. Same key with different content → invalid.\n" +
            "task=<name> on plan/commit/exec: that task's jobs are not cancelled when the conversation drops, and another conversation with the same task " +
            "can query them (job) and apply its plans. Documents: doc=dN pins the target; if that document was closed → target_unavailable (it never switches documents on its own).";

        private const string Tasks =
            "LONG TASKS (0.6.0): act \"task start recipe=<name>|file=<path.cs> id=<id> target=… args.field=value [save_every=N] [at_end=save,pdf] [on_error=continue]\".\n" +
            "ONE command: the engine enumerates the recipe's units, runs them in measured batches (one transaction and one Undo per unit), yields Revit between visits, " +
            "writes a durable checkpoint after each batch, saves every N and runs the final barriers only once everything before them is committed. Replies after ~10 s (wait_s=) with the status.\n" +
            "Follow up: revit \"task wait <id> from=<rev> max=60\" (waits for changes without polling) | task status|summary|units <id> [status=conflict] | task list.\n" +
            "Another client (Codex↔Claude) picks it up with the id: task summary <id> gives where it stopped, what is left and the decisions; no code needs to be resent.\n" +
            "Statuses: queued, running_now, waiting_for_context (pause/Hold/dialog/closed document: resumes on its own), applied, applied_with_differences, partial, interrupted, cancelled.\n" +
            "interrupted/partial: act \"task resume <id>\" verifies what is uncertain (UniqueId of what was created or the recipe's Verify; after a reopen, anything after the last save of that opening) " +
            "and continues without duplicating. What cannot be proven stays unknown: act \"task reconcile <id> decision=applied|not_applied keys=…\". " +
            "Cancel: act \"task cancel <id>\" (between units; what is committed stays). While it runs, the document is taken: other writes → document_busy (exec/commit with task=<id> do go through).";

        private const string Recipes =
            "RECIPES: C# class that inherits from Recipe (by file in %LOCALAPPDATA%\\Bimbridge\\recetas\\<name>.cs, in the add-in's recipes\\, or as code). task recipes lists them.\n" +
            "public class X : Recipe { public override string Version => \"1\"; public override IEnumerable<string> Inputs => new[]{\"level\"};\n" +
            "  public override IEnumerable<Unit> Units(ScriptContext ctx) { foreach (var e in …) yield return new Unit(\"light:\" + e.UniqueId, data, new[]{ ctx.Long(e.Id) }); yield return Unit.Save(); }\n" +
            "  public override object Run(ScriptContext ctx, Unit u) { … return summary; }   // inside its transaction; ctx.Skip(\"reason\") skips it\n" +
            "  public override Verification Verify(ScriptContext ctx, Unit u) => …;   // after a crash: was it applied?\n" +
            "  public override object Postcondition(ScriptContext ctx) => new { deviations = … }; }   // final read-only QC (rolled back)\n" +
            "STABLE keys (UniqueId, not ElementId). Unit ids = what it reads/changes: if someone else changes them first, the unit becomes a conflict (nothing is overwritten). " +
            "ctx.Decision(text), ctx.Exception(reason, detail), ctx.Artifact(name, content) are kept in the task memory. Transaction => TxPolicy.None if the recipe opens its own (EditFamily/LoadFamily). " +
            "The task keeps an exact copy of the recipe (with its SHA-256): changing the file does not alter tasks already started.\n" +
            "BUILT-IN: tag_lights (view, tag_type_name, save, pdf_sheets), batch_parameter (ids|sheet_set, parameter, value), qc_manifest (JSON manifest or path; CSV of deviations), " +
            "mount_supports (flush/embedded/separated/no_reference, includes links), export_sheets (sheet_set|sheets, before=save|sync).\n" +
            "Bim.* HELPERS (in scripts and recipes, mm): Groups.Inspect/Signature/Compose/ChangeType/VerifyComposition; Mounting.Body/Support/View3D/Transformation; " +
            "Views.Range/ApplyRange/CompareTemplate/WhyNotVisible/FilterByCategory; Manifest.Collect/Qc. Those that write run inside the engine transaction.";

        private const string Query =
            "QUERY (query): cat (LightingFixtures, ElectricalFixtures, LightingDevices…), lvl, type (contains), ws, ids/$set, view (visible in that view).\n" +
            "where: field=value, !=, >, <, >=, <=, ~ (contains), field? (not empty), !field (empty). E.g.: where=[\"elev>9\",\"type~LT-06\"].\n" +
            "f: id,cat,fam,type,lvl,ws,elev,mark,comments,host,x,y,z,name,uid,view or any parameter by name.\n" +
            "group_by (up to 3 fields) counts by group; count=true only the total; page/page_size (max. 200); since=rev only what changed afterwards.\n" +
            "Several pages: the response carries cursor=q…; request the next ones with cursor=q… page=N (without re-reading the model). " +
            "If the document changed, the cursor is rejected (conflict) and the query must be repeated; it expires after 5 min unused.\n" +
            "units=mm converts elev,x,y,z (also in where). full=true does not truncate texts.\n" +
            "get ids=… f=*: ALL parameters (instance and type) of up to 10 elements; what they share is printed once in 'common'. search=text filters names.";

        private const string Catalog =
            "CATALOG (catalog kind=…): summary | categories | types | families | levels | views | templates | sheets | worksets | links | filters.\n" +
            "cat filters types and families; lvl and type (plan, rcp, section, elevation, 3d, legend, schedule…) filter views; search=text in any column.\n" +
            "f adds parameters by name to views and sheets (e.g. f=Sub-Discipline,Set Group or f=Sheet Set,Drawn By).\n" +
            "levels: elev = displayed elevation; z = internal elevation (the one used by x,y,z). views: sheet = number of the sheet it is placed on.";

        private const string Geo =
            "GEO (real meshes of the model and its links, without modifying anything). name=recipe:\n" +
            "- mounting: top face of the body against the bottom face of what is above (slabs, beams, ramps, stairs) at 1/5/9 points; the lowest corner wins. " +
            "Classes ok, embedded, floating, no_surface; saves $empotradas, $flotando, $no_surface. tol=1/16\" by default.\n" +
            "- clearance: the same at 1 point. clashes: penetrations (rays + exact boolean) → $choques. near: columns/beams around (radius) → $cerca_choque.\n" +
            "- wall: distance and side of the wall → $penetra_muro, $separadas_muro. actual_level: physical vs. assigned level → $nivel_distinto. duplicates → $duplicados.\n" +
            "- room: room (link) and space of each element → $no_room. view_range: inside/above/below the range of each plan → $rango_encima, $rango_debajo. index: cache status.\n" +
            "Keys: cat, lvl, ids/$set, ws, type, against (slabs, beams, columns, ramps, stairs, walls, ceilings, roofs or categories), links (contains|all|none), " +
            "host=false, side=up|down, points, tol, max, radius, sub_exclude (body subcategories to ignore, e.g. JBox), only=class|all, page.\n" +
            "Chaining: geo name=mounting → plan [{op:snap_to_surface, ids:$flotando}] → commit. If it takes a while it returns job_id: wait with job.";

        private const string CaptureText =
            "CAPTURE (capture): PNG image of a view to look at it. view=id (the active one by default; catalog kind=views for ids). px 400–2400 (1400 by default; 1000 is enough for a glance).\n" +
            "color_by=categories: fixtures orange, switches green, receptacles blue, panels magenta, alarm red, data/communications cyan… (legend in the response).\n" +
            "highlight=\"$untagged\" or \"$a=red;$b=blue;123,456=#00AAFF\" (up to 6 groups). framing=ids|$set|highlighted crops to those elements.\n" +
            "Highlight and crop are applied only for the image and rolled back immediately: the model does not change and nothing appears in Undo. " +
            "Sheets: captured as they are. 'content' counts what is visible by category (host model).";

        private const string Batch =
            "BATCH (batch): several reads in one call. calls=[{\"tool\":\"query\",\"cat\":\"LightingFixtures\",\"count\":true}, {\"tool\":\"catalog\",\"kind\":\"levels\"}, …] (max. 8).\n" +
            "Supported tools: revit_context, query, get, catalog, check, geo, sets. They run concurrently and come back in the same order; one error does not stop the others.";

        public static string TopicList => string.Join(", ", Topics);

        public static int Size(string topic) => Encoding.UTF8.GetByteCount(Get(topic));

        public static string All() => string.Join("\n\n", Topics.Select(t => Get(t)));
    }
}
