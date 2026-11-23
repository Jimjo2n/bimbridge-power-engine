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

            public Dictionary<string, object> Brief() => new Dictionary<string, object> { ["id"] = Id, ["status"] = State, ["kind"] = Function };

            public Dictionary<string, object> Full() => new Dictionary<string, object>
            {
                ["id"] = Id, ["function"] = Function, ["parameters"] = Params.ToList<object>(), ["permission"] = Permission,
                ["compatibility"] = Compatibility, ["status"] = State, ["engine"] = Engine,
                ["restrictions"] = Constraints.ToList<object>(), ["example"] = Example,
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
              new[] { "path" }, new[] { "path inside the job's authorized folders", "no network", "encrypted PDF → NOT_SUPPORTED" }, "{\"job\":\"pdf_info\",\"args\":{\"path\":\"…/E-1.3.09.pdf\"}}", NoRevit, "pdf", "document"),
            E("pdf.extract", CapabilityStates.Available, Python, Permissions.Read, "words with box, lines, rectangles and curves in the visible page area; sheet number, revision and scale",
              new[] { "path", "pages|from,until", "include[text,vectors,metadata]", "max_elements" }, new[] { "scanned → NO_CONTENT (nothing is invented)", "PAGE coordinates, never Revit coordinates" }, "{\"job\":\"pdf_extract\",\"args\":{\"path\":\"…\",\"pages\":[1],\"include\":[\"metadata\"]}}", NoRevit, "pdf"),
            E("pdf.tables", CapabilityStates.Available, Python, Permissions.Read, "ruled tables (fixture schedules, schedules) cell by cell; discards plan grids",
              new[] { "path", "pages", "include_grids", "max_row_height_pt" }, new[] { "only tables drawn with lines", "unruled tables → not detected (pending)" }, "{\"job\":\"pdf_tables\",\"args\":{\"path\":\"…\"}}", NoRevit, "pdf", "schedule", "schedule"),
            E("pdf.symbols", CapabilityStates.Available, Python, Permissions.Read, "candidate symbols by geometric signature + legend + label, with confidence and ambiguities",
              new[] { "path", "pages", "max_symbol_pt", "label_radius_pt" }, new[] { "candidates, not confirmed fixtures", "ambiguous → authorized agent or person", "not vision: scanned pages are not interpreted" }, "{\"job\":\"pdf_symbols\",\"args\":{\"path\":\"…\"}}", NoRevit, "pdf", "symbol", "legend", "fixture"),
            E("pdf.grids", CapabilityStates.Available, Python, Permissions.Read, "plan grids (long line + labeled bubble) and named intersections \"A/1\" in page points",
              new[] { "path", "pages", "no_bubble" }, new[] { "anchors for plan–model registration, not Revit coordinates", "no_bubble=true allows inferring intersections from end labels on pages without recognized bubbles" }, "{\"job\":\"pdf_grids\",\"args\":{\"path\":\"…\",\"pages\":[1]}}", NoRevit, "pdf", "grids", "grid", "record", "anchors"),
            E("docx.extract", CapabilityStates.Available, Python + " / .NET", Permissions.Read, "styled paragraphs and tables of a DOCX; flags phrases that look like commands (they are data)",
              new[] { "path", "max_elements" }, new[] { "DTD/entities rejected (XXE)", "the content is never obeyed" }, "{\"job\":\"docx_extract\",\"args\":{\"path\":\"…/spec.docx\"}}", NoRevit, "docx", "specification"),
            E("pdf.scanned_interpretation", CapabilityStates.RequiresAuthorization, "external (vision)", Permissions.Read, "interpret a scanned PDF (no vectors) through vision",
              new[] { "path" }, new[] { "requires explicit authorization: it would send images to a vision service", "not implemented inside the worker" }, null, NoRevit, "scanned", "vision"),

            // ---- geometry (Rust with C# fallback) ----
            E("geo.transform3", CapabilityStates.Available, Rust, Permissions.Read, "3×4 affine transform of N points", new[] { "xyz[]", "m[12]" }, new[] { "finite values" }, null, NoRevit, "geometry", "transformation"),
            E("geo.nearest", CapabilityStates.Available, Rust, Permissions.Read, "2D/3D nearest neighbor with maximum radius (uniform grid)", new[] { "query[]", "reference[]", "dim", "max_dist" }, new[] { "ties → lowest index (deterministic)" }, null, NoRevit, "geometry", "proximity"),
            E("geo.match", CapabilityStates.Available, Rust, Permissions.Read, "1-to-1 expected↔observed matching by distance (deterministic global greedy)", new[] { "expected_items[]", "observed_items[]", "dim", "tol" }, new string[0], null, NoRevit, "geometry", "correspondence"),
            E("geo.duplicates", CapabilityStates.Available, Rust, Permissions.Read, "duplicate groups by tolerance (union-find)", new[] { "points[]", "dim", "tol" }, new string[0], null, NoRevit, "geometry", "duplicates"),
            E("geo.segment_contacts", CapabilityStates.Available, Rust, Permissions.Read, "contacts between 2D segments: cross, touch, collinear overlap", new[] { "segments_list[]", "eps" }, new string[0], null, NoRevit, "geometry", "intersection"),
            E("geo.components", CapabilityStates.Available, Rust, Permissions.Read, "connected components and degree of a graph", new[] { "nodes", "edges[]" }, new string[0], null, NoRevit, "graph"),

            // ---- PDF → model workflow ----
            E("wf.registration", CapabilityStates.Available, Csharp, Permissions.Read, "plan–model registration: least-squares similarity or affine with anchors, residuals, invalid anchors, units and composition with link transforms",
              new[] { "anchors[{page_pt, model_ft}]", "mode similarity|affine", "tol_ft" }, new[] { "≥2 anchors (similarity) / ≥3 (affine)", "never uses pixels or page points as Revit coordinates without transforming them" }, null, NoRevit, "record", "anchors", "scale"),
            E("wf.placement_plan", CapabilityStates.Available, Csharp, Permissions.Read, "deterministic placement plan (id, type, family, level, xyz, orientation, height, host, PDF source, confidence) and 'place' batches ≤500",
              new[] { "symbols", "registration", "type_map", "level", "height" }, new[] { "ambiguities are escalated; nothing is placed with confidence < threshold" }, null, NoRevit, "placement", "plan"),
            E("wf.placement_execute", CapabilityStates.NativeTestPending, RevitApi, Permissions.ModifyProject, "run the plan with the existing typed operations (place, tag) through queue/journal",
              new[] { "plan" }, new[] { "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION", "lab copies only until authorized" }, null, Revit, "placement", "execution"),
            E("wf.verify_placement", CapabilityStates.Available, Csharp, Permissions.Read, "independent verification: missing, duplicates, type, displaced, rotation, height, host, visibility → compact report",
              new[] { "expected (independent source with sha256)", "observed" }, new[] { "the source cannot be the execution plan itself" }, null, NoRevit, "verification", "qc"),

            // ---- advanced engines ----
            E("mep.network", CapabilityStates.Available, Csharp + " (+ Rust for proximity)", Permissions.Read, "conduit network: proximity, topological continuity and physical connection kept separate; open ends",
              new[] { "segments[{id,a,b,diameter,connectors}]", "tol" }, new[] { "proximity ≠ connection" }, null, NoRevit, "mep", "conduit", "network"),
            E("mep.riser", CapabilityStates.Available, Csharp, Permissions.Read, "riser graph: levels, vertical connections, crossings, labels and open ends",
              new[] { "nodes", "edges", "levels" }, new[] { "a 2D diagram connection is not a physical conduit" }, null, NoRevit, "riser", "riser", "diagram"),
            E("groups.variants", CapabilityStates.Available, Csharp, Permissions.Read, "detection of repeated groups and variants by relative signature (translation/rotation)",
              new[] { "elements[{id,type,x,y,rot,group}]", "tol" }, new string[0], null, NoRevit, "groups", "variants"),
            E("annot.layout", CapabilityStates.Experimental, Csharp, Permissions.Read, "tag positions without collisions, aligned and readable at the scale",
              new[] { "anchors", "obstacles", "scale", "tag_size" }, new[] { "proposal; native tag placement remains NATIVE_TEST_PENDING" }, null, NoRevit, "annotation", "tags_list", "collisions"),

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
            if (op == Wire.OpExec && string.Equals(Arg.GetString(args, "action"), "lab", StringComparison.OrdinalIgnoreCase)) return CapabilityStates.Experimental;
            return Describe("wire." + op)?.State ?? CapabilityStates.Available;
        }

        public static Dictionary<string, object> Summary() => new Dictionary<string, object>
        {
            ["total"] = All.Count,
            ["by_status"] = All.GroupBy(e => e.State).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count()),
        };
    }
}
