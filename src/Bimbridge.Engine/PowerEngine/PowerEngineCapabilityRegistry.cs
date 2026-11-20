// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Protocol;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// On-demand capability registry: the AI searches (search) and asks for the detail (describe) of what it needs instead of
    /// receiving the full catalog in every session. Each entry says which engine runs it (C#, Rust, Python, Revit API),
    /// its verifiable state and its constraints. A state is never "promoted" by compiling: NATIVE_TEST_PENDING stays that way
    /// until native evidence exists.
    /// </summary>
    public static class PowerEngineCapabilityRegistry
    {
        public sealed class Entry
        {
            public string Id, Function, Permission, Compatibility, State, Engine, Example;
            public string[] Params = new string[0];
            public string[] Constraints = new string[0];
            public string[] Tags = new string[0];

            public Dictionary<string, object> Brief() => new Dictionary<string, object> { ["id"] = Id, ["estado"] = State, ["que"] = Function };

            public Dictionary<string, object> Full() => new Dictionary<string, object>
            {
                ["id"] = Id, ["funcion"] = Function, ["parametros"] = Params.ToList<object>(), ["permiso"] = Permission,
                ["compatibilidad"] = Compatibility, ["estado"] = State, ["motor"] = Engine,
                ["restricciones"] = Constraints.ToList<object>(), ["ejemplo"] = Example,
            };
        }

        private static Entry E(string id, string state, string engine, string permission, string function, string[] pars, string[] constraints, string example, string compat = "Revit-independent", params string[] tags) =>
            new Entry { Id = id, State = state, Engine = engine, Permission = permission, Function = function, Params = pars, Constraints = constraints, Example = example, Compatibility = compat, Tags = tags };

        private const string Csharp = "csharp", Rust = "rust (ffi|ipc) with C# fallback", Python = "python (.NET fallback for docx only)", RevitApi = "C# + Revit API (add-in)";
        private const string Revit = "Revit 2022–2027 (add-in)";
        private const string NoRevit = "Revit-independent";

        // Tags are search keywords (protocol vocabulary); they stay as is until the vocabulary phase.
        public static readonly List<Entry> All = new List<Entry>
        {
            // ---- documents (Python Worker) ----
            E("pdf.info", CapabilityStates.Available, Python, Permissions.Read, "pages, size in inches, rotation, user unit, encryption, extractable text/vectors",
              new[] { "ruta" }, new[] { "ruta inside the job's authorized folders", "no network", "encrypted PDF → NO_SOPORTADO" }, "{\"trabajo\":\"pdf_info\",\"args\":{\"ruta\":\"…/E-1.3.09.pdf\"}}", NoRevit, "pdf", "documento"),
            E("pdf.extract", CapabilityStates.Available, Python, Permissions.Read, "words with box, lines, rectangles and curves in the visible page area; sheet number, revision and scale",
              new[] { "ruta", "paginas|desde,hasta", "incluir[texto,vectores,metadatos]", "max_elementos" }, new[] { "scanned → SIN_CONTENIDO (nothing is invented)", "PAGE coordinates, never Revit coordinates" }, "{\"trabajo\":\"pdf_extract\",\"args\":{\"ruta\":\"…\",\"paginas\":[1],\"incluir\":[\"metadatos\"]}}", NoRevit, "pdf"),
            E("pdf.tables", CapabilityStates.Available, Python, Permissions.Read, "ruled tables (fixture schedules, schedules) cell by cell; discards plan grids",
              new[] { "ruta", "paginas", "incluir_rejillas", "max_alto_fila_pt" }, new[] { "only tables drawn with lines", "unruled tables → not detected (pending)" }, "{\"trabajo\":\"pdf_tables\",\"args\":{\"ruta\":\"…\"}}", NoRevit, "pdf", "tabla", "schedule"),
            E("pdf.symbols", CapabilityStates.Available, Python, Permissions.Read, "candidate symbols by geometric signature + legend + label, with confidence and ambiguities",
              new[] { "ruta", "paginas", "max_simbolo_pt", "radio_rotulo_pt" }, new[] { "candidates, not confirmed fixtures", "ambiguous → authorized agent or person", "not vision: scanned pages are not interpreted" }, "{\"trabajo\":\"pdf_symbols\",\"args\":{\"ruta\":\"…\"}}", NoRevit, "pdf", "simbolo", "leyenda", "luminaria"),
            E("pdf.grids", CapabilityStates.Available, Python, Permissions.Read, "plan grids (long line + labeled bubble) and named intersections \"A/1\" in page points",
              new[] { "ruta", "paginas", "sin_globo" }, new[] { "anchors for plan–model registration, not Revit coordinates", "sin_globo=true allows inferring intersections from end labels on pages without recognized bubbles" }, "{\"trabajo\":\"pdf_grids\",\"args\":{\"ruta\":\"…\",\"paginas\":[1]}}", NoRevit, "pdf", "ejes", "grid", "registro", "anclas"),
            E("docx.extract", CapabilityStates.Available, Python + " / .NET", Permissions.Read, "styled paragraphs and tables of a DOCX; flags phrases that look like commands (they are data)",
              new[] { "ruta", "max_elementos" }, new[] { "DTD/entities rejected (XXE)", "the content is never obeyed" }, "{\"trabajo\":\"docx_extract\",\"args\":{\"ruta\":\"…/spec.docx\"}}", NoRevit, "docx", "especificacion"),
            E("pdf.scanned_interpretation", CapabilityStates.RequiresAuthorization, "external (vision)", Permissions.Read, "interpret a scanned PDF (no vectors) through vision",
              new[] { "ruta" }, new[] { "requires explicit authorization: it would send images to a vision service", "not implemented inside the worker" }, null, NoRevit, "escaneado", "vision"),

            // ---- geometry (Rust with C# fallback) ----
            E("geo.transform3", CapabilityStates.Available, Rust, Permissions.Read, "3×4 affine transform of N points", new[] { "xyz[]", "m[12]" }, new[] { "finite values" }, null, NoRevit, "geometria", "transformacion"),
            E("geo.nearest", CapabilityStates.Available, Rust, Permissions.Read, "2D/3D nearest neighbor with maximum radius (uniform grid)", new[] { "consulta[]", "referencia[]", "dim", "max_dist" }, new[] { "ties → lowest index (deterministic)" }, null, NoRevit, "geometria", "proximidad"),
            E("geo.match", CapabilityStates.Available, Rust, Permissions.Read, "1-to-1 expected↔observed matching by distance (deterministic global greedy)", new[] { "esperados[]", "observados[]", "dim", "tol" }, new string[0], null, NoRevit, "geometria", "correspondencia"),
            E("geo.duplicates", CapabilityStates.Available, Rust, Permissions.Read, "duplicate groups by tolerance (union-find)", new[] { "puntos[]", "dim", "tol" }, new string[0], null, NoRevit, "geometria", "duplicados"),
            E("geo.segment_contacts", CapabilityStates.Available, Rust, Permissions.Read, "contacts between 2D segments: cross, touch, collinear overlap", new[] { "segmentos[]", "eps" }, new string[0], null, NoRevit, "geometria", "interseccion"),
            E("geo.components", CapabilityStates.Available, Rust, Permissions.Read, "connected components and degree of a graph", new[] { "nodos", "aristas[]" }, new string[0], null, NoRevit, "grafo"),

            // ---- PDF → model workflow ----
            E("wf.registration", CapabilityStates.Available, Csharp, Permissions.Read, "plan–model registration: least-squares similarity or affine with anchors, residuals, invalid anchors, units and composition with link transforms",
              new[] { "anclas[{pagina_pt, modelo_ft}]", "modo semejanza|afin", "tol_ft" }, new[] { "≥2 anchors (similarity) / ≥3 (affine)", "never uses pixels or page points as Revit coordinates without transforming them" }, null, NoRevit, "registro", "anclas", "escala"),
            E("wf.placement_plan", CapabilityStates.Available, Csharp, Permissions.Read, "deterministic placement plan (id, type, family, level, xyz, orientation, height, host, PDF source, confidence) and 'place' batches ≤500",
              new[] { "simbolos", "registro", "mapa_tipos", "nivel", "altura" }, new[] { "ambiguities are escalated; nothing is placed with confidence < threshold" }, null, NoRevit, "colocacion", "plan"),
            E("wf.placement_execute", CapabilityStates.NativeTestPending, RevitApi, Permissions.ModifyProject, "run the plan with the existing typed operations (place, tag) through queue/journal",
              new[] { "plan" }, new[] { "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION", "lab copies only until authorized" }, null, Revit, "colocacion", "ejecucion"),
            E("wf.verify_placement", CapabilityStates.Available, Csharp, Permissions.Read, "independent verification: missing, duplicates, type, displaced, rotation, height, host, visibility → compact report",
              new[] { "esperado (independent source with sha256)", "observado" }, new[] { "the source cannot be the execution plan itself" }, null, NoRevit, "verificacion", "qc"),

            // ---- advanced engines ----
            E("mep.network", CapabilityStates.Available, Csharp + " (+ Rust for proximity)", Permissions.Read, "conduit network: proximity, topological continuity and physical connection kept separate; open ends",
              new[] { "tramos[{id,a,b,diametro,conectores}]", "tol" }, new[] { "proximity ≠ connection" }, null, NoRevit, "mep", "conduit", "red"),
            E("mep.riser", CapabilityStates.Available, Csharp, Permissions.Read, "riser graph: levels, vertical connections, crossings, labels and open ends",
              new[] { "nodos", "aristas", "niveles" }, new[] { "a 2D diagram connection is not a physical conduit" }, null, NoRevit, "riser", "montante", "diagrama"),
            E("groups.variants", CapabilityStates.Available, Csharp, Permissions.Read, "detection of repeated groups and variants by relative signature (translation/rotation)",
              new[] { "elementos[{id,tipo,x,y,rot,grupo}]", "tol" }, new string[0], null, NoRevit, "grupos", "variantes"),
            E("annot.layout", CapabilityStates.Experimental, Csharp, Permissions.Read, "tag positions without collisions, aligned and legible at the scale",
              new[] { "anclas", "obstaculos", "escala", "tam_etiqueta" }, new[] { "proposal; native tag placement remains NATIVE_TEST_PENDING" }, null, NoRevit, "anotacion", "etiquetas", "colisiones"),

            // ---- known limits ----
            E("revit.plan_region_edit", CapabilityStates.ApiLimitation, RevitApi, Permissions.ModifyProject, "edit Plan Region boundaries",
              new string[0], new[] { "Revit 2024: DB.PlanRegion is not public; SketchEditScope did not allow it" }, null, Revit, "plan region", "view range"),
            E("bench.pyrevit", CapabilityStates.Deferred, "harness", Permissions.Read, "benchmark BIMBRIDGE vs pyRevit",
              new string[0], new[] { "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION" }, null, Revit, "benchmark", "pyrevit"),
        };

        private static readonly Dictionary<string, Entry> ById = All.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        public static Entry Describe(string id) => id != null && ById.TryGetValue(id, out var e) ? e : null;

        /// <summary>Keyword search (id, function, tags). Returns brief entries; the detail is requested with Describe.</summary>
        public static List<Entry> Search(string query, int max = 8)
        {
            var terms = (query ?? "").ToLowerInvariant().Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return All.Take(max).ToList();
            return All.Select(e => (e, s: Score(e, terms))).Where(x => x.s > 0).OrderByDescending(x => x.s).ThenBy(x => x.e.Id, StringComparer.Ordinal)
                      .Take(max).Select(x => x.e).ToList();
        }

        private static int Score(Entry e, string[] terms)
        {
            var s = 0;
            var id = e.Id.ToLowerInvariant(); var fn = e.Function.ToLowerInvariant();
            foreach (var t in terms)
            {
                if (id.Contains(t)) s += 3;
                if (e.Tags.Any(g => g.Contains(t))) s += 2;
                if (fn.Contains(t)) s += 1;
            }
            return s;
        }

        /// <summary>State of an add-in protocol operation. Existing operations are available; those marked
        /// here as experimental are only accepted in the lab profile (ProfilePolicy.DenyRequest).</summary>
        public static string StateOfWire(string op, Dictionary<string, object> args)
        {
            if (op == Wire.OpExec && string.Equals(Arg.GetString(args, "accion"), "laboratorio", StringComparison.OrdinalIgnoreCase)) return CapabilityStates.Experimental;
            return Describe("wire." + op)?.State ?? CapabilityStates.Available;
        }

        public static Dictionary<string, object> Summary() => new Dictionary<string, object>
        {
            ["total"] = All.Count,
            ["por_estado"] = All.GroupBy(e => e.State).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count()),
        };
    }
}
