// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using System.Text;

namespace Bimbridge.Engine
{
    /// <summary>
    /// On-demand guides (guide tool). Tool descriptions are short because they are loaded in every session;
    /// the detail lives here and the AI only requests it when needed (and only the topic or operation it needs).
    /// </summary>
    public static class Guides
    {
        public static readonly string[] Topics = { "flujo", "consulta", "catalogo", "geo", "escritura", "exec", "familias", "documento", "recuperacion", "tareas", "recetas", "captura", "lote" };

        public static string Get(string tema, string op = null)
        {
            switch ((tema ?? "flujo").Trim().ToLowerInvariant())
            {
                case "flujo": return Flow;
                case "consulta": case "query": return Query;
                case "catalogo": case "catálogo": return Catalog;
                case "geo": case "geometria": case "geometría": return Geo;
                case "escritura": case "plan": return OpCatalog.Guide(op);
                case "captura": case "capture": return CaptureText;
                case "lote": return Batch;
                case "exec": case "codigo": case "código": return Exec;
                case "familias": case "familia": case "fam": case "conector": return Families;
                case "documento": case "doc": case "pdf": case "sync": case "guardar": return DocOps;
                case "recuperacion": case "recuperación": case "clave": return Recovery;
                case "tarea": case "tareas": return Tasks;
                case "receta": case "recetas": return Recipes;
                default: return "Topics: " + string.Join(", ", Topics) + ". escritura accepts op=tag,place… to show only those operations.";
            }
        }

        private const string Flow =
            "RECOMMENDED WORKFLOW (fewer calls, less text):\n" +
            "1. revit_context: version, document, rev. If several Revit instances are open, they appear in 'instancias' (switch with instancia=<pid>).\n" +
            "2. catalog que=resumen: levels, views, sheets, links and categories with counts, in one call.\n" +
            "3. query with count=true or group_by=lvl,type to summarize; request rows only if you will use them and only the fields you need (f=id,type).\n" +
            "4. Save results with save_as=$name and reuse them (ids=$name) in query, get, geo, plan, show and capture.\n" +
            "5. Several independent reads → lote (a single call).\n" +
            "6. To SEE the view: capture (image) with colorear=categorias to tell fixtures from receptacles and switches; resaltar=$set.\n" +
            "7. Real physics (heights, embedded, floating, clashes): geo. Repeating without model changes is instant (cache).\n" +
            "8. Changes: plan → commit (typed, diff, a single Undo) for what its operations cover; exec (C#) for everything else; act/fam and doc for families, " +
            "connectors, save, sync and PDF. If the task is authorized, apply without asking for confirmation on each batch; otherwise, show the diff first. " +
            "estado=retenido = waiting in the Revit queue; do not repeat. User pause = stop.\n" +
            "9. show ids=… to point the user at the elements on screen (selects and zooms).\n" +
            "Statuses: read 'aviso' and follow its instruction. job waits for long jobs (job_id).";

        private const string Exec =
            "EXEC (general C# over the public Revit API; compiled with Roslyn 4.8 inside Revit):\n" +
            "Forms: expression (doc.Title) | body with return | class with public static object Run(ScriptContext ctx). using directives at the top are hoisted automatically.\n" +
            "Already available: doc, uidoc, app, uiapp, args (input JSON object), ctx. ctx.Mm(6.5) mm→feet, ctx.ToMm(feet), ctx.E(id), ctx.Set(\"$x\"), " +
            "ctx.SaveSet(\"$y\", ids), ctx.Log(x), ctx.DocOf(\"d3\"), ctx.ThrowIfCancelled().\n" +
            "modo=consulta (default): runs inside a transaction that is ALWAYS rolled back; use it to measure or test without touching the model.\n" +
            "modo=ejecutar tx=auto: one transaction, one Undo | tx=grupo: several script transactions in a group | tx=ninguna: the script opens its own " +
            "(required for EditFamily, LoadFamily, Save, SaveAs). Requires permission W (or R for a query).\n" +
            "Returns: value as JSON (ElementId → number, XYZ → [x,y,z] in feet, Element → id/cat/name), log, Revit warnings grouped by type, " +
            "elements created/changed/deleted per document ($exec_cambiados). Errors include the script line. Every script is audited (hash + file).\n" +
            "guardar=name saves it; nombre=X repeats it; nombre=lista. A script runs on the Revit thread: Revit does not respond while it runs.";

