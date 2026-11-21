// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bimbridge.Engine.Workflow
{
    /// <summary>
    /// 0.8.0: the SINGLE capability registry and traceability matrix Workflow → CAP → Operation → Component → State → Test → Result → Benchmark.
    /// docs/TRACEABILITY_MATRIX_0.8.0.md is generated from here (a test checks that it matches). The State is that of THIS 0.8.0 build:
    /// native evidence from earlier builds is kept separate (column "prior native") and never counts as proof for this build.
    /// </summary>
    public static class Traceability
    {
        public const string Implemented = "IMPLEMENTED", Unit = "TESTED_UNIT", Simulated = "TESTED_SIMULATED", Native = "TESTED_NATIVE",
            NativePending = "NATIVE_ACCEPTANCE_PENDING", Blocked = "BLOCKED", NotSupported = "NOT_SUPPORTED", Deferred = "DEFERRED";

        public static readonly string[] States = { Implemented, Unit, Simulated, Native, NativePending, Blocked, NotSupported, Deferred };

        public sealed class Row
        {
            public string Workflows, Cap, Operation, Family, Component, State, Test, Result, Benchmark, PriorNative;
        }

        public static readonly IReadOnlyDictionary<string, string> Workflows = new Dictionary<string, string>
        {
            ["WF-01"] = "Configure template and view classification", ["WF-02"] = "Place lighting from drawings",
            ["WF-03"] = "Light mounting and visibility in RCP", ["WF-04"] = "Issue lighting set and QC sheet",
            ["WF-05"] = "Unit, panel and group prototypes", ["WF-06"] = "Model interior markups and clean up RCP DIM",
            ["WF-07"] = "Model and audit two physical feed families", ["WF-08"] = "Adjust crossings, colors and coordination sheet",
            ["WF-09"] = "Prepare a partial print set", ["WF-10"] = "Build a 2D single-line riser",
            ["WF-11"] = "Audit and prepare the riser for issue", ["WF-12"] = "Classroom exercises and family editing",
            ["WF-13"] = "Repair connectors and resume work", ["WF-14"] = "Transfer knowledge and guide the user",
        };

        public static readonly IReadOnlyDictionary<string, string> Caps = new Dictionary<string, string>
        {
            ["CAP-001"] = "Identity, preflight and capabilities", ["CAP-002"] = "Typed, compact queries",
            ["CAP-003"] = "Transactional plan and mutual exclusion", ["CAP-004"] = "Checkpoint, resume and sync",
            ["CAP-005"] = "Reusable operations on elements and views", ["CAP-006"] = "Sheet, set, preset and verifiable issue",
            ["CAP-007"] = "Native and visual verification", ["CAP-008"] = "Sources, versions and independent cross-check",
            ["CAP-009"] = "Geometric registration and mounting", ["CAP-010"] = "Groups, variants and propagation",
            ["CAP-011"] = "Physical networks and coordination", ["CAP-012"] = "2D riser graph and composition",
            ["CAP-013"] = "Annotation legibility and demonstration views", ["CAP-014"] = "Family editor and MEP connectors",
            ["CAP-015"] = "Events, handoff and extensions",
        };

        public static readonly string[] OpFamilies = { "modeling", "editing", "families", "geometry", "MEP", "views", "sheets", "annotations", "documents", "interoperability", "coordination", "publishing", "administration", "core" };

        // Native evidence from EARLIER builds (does not count for 0.8.0).
        private const string Rc1 = "rc1 14ec21d · Revit 2024 · disposable copy (NRT-03 15/15, NRT-05, NRT-06 11/11)";
        private const string Read024 = "0.2.x · Revit 2024 · read";
        private const string Read050 = "0.5.0 · Revit 2026 · read, one sample";
        private const string Bk = "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION";
        private const string SimOk = "OK without Revit; native: " + Bk;

        public static readonly List<Row> All = new List<Row>
        {
            // ---------------- core: identity, plan, resume ----------------
            R("WF-13", "CAP-001", "ctx / identity / instances", "core", "Add-in · AgentHost RevitLink", NativePending, "Rc1_FailClosed_MissingContextFlag (5), AgentHostRevitTests", SimOk, "B7", Rc1),
            R("WF-13", "CAP-001", "per-document capabilities (ctx) and this registry", "core", "Engine Capabilities + Traceability", Unit, "Capabilities_DocMatchesCode, Traceability_DocMatchesCode", "OK", "—", Read050),
            R("WF-02,WF-05", "CAP-002", "consultar / q / get / params (paging, projection)", "core", "Add-in Query · AgentHost ReadRows", NativePending, "Rc1 paging (ForcePage, changing total)", SimOk, "B0", Read024 + "; " + Rc1),
            R("WF-02,WF-09,WF-13", "CAP-003", "plan → commit (diff, one Undo, clave idempotency)", "core", "Add-in PlanBuilder/PlanApplier · AgentHost PlanRunner", NativePending, "PlanTests, PlanQueueTests, AgentHostRevitTests", SimOk + " (0.8.0 changed the commit audit)", "B0, B7", Rc1),
            R("WF-13", "CAP-003", "per-document exclusion: lease + fencing", "core", "AgentHost ExecJournal/Lease", NativePending, "LeaseFencingTests", SimOk, "B7", Rc1),
            R("WF-13", "CAP-003", "durable audit of every persistent commit (intent → result)", "core", "Add-in RequestHandler.Commit · Engine AuditLog", NativePending, "AuditLogTests, V080_Nrt07Harness", SimOk + " (closes rc1 H2)", "B7", "—"),
            R("WF-13", "CAP-004", "resume after a crash; uncertain ⇒ observe, never repeat", "core", "AgentHost PlanRunner/RevitBackend", NativePending, "NRT-03/05/06 simulated, V080_Handoff_AfterCrash", SimOk, "B7", Rc1),
            R("WF-13", "CAP-004", "IPC response lost after commit (NRT-07)", "core", "AgentHost RevitBackend + Faults", NativePending, "V080_Nrt07Harness_LostCommitResponse", SimOk, "B7", "—"),
            R("WF-13,WF-14", "CAP-015", "structured handoff (committed / uncertain / next safe step)", "core", "AgentHost Handoff", Simulated, "V080_Handoff_AfterCrash_ProposesReconcile", "OK", "B7", "—"),
            R("WF-13", "CAP-015", "signed events, scheduling and notifications", "core", "—", Deferred, "—", "0.9.0 (minimal events)", "—", "—"),
            R("WF-13", "CAP-001", "atomic writes of state files (leftover temp file)", "core", "Engine Home.WriteAtomic", Unit, "DurabilityTests", "OK; rc1 native leftover not rechecked", "B7", "—"),

            // ---------------- sources, requirements, verification ----------------
            R("WF-02,WF-10,WF-11", "CAP-008", "requirements with provenance and authority; inference never OBSERVED", "core", "Engine Workflow.Requirements", Unit, "WorkflowTests (9)", "OK", "B2, B5", "—"),
            R("WF-02,WF-11", "CAP-008", "verificar_independiente: independent source → model → differences", "coordination", "Engine IndependentVerifier · AgentHost RevitBackend", NativePending, "V080_IndependentVerification_AgainstModel", SimOk + " (read only)", "B2, B5", "—"),
            R("WF-02", "CAP-008", "source adapters (extract PDF/tables with coordinates)", "coordination", "—", Deferred, "—", "0.9.0", "B2", "—"),
            R("WF-04,WF-09,WF-11", "CAP-006", "delivery states (12 stages) and receipts", "documents", "Engine Workflow.Delivery", Unit, "WorkflowTests", "OK", "B6", "—"),

            // ---------------- editing and modeling ----------------
            R("WF-02", "CAP-005", "set_param / set_values / fijar_parametro_lote", "editing", "Add-in PlanApplier · AgentHost", NativePending, "PlanTests, Rc11_SlowIdleRevit, Bench_B0_586", SimOk, "B0", Rc1),
            R("WF-02", "CAP-005", "change_type / set_type_param / set_level / set_workset / set_elevation", "editing", "Add-in PlanApplier", NativePending, "PlanTests, WriteEngineTests", SimOk, "B2", "—"),
            R("WF-02,WF-05", "CAP-005", "move / rotate / mirror / copy / delete", "editing", "Add-in PlanApplier", NativePending, "WriteEngineTests.BuildMoved, PlanTests", SimOk, "—", "—"),
            R("WF-02", "CAP-005", "place (instance at a point, slab/level mounting)", "modeling", "Add-in PlanApplier", NativePending, "WriteEngineTests.Parse_Validates", SimOk, "B2", "—"),
            R("—", "CAP-005", "walls, floors, ceilings, roofs, stairs (typed creation)", "modeling", "—", NotSupported, "—", "exec only (level B, dynamic_code)", "—", "—"),
            R("WF-05", "CAP-010", "group_create / ungroup / group_place", "editing", "Add-in PlanApplier", NativePending, "PlanTests", SimOk, "B3", "—"),
            R("WF-05", "CAP-010", "group coverage by type/floor plan and propagation", "editing", "—", Deferred, "—", "0.9.0", "B3", "—"),

            // ---------------- geometry and mounting ----------------
            R("WF-03", "CAP-009", "geo mounting / clashes / ranges / rooms (meshes, rays, links)", "geometry", "Add-in GeoRunner · Engine TriMesh", NativePending, "GeoComputeTests, MeshTests", SimOk, "B2", Read024),
            R("WF-03", "CAP-009", "snap_to_surface (real bottom face of slab/ramp)", "geometry", "Add-in PlanApplier", NativePending, "PlanTests", SimOk, "B2", "—"),
            R("WF-02", "CAP-009", "drawing–model registration with anchors", "geometry", "—", Deferred, "—", "0.9.0", "B2", "—"),

            // ---------------- views ----------------
            R("WF-01,WF-03", "CAP-005", "view_create (floor plan, RCP, section, 3D, drafting) / view_set (template, range, detail)", "views", "Add-in PlanApplier · WriteKit", NativePending, "WriteEngineTests, Draw2DTests", SimOk, "B1", "—"),
            R("WF-01,WF-08", "CAP-007", "view_hide / view_override (colors, halftone, transparency)", "views", "Add-in PlanApplier", NativePending, "WriteEngineTests", SimOk, "B1", "—"),
            R("WF-03,WF-11", "CAP-007", "chk lighting_audit / manifest QC per view", "views", "Add-in Check · Engine QC", NativePending, "QcFixtureTests", SimOk, "B2", Read024),
            R("WF-06,WF-11", "CAP-007", "show / capture (point out and capture)", "views", "Add-in Show/Capture", NativePending, "—", "native: " + Bk, "—", Read024),

            // ---------------- sheets and issue ----------------
            R("WF-04,WF-09", "CAP-006", "sheet_create / sheet_place", "sheets", "Add-in PlanApplier", NativePending, "WriteEngineTests", SimOk, "B6", "—"),
            R("WF-04,WF-09", "CAP-006", "sheet_set (persistent sheet set)", "sheets", "Add-in PlanApplier", NativePending, "SheetSetOpTests", SimOk + " (Revit decides the order)", "B6", "—"),
            R("WF-04,WF-09", "CAP-006", "doc pdf (export sheets or set and verify files)", "documents", "Add-in DocOps", NativePending, "—", "native: " + Bk + " (requires export permission)", "B6", "—"),
            R("WF-04", "CAP-006", "verify the exported PDF (pages, sha, revision)", "documents", "Engine Delivery (receipts)", Unit, "WorkflowTests (page ≠ set, sha ≠ export)", "OK; the PDF reader is 0.9.0", "B6", "—"),

            // ---------------- annotation and 2D drawing ----------------
            R("WF-02", "CAP-013", "tag / tag_move", "annotations", "Add-in PlanApplier", NativePending, "PlanTests", SimOk, "B3", "—"),
            R("WF-06", "CAP-013", "text_note / filled_region (validated boundary)", "annotations", "Add-in PlanApplier · Engine Draw2D", NativePending, "Draw2DTests", SimOk, "B5", "—"),
            R("WF-10", "CAP-012", "detail_line / detail_rect / detail_polygon / detail_arc (line styles)", "annotations", "Add-in PlanApplier · Engine Draw2D", NativePending, "Draw2DTests (12)", SimOk, "B5", "—"),
            R("WF-10", "CAP-012", "detail_component (symbols and detail families)", "annotations", "Add-in PlanApplier", NativePending, "WriteEngineTests.Catalog", SimOk, "B5", "—"),
            R("WF-06", "CAP-013", "dimension (linear dimension between detail lines)", "annotations", "Add-in PlanApplier", NativePending, "Draw2DTests", SimOk, "B5", "—"),
            R("WF-10,WF-11", "CAP-012", "riser graph (nodes/edges) and connectivity audit", "annotations", "—", Deferred, "—", "0.9.0", "B5", "—"),
            R("WF-06,WF-08", "CAP-013", "legibility on paper (density, leaders, collisions)", "annotations", "Engine Draw2D (paper↔view, align, distribute)", Deferred, "Draw2DTests (transforms)", "2D base ready; optimization 0.9.0", "—", "—"),

            // ---------------- families ----------------
            R("WF-12", "CAP-014", "fam info / faces", "families", "Add-in FamilyOps", NativePending, "—", "native: " + Bk, "B8", Read050),
            R("WF-12,WF-07", "CAP-014", "fam abrir / conector / param / cargar", "families", "Add-in FamilyOps", NativePending, "ScriptingTests (compiles per year)", "native: " + Bk + " (requires modificar_familia permission)", "B8", "—"),
            R("WF-12", "CAP-014", "fam buscar (index of authorized folders, no disk browsing)", "families", "Engine FamilyLibrary", Unit, "FamilyLibraryTests (3)", "OK", "B8", "—"),
            R("WF-12", "CAP-014", "fam cargar_archivo (indexed, unchanged RFA, sha-256)", "families", "Engine FamilyLibrary · Add-in", NativePending, "FamilyLibraryTests", SimOk + " (requires cargar_archivo permission)", "B8", "—"),

            // ---------------- MEP and coordination ----------------
            R("WF-07,WF-08", "CAP-011", "physical routing, fittings, stubs and dimension reconciliation", "MEP", "—", Deferred, "—", "0.9.0", "B4", "—"),
            R("WF-08", "CAP-011", "clash detection with links", "coordination", "Add-in GeoRunner", NativePending, "GeoComputeTests", SimOk, "B4", Read024),

            // ---------------- documents ----------------
            R("WF-04,WF-13", "CAP-004", "doc guardar / guardar_como (file and hash verification)", "documents", "Add-in DocOps", NativePending, "NRT-05 simulated", SimOk + " (requires guardar permission)", "B6", Rc1),
            R("WF-04,WF-13", "CAP-004", "doc sync (workshared only, sincronizar permission)", "documents", "Add-in DocOps", NativePending, "WorksharedPolicyTests", "native: " + Bk + " (never on corporate central models)", "B6", "—"),

            // ---------------- interoperability, publishing, administration ----------------
            R("—", "CAP-006", "export IFC / DWG / NWC (typed)", "interoperability", "—", NotSupported, "—", "exec only (level B)", "—", "—"),
            R("—", "CAP-006", "publish (ACC, BIM 360, email)", "publishing", "—", NotSupported, "PermissionsTests (always denied)", "there is no publish operation", "—", "—"),
            R("WF-13", "CAP-001", "separate permissions (10 classes) enforced in the add-in", "administration", "Protocol Permissions · Add-in", NativePending, "PermissionsTests (3)", SimOk, "—", "—"),
            R("WF-13", "CAP-005", "controlled C# exec (query / trial / run) with review and audit", "administration", "Add-in Exec · Scripting", NativePending, "ScriptingTests, TrialGuardTests", SimOk, "—", "—"),
            R("—", "CAP-005", "supervised UI interaction (level C)", "administration", "—", NotSupported, "—", "show/capture only; no automated clicks", "—", "—"),
            R("WF-13", "CAP-001", "Update from ZIP (signature, hashes, compatibility, apply on Revit close, rollback)", "administration", "Setup.Core ZipUpdate · Setup /aplicar-pendiente · Add-in", NativePending, "ZipUpdateTests (12)", SimOk + "; signature: key pending from IT", "—", "—"),
            R("WF-13", "CAP-001", "installer with SHA-256 manifest and /diagnostico", "administration", "Setup.Core", Unit, "SetupTests", "OK", "B8", Rc1),
            R("—", "CAP-001", "measurement of ESTIMATED tokens and wire calls", "administration", "Engine TokenEstimate · AgentHost Bench", Simulated, "Bench_B0_586_OneByOne_vs_Plan", "OK (1,172 → 3 agent calls, simulated)", "B0", "—"),
        };

        private static Row R(string wf, string cap, string op, string fam, string comp, string state, string test, string res, string bench, string prev) =>
            new Row { Workflows = wf, Cap = cap, Operation = op, Family = fam, Component = comp, State = state, Test = test, Result = res, Benchmark = bench, PriorNative = prev };

        /// <summary>Registry consistency problems (known states, CAPs and WFs, coverage). Empty = consistent.</summary>
        public static List<string> Audit()
        {
            var p = new List<string>();
            foreach (var r in All)
            {
                if (!States.Contains(r.State)) p.Add(r.Operation + ": state " + r.State);
                if (!Caps.ContainsKey(r.Cap)) p.Add(r.Operation + ": CAP " + r.Cap);
                if (!OpFamilies.Contains(r.Family)) p.Add(r.Operation + ": family " + r.Family);
                foreach (var w in Split(r.Workflows)) if (!Workflows.ContainsKey(w)) p.Add(r.Operation + ": " + w);
                if (r.State == Native) p.Add(r.Operation + ": TESTED_NATIVE without a 0.8.0 native campaign");
            }
            foreach (var c in Caps.Keys) if (!All.Any(r => r.Cap == c)) p.Add(c + " has no rows");
            foreach (var w in Workflows.Keys) if (!All.Any(r => Split(r.Workflows).Contains(w))) p.Add(w + " has no rows");
            foreach (var f in OpFamilies) if (!All.Any(r => r.Family == f)) p.Add("family " + f + " has no rows");
            return p;
        }

        private static IEnumerable<string> Split(string wf) => (wf ?? "").Split(',').Select(x => x.Trim()).Where(x => x.StartsWith("WF-", StringComparison.Ordinal));

        public static Dictionary<string, int> CountByState() => States.ToDictionary(s => s, s => All.Count(r => r.State == s));

        public static string Markdown(string version, string commitNote)
        {
            string Esc(string s) => (s ?? "").Replace("|", "\\|");
            var sb = new StringBuilder();
            sb.Append("# Traceability matrix — BIMBRIDGE ").Append(version).Append("\n\n");
            sb.Append("Generated from `src/Bimbridge.Engine/Workflow/Traceability.cs` (a test checks that it matches). ").Append(commitNote).Append("\n\n");
            sb.Append("**State** is that of this 0.8.0 build. No row is TESTED_NATIVE: there was no 0.8.0 native campaign (native test environment unavailable). ");
            sb.Append("Native evidence from earlier builds is in *prior native* and does not count as proof for this build. ");
            sb.Append("Tokens are ESTIMATED (characters/4).\n\n");
            sb.Append("Count: ").Append(string.Join(" · ", CountByState().Where(k => k.Value > 0).Select(k => k.Key + " " + k.Value))).Append(" · total ").Append(All.Count).Append("\n\n");
            sb.Append("## Single matrix\n\n| Workflow | CAP | Operation | Family | Component | State | Test | Result | Benchmark | Prior native |\n|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (var r in All)
                sb.Append("| ").Append(Esc(r.Workflows)).Append(" | ").Append(r.Cap).Append(" | ").Append(Esc(r.Operation)).Append(" | ").Append(r.Family).Append(" | ")
                  .Append(Esc(r.Component)).Append(" | ").Append(r.State).Append(" | ").Append(Esc(r.Test)).Append(" | ").Append(Esc(r.Result)).Append(" | ")
                  .Append(Esc(r.Benchmark)).Append(" | ").Append(Esc(r.PriorNative)).Append(" |\n");

            sb.Append("\n## By capability (CAP)\n\n| CAP | Name | Rows | States |\n|---|---|---|---|\n");
            foreach (var c in Caps)
            {
                var rows = All.Where(r => r.Cap == c.Key).ToList();
                sb.Append("| ").Append(c.Key).Append(" | ").Append(c.Value).Append(" | ").Append(rows.Count).Append(" | ")
                  .Append(string.Join(", ", rows.GroupBy(r => r.State).OrderBy(g => Array.IndexOf(States, g.Key)).Select(g => g.Key + " " + g.Count()))).Append(" |\n");
            }
            sb.Append("\n## By workflow\n\n| WF | Name | Operations | Implemented (without 0.8.0 native acceptance) | Deferred / not supported |\n|---|---|---|---|---|\n");
            foreach (var w in Workflows)
            {
                var rows = All.Where(r => Split(r.Workflows).Contains(w.Key)).ToList();
                var ok = rows.Count(r => r.State == Unit || r.State == Simulated || r.State == NativePending);
                var no = rows.Count(r => r.State == Deferred || r.State == NotSupported);
                sb.Append("| ").Append(w.Key).Append(" | ").Append(w.Value).Append(" | ").Append(rows.Count).Append(" | ").Append(ok).Append(" | ").Append(no).Append(" |\n");
            }
            sb.Append("\n## By Revit operation family\n\n| Family | Rows | States |\n|---|---|---|\n");
            foreach (var f in OpFamilies)
            {
                var rows = All.Where(r => r.Family == f).ToList();
                sb.Append("| ").Append(f).Append(" | ").Append(rows.Count).Append(" | ")
                  .Append(string.Join(", ", rows.GroupBy(r => r.State).OrderBy(g => Array.IndexOf(States, g.Key)).Select(g => g.Key + " " + g.Count()))).Append(" |\n");
            }
            sb.Append("\n*Implemented* = the code exists in this build (with or without tests without Revit, see the Test column); for the user it only counts after the native campaign.\n");
            return sb.ToString();
        }
    }
}