        private const string Families =
            "FAMILIES (act / fam). Typical washbasin workflow:\n" +
            "1. revit \"fam info familia=X\" (types, parameters, formulas, connectors) and \"fam caras familia=X\" (planar faces element:n with normal and center).\n" +
            "2. act \"abrir familia=X\" → dN handle of the RFA (reuses the open one).\n" +
            "3. act \"conector nombre=fria sistema=fria cara=812:3 radio=6.5mm doc=dN\" (radius or diameter ALWAYS with a unit; none is assumed). " +
            "Searches by name (connector description), adopts an unnamed one on that face or creates it; never duplicates. flujo=entrada|salida|bidireccional only if it was decided.\n" +
            "4. act \"param nombre=X valor=6.5mm\" | formula= | crear=tipo|instancia dato=longitud | asociar=elem:CONNECTOR_RADIUS. If a family parameter drives " +
            "the radius, it is reported as pending: the association is not broken.\n" +
            "5. act \"guardar doc=dN\" and \"cargar en=d1 doc=dN sobrescribir=valores\": reloads into the project and re-reads the connectors from the instances.\n" +
            "Repeating 3–5 with the same clave= does not apply twice.";

        private const string DocOps =
            "DOCUMENT (act or doc_op): guardar | guardar_como como=path [sobrescribir=si] | sync [comentario=] [liberar=si] | " +
            "pdf conjunto=\"NON-UNIT LIGHTING\" or hojas=$x|10E.01.NUL,10E.02.NUL [carpeta=] [nombre=] | " +
            "ifc [version=ifc2x3|ifc4|ifc4dtv] [vista=] [cantidades=si] [carpeta=] [nombre=] | tabla_csv tabla=<name|id> [carpeta=] [nombre=] | cerrar [guardar=si|descartar=si].\n" +
            "Each one runs as a job (job_id if it takes a while) and is VERIFIED: file exists, new timestamp, size, SHA-256, PDF pages; sync confirms by event. " +
            "Does not close the active document (Revit does not allow it through the API). Permission D or W.";

        private const string Recovery =
            "RECOVERY: clave=<text> on commit, exec ejecutar, act and doc_op records it in an on-disk journal (operaciones.jsonl).\n" +
            "Same key already done → returns the previous result without repeating. Interrupted (Revit closed midway) → estado=interrumpido: review the model " +
            "and repeat with reconciliar=si. Same key with different content → invalido.\n" +
            "tarea=<name> on plan/commit/exec: that task's jobs are not cancelled when the conversation drops, and another conversation with the same task " +
            "can query them (job) and apply its plans. Documents: doc=dN pins the target; if that document was closed → destino_no_disponible (it never switches documents on its own).";

        private const string Tasks =
            "LONG TASKS (0.6.0): act \"tarea iniciar receta=<name>|archivo=<path.cs> id=<id> objetivo=… args.field=value [guardar_cada=N] [al_final=guardar,pdf] [en_error=continuar]\".\n" +
            "ONE command: the engine enumerates the recipe's units, runs them in measured batches (one transaction and one Undo per unit), yields Revit between visits, " +
            "writes a durable checkpoint after each batch, saves every N and runs the final barriers only once everything before them is committed. Replies after ~10 s (espera_s=) with the status.\n" +
            "Follow up: revit \"tarea esperar <id> desde=<rev> max=60\" (waits for changes without polling) | tarea estado|resumen|unidades <id> [estado=conflicto] | tarea lista.\n" +
            "Another client (Codex↔Claude) picks it up with the id: tarea resumen <id> gives where it stopped, what is left and the decisions; no code needs to be resent.\n" +
            "Statuses: en_cola, ejecutando, esperando_contexto (pause/Hold/dialog/closed document: resumes on its own), aplicada, aplicada_con_diferencias, parcial, interrumpida, cancelada.\n" +
            "interrumpida/parcial: act \"tarea reanudar <id>\" verifies what is uncertain (UniqueId of what was created or the recipe's Verificar; after a reopen, anything after the last save of that opening) " +
            "and continues without duplicating. What cannot be proven stays desconocida: act \"tarea reconciliar <id> decision=aplicada|no_aplicada claves=…\". " +
            "Cancel: act \"tarea cancelar <id>\" (between units; what is committed stays). While it runs, the document is taken: other writes → documento_ocupado (exec/commit with tarea=<id> do go through).";

        private const string Recipes =
            "RECIPES: C# class that inherits from Receta (by file in %LOCALAPPDATA%\\Bimbridge\\recetas\\<name>.cs, in the add-in's recetas\\, or as code). tarea recetas lists them.\n" +
            "public class X : Receta { public override string Version => \"1\"; public override IEnumerable<string> Entradas => new[]{\"nivel\"};\n" +
            "  public override IEnumerable<Unidad> Unidades(ScriptContext ctx) { foreach (var e in …) yield return new Unidad(\"luz:\" + e.UniqueId, datos, new[]{ ctx.Long(e.Id) }); yield return Unidad.Guardar(); }\n" +
            "  public override object Ejecutar(ScriptContext ctx, Unidad u) { … return resumen; }   // inside its transaction; ctx.Omitir(\"reason\") skips it\n" +
            "  public override Verificacion Verificar(ScriptContext ctx, Unidad u) => …;   // after a crash: was it applied?\n" +
            "  public override object Postcondicion(ScriptContext ctx) => new { desviaciones = … }; }   // final read-only QC (rolled back)\n" +
            "STABLE keys (UniqueId, not ElementId). Unit ids = what it reads/changes: if someone else changes them first, the unit becomes a conflict (nothing is overwritten). " +
            "ctx.Decision(text), ctx.Excepcion(reason, detail), ctx.Artefacto(name, content) are kept in the task memory. Transaccion => PoliticaTx.Ninguna if the recipe opens its own (EditFamily/LoadFamily). " +
            "The task keeps an exact copy of the recipe (with its SHA-256): changing the file does not alter tasks already started.\n" +
            "BUILT-IN: etiquetar_luces (vista, tipo_etiqueta, guardar, pdf_hojas), parametro_por_lote (ids|conjunto, parametro, valor), qc_manifiesto (JSON manifest or path; CSV of deviations), " +
            "montaje_soportes (al_ras/empotrado/separado/sin_referencia, includes links), exportar_laminas (conjunto|hojas, antes=guardar|sync).\n" +
            "Bim.* HELPERS (in scripts and recipes, mm): Grupos.Inspeccionar/Firma/Componer/CambiarTipo/VerificarComposicion; Montaje.Cuerpo/Soporte/Vista3D/Transformacion; " +
            "Vistas.Rango/AplicarRango/CompararPlantilla/PorQueNoSeVe/FiltroPorCategoria; Manifiesto.Juntar/Qc. Those that write run inside the engine transaction.";

        private const string Query =
            "QUERY (query): cat (LightingFixtures, ElectricalFixtures, LightingDevices…), lvl, type (contains), ws, ids/$set, view (visible in that view).\n" +
            "where: field=value, !=, >, <, >=, <=, ~ (contains), field? (not empty), !field (empty). E.g.: where=[\"elev>9\",\"type~LT-06\"].\n" +
            "f: id,cat,fam,type,lvl,ws,elev,mark,comments,host,x,y,z,name,uid,view or any parameter by name.\n" +
            "group_by (up to 3 fields) counts by group; count=true only the total; page/page_size (max. 200); since=rev only what changed afterwards.\n" +
            "Several pages: the response carries cursor=q…; request the next ones with cursor=q… page=N (without re-reading the model). " +
            "If the document changed, the cursor is rejected (conflict) and the query must be repeated; it expires after 5 min unused.\n" +
            "units=mm converts elev,x,y,z (also in where). full=true does not truncate texts.\n" +
            "get ids=… f=*: ALL parameters (instance and type) of up to 10 elements; what they share is printed once in 'comunes'. buscar=text filters names.";

        private const string Catalog =
            "CATALOG (catalog que=…): resumen | categorias | tipos | familias | niveles | vistas | plantillas | laminas | worksets | vinculos | filtros.\n" +
            "cat filters types and families; lvl and tipo (plano, rcp, seccion, alzado, 3d, leyenda, tabla…) filter views; buscar=text in any column.\n" +
            "f adds parameters by name to views and sheets (e.g. f=Sub-Discipline,Set Group or f=Sheet Set,Drawn By).\n" +
            "niveles: elev = displayed elevation; z = internal elevation (the one used by x,y,z). vistas: hoja = number of the sheet it is placed on.";

        private const string Geo =
            "GEO (real meshes of the model and its links, without modifying anything). name=recipe:\n" +
            "- montaje: top face of the body against the bottom face of what is above (slabs, beams, ramps, stairs) at 1/5/9 points; the lowest corner wins. " +
            "Classes ok, empotrada, flotando, sin_superficie; saves $empotradas, $flotando, $sin_superficie. tol=1/16\" by default.\n" +
            "- holgura: the same at 1 point. choques: penetrations (rays + exact boolean) → $choques. cerca: columns/beams around (radius) → $cerca_choque.\n" +
            "- muro: distance and side of the wall → $penetra_muro, $separadas_muro. nivel_real: physical vs. assigned level → $nivel_distinto. duplicados → $duplicados.\n" +
            "- cuarto: room (link) and space of each element → $sin_cuarto. rango_vista: inside/above/below the range of each plan → $rango_encima, $rango_debajo. indice: cache status.\n" +
            "Keys: cat, lvl, ids/$set, ws, type, contra (slabs, beams, columns, ramps, stairs, walls, ceilings, roofs or categories), vinculos (contiene|todos|ninguno), " +
            "anfitrion=false, lado=arriba|abajo, puntos, tol, max, radio, sub_excluir (body subcategories to ignore, e.g. JBox), solo=clase|todos, page.\n" +
            "Chaining: geo name=montaje → plan [{op:snap_to_surface, ids:$flotando}] → commit. If it takes a while it returns job_id: wait with job.";

        private const string CaptureText =
            "CAPTURE (capture): PNG image of a view to look at it. vista=id (the active one by default; catalog que=vistas for ids). px 400–2400 (1400 by default; 1000 is enough for a glance).\n" +
            "colorear=categorias: fixtures orange, switches green, receptacles blue, panels magenta, alarm red, data/communications cyan… (legend in the response).\n" +
            "resaltar=\"$sin_tag\" or \"$a=rojo;$b=azul;123,456=#00AAFF\" (up to 6 groups). encuadre=ids|$set|resaltado crops to those elements.\n" +
            "Highlight and crop are applied only for the image and rolled back immediately: the model does not change and nothing appears in Undo. " +
            "Sheets: captured as they are. 'contenido' counts what is visible by category (host model).";

        private const string Batch =
            "BATCH (lote): several reads in one call. llamadas=[{\"tool\":\"query\",\"cat\":\"LightingFixtures\",\"count\":true}, {\"tool\":\"catalog\",\"que\":\"niveles\"}, …] (max. 8).\n" +
            "Supported tools: revit_context, query, get, catalog, check, geo, sets. They run concurrently and come back in the same order; one error does not stop the others.";

        public static string TopicList => string.Join(", ", Topics);

        public static int Size(string tema) => Encoding.UTF8.GetByteCount(Get(tema));

        public static string All() => string.Join("\n\n", Topics.Select(t => Get(t)));
    }
}
