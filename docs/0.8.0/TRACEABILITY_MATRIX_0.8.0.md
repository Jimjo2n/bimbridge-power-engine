# Traceability matrix — BIMBRIDGE 0.8.0

Generated from `src/Bimbridge.Engine/Workflow/Traceability.cs` (a test checks that it matches). Internal candidate; native acceptance pending.

**State** is that of this 0.8.0 build. No row is TESTED_NATIVE: there was no 0.8.0 native campaign (native test environment unavailable). Native evidence from earlier builds is in *prior native* and does not count as proof for this build. Tokens are ESTIMATED (characters/4).

Count: TESTED_UNIT 7 · TESTED_SIMULATED 2 · NATIVE_ACCEPTANCE_PENDING 36 · NOT_SUPPORTED 4 · DEFERRED 7 · total 56

## Single matrix

| Workflow | CAP | Operation | Family | Component | State | Test | Result | Benchmark | Prior native |
|---|---|---|---|---|---|---|---|---|---|
| WF-13 | CAP-001 | ctx / identity / instances | core | Add-in · AgentHost RevitLink | NATIVE_ACCEPTANCE_PENDING | Rc1_FailClosed_MissingContextFlag (5), AgentHostRevitTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B7 | rc1 14ec21d · Revit 2024 · disposable copy (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-13 | CAP-001 | per-document capabilities (ctx) and this registry | core | Engine Capabilities + Traceability | TESTED_UNIT | Capabilities_DocMatchesCode, Traceability_DocMatchesCode | OK | — | 0.5.0 · Revit 2026 · read, one sample |
| WF-02,WF-05 | CAP-002 | consultar / q / get / params (paging, projection) | core | Add-in Query · AgentHost ReadRows | NATIVE_ACCEPTANCE_PENDING | Rc1 paging (ForcePage, changing total) | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B0 | 0.2.x · Revit 2024 · read; rc1 14ec21d · Revit 2024 · disposable copy (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-02,WF-09,WF-13 | CAP-003 | plan → commit (diff, one Undo, clave idempotency) | core | Add-in PlanBuilder/PlanApplier · AgentHost PlanRunner | NATIVE_ACCEPTANCE_PENDING | PlanTests, PlanQueueTests, AgentHostRevitTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (0.8.0 changed the commit audit) | B0, B7 | rc1 14ec21d · Revit 2024 · disposable copy (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-13 | CAP-003 | per-document exclusion: lease + fencing | core | AgentHost ExecJournal/Lease | NATIVE_ACCEPTANCE_PENDING | LeaseFencingTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B7 | rc1 14ec21d · Revit 2024 · disposable copy (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-13 | CAP-003 | durable audit of every persistent commit (intent → result) | core | Add-in RequestHandler.Commit · Engine AuditLog | NATIVE_ACCEPTANCE_PENDING | AuditLogTests, V080_Nrt07Harness | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (closes rc1 H2) | B7 | — |
| WF-13 | CAP-004 | resume after a crash; uncertain ⇒ observe, never repeat | core | AgentHost PlanRunner/RevitBackend | NATIVE_ACCEPTANCE_PENDING | NRT-03/05/06 simulated, V080_Handoff_AfterCrash | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B7 | rc1 14ec21d · Revit 2024 · disposable copy (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-13 | CAP-004 | IPC response lost after commit (NRT-07) | core | AgentHost RevitBackend + Faults | NATIVE_ACCEPTANCE_PENDING | V080_Nrt07Harness_LostCommitResponse | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B7 | — |
| WF-13,WF-14 | CAP-015 | structured handoff (committed / uncertain / next safe step) | core | AgentHost Handoff | TESTED_SIMULATED | V080_Handoff_AfterCrash_ProposesReconcile | OK | B7 | — |
| WF-13 | CAP-015 | signed events, scheduling and notifications | core | — | DEFERRED | — | 0.9.0 (minimal events) | — | — |
| WF-13 | CAP-001 | atomic writes of state files (leftover temp file) | core | Engine Home.WriteAtomic | TESTED_UNIT | DurabilityTests | OK; rc1 native leftover not rechecked | B7 | — |
| WF-02,WF-10,WF-11 | CAP-008 | requirements with provenance and authority; inference never OBSERVED | core | Engine Workflow.Requirements | TESTED_UNIT | WorkflowTests (9) | OK | B2, B5 | — |
| WF-02,WF-11 | CAP-008 | verificar_independiente: independent source → model → differences | coordination | Engine IndependentVerifier · AgentHost RevitBackend | NATIVE_ACCEPTANCE_PENDING | V080_IndependentVerification_AgainstModel | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (read only) | B2, B5 | — |
| WF-02 | CAP-008 | source adapters (extract PDF/tables with coordinates) | coordination | — | DEFERRED | — | 0.9.0 | B2 | — |
| WF-04,WF-09,WF-11 | CAP-006 | delivery states (12 stages) and receipts | documents | Engine Workflow.Delivery | TESTED_UNIT | WorkflowTests | OK | B6 | — |
| WF-02 | CAP-005 | set_param / set_values / fijar_parametro_lote | editing | Add-in PlanApplier · AgentHost | NATIVE_ACCEPTANCE_PENDING | PlanTests, Rc11_SlowIdleRevit, Bench_B0_586 | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B0 | rc1 14ec21d · Revit 2024 · disposable copy (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-02 | CAP-005 | change_type / set_type_param / set_level / set_workset / set_elevation | editing | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | PlanTests, WriteEngineTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | — |
| WF-02,WF-05 | CAP-005 | move / rotate / mirror / copy / delete | editing | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests.BuildMoved, PlanTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | — | — |
| WF-02 | CAP-005 | place (instance at a point, slab/level mounting) | modeling | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests.Parse_Validates | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | — |
| — | CAP-005 | walls, floors, ceilings, roofs, stairs (typed creation) | modeling | — | NOT_SUPPORTED | — | exec only (level B, dynamic_code) | — | — |
| WF-05 | CAP-010 | group_create / ungroup / group_place | editing | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | PlanTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B3 | — |
| WF-05 | CAP-010 | group coverage by type/floor plan and propagation | editing | — | DEFERRED | — | 0.9.0 | B3 | — |
| WF-03 | CAP-009 | geo mounting / clashes / ranges / rooms (meshes, rays, links) | geometry | Add-in GeoRunner · Engine TriMesh | NATIVE_ACCEPTANCE_PENDING | GeoComputeTests, MeshTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | 0.2.x · Revit 2024 · read |
| WF-03 | CAP-009 | snap_to_surface (real bottom face of slab/ramp) | geometry | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | PlanTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | — |
| WF-02 | CAP-009 | drawing–model registration with anchors | geometry | — | DEFERRED | — | 0.9.0 | B2 | — |
| WF-01,WF-03 | CAP-005 | view_create (floor plan, RCP, section, 3D, drafting) / view_set (template, range, detail) | views | Add-in PlanApplier · WriteKit | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests, Draw2DTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B1 | — |
| WF-01,WF-08 | CAP-007 | view_hide / view_override (colors, halftone, transparency) | views | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B1 | — |
| WF-03,WF-11 | CAP-007 | chk lighting_audit / manifest QC per view | views | Add-in Check · Engine QC | NATIVE_ACCEPTANCE_PENDING | QcFixtureTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | 0.2.x · Revit 2024 · read |
| WF-06,WF-11 | CAP-007 | show / capture (point out and capture) | views | Add-in Show/Capture | NATIVE_ACCEPTANCE_PENDING | — | native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | — | 0.2.x · Revit 2024 · read |
| WF-04,WF-09 | CAP-006 | sheet_create / sheet_place | sheets | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B6 | — |
| WF-04,WF-09 | CAP-006 | sheet_set (persistent sheet set) | sheets | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | SheetSetOpTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (Revit decides the order) | B6 | — |
| WF-04,WF-09 | CAP-006 | doc pdf (export sheets or set and verify files) | documents | Add-in DocOps | NATIVE_ACCEPTANCE_PENDING | — | native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (requires export permission) | B6 | — |
| WF-04 | CAP-006 | verify the exported PDF (pages, sha, revision) | documents | Engine Delivery (receipts) | TESTED_UNIT | WorkflowTests (page ≠ set, sha ≠ export) | OK; the PDF reader is 0.9.0 | B6 | — |
| WF-02 | CAP-013 | tag / tag_move | annotations | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | PlanTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B3 | — |
| WF-06 | CAP-013 | text_note / filled_region (validated boundary) | annotations | Add-in PlanApplier · Engine Draw2D | NATIVE_ACCEPTANCE_PENDING | Draw2DTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B5 | — |
| WF-10 | CAP-012 | detail_line / detail_rect / detail_polygon / detail_arc (line styles) | annotations | Add-in PlanApplier · Engine Draw2D | NATIVE_ACCEPTANCE_PENDING | Draw2DTests (12) | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B5 | — |
| WF-10 | CAP-012 | detail_component (symbols and detail families) | annotations | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests.Catalog | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B5 | — |
| WF-06 | CAP-013 | dimension (linear dimension between detail lines) | annotations | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | Draw2DTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B5 | — |
| WF-10,WF-11 | CAP-012 | riser graph (nodes/edges) and connectivity audit | annotations | — | DEFERRED | — | 0.9.0 | B5 | — |
| WF-06,WF-08 | CAP-013 | legibility on paper (density, leaders, collisions) | annotations | Engine Draw2D (paper↔view, align, distribute) | DEFERRED | Draw2DTests (transforms) | 2D base ready; optimization 0.9.0 | — | — |
| WF-12 | CAP-014 | fam info / faces | families | Add-in FamilyOps | NATIVE_ACCEPTANCE_PENDING | — | native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B8 | 0.5.0 · Revit 2026 · read, one sample |
| WF-12,WF-07 | CAP-014 | fam abrir / conector / param / cargar | families | Add-in FamilyOps | NATIVE_ACCEPTANCE_PENDING | ScriptingTests (compiles per year) | native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (requires modificar_familia permission) | B8 | — |
| WF-12 | CAP-014 | fam buscar (index of authorized folders, no disk browsing) | families | Engine FamilyLibrary | TESTED_UNIT | FamilyLibraryTests (3) | OK | B8 | — |
| WF-12 | CAP-014 | fam cargar_archivo (indexed, unchanged RFA, sha-256) | families | Engine FamilyLibrary · Add-in | NATIVE_ACCEPTANCE_PENDING | FamilyLibraryTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (requires cargar_archivo permission) | B8 | — |
| WF-07,WF-08 | CAP-011 | physical routing, fittings, stubs and dimension reconciliation | MEP | — | DEFERRED | — | 0.9.0 | B4 | — |
| WF-08 | CAP-011 | clash detection with links | coordination | Add-in GeoRunner | NATIVE_ACCEPTANCE_PENDING | GeoComputeTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B4 | 0.2.x · Revit 2024 · read |
| WF-04,WF-13 | CAP-004 | doc guardar / guardar_como (file and hash verification) | documents | Add-in DocOps | NATIVE_ACCEPTANCE_PENDING | NRT-05 simulated | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (requires guardar permission) | B6 | rc1 14ec21d · Revit 2024 · disposable copy (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-04,WF-13 | CAP-004 | doc sync (workshared only, sincronizar permission) | documents | Add-in DocOps | NATIVE_ACCEPTANCE_PENDING | WorksharedPolicyTests | native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (never on corporate central models) | B6 | — |
| — | CAP-006 | export IFC / DWG / NWC (typed) | interoperability | — | NOT_SUPPORTED | — | exec only (level B) | — | — |
| — | CAP-006 | publish (ACC, BIM 360, email) | publishing | — | NOT_SUPPORTED | PermissionsTests (always denied) | there is no publish operation | — | — |
| WF-13 | CAP-001 | separate permissions (10 classes) enforced in the add-in | administration | Protocol Permissions · Add-in | NATIVE_ACCEPTANCE_PENDING | PermissionsTests (3) | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | — | — |
| WF-13 | CAP-005 | controlled C# exec (query / trial / run) with review and audit | administration | Add-in Exec · Scripting | NATIVE_ACCEPTANCE_PENDING | ScriptingTests, TrialGuardTests | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | — | — |
| — | CAP-005 | supervised UI interaction (level C) | administration | — | NOT_SUPPORTED | — | show/capture only; no automated clicks | — | — |
| WF-13 | CAP-001 | Update from ZIP (signature, hashes, compatibility, apply on Revit close, rollback) | administration | Setup.Core ZipUpdate · Setup /aplicar-pendiente · Add-in | NATIVE_ACCEPTANCE_PENDING | ZipUpdateTests (12) | OK without Revit; native: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION; signature: key pending from IT | — | — |
| WF-13 | CAP-001 | installer with SHA-256 manifest and /diagnostico | administration | Setup.Core | TESTED_UNIT | SetupTests | OK | B8 | rc1 14ec21d · Revit 2024 · disposable copy (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| — | CAP-001 | measurement of ESTIMATED tokens and wire calls | administration | Engine TokenEstimate · AgentHost Bench | TESTED_SIMULATED | Bench_B0_586_OneByOne_vs_Plan | OK (1,172 → 3 agent calls, simulated) | B0 | — |

## By capability (CAP)

| CAP | Name | Rows | States |
|---|---|---|---|
| CAP-001 | Identity, preflight and capabilities | 7 | TESTED_UNIT 3, TESTED_SIMULATED 1, NATIVE_ACCEPTANCE_PENDING 3 |
| CAP-002 | Typed, compact queries | 1 | NATIVE_ACCEPTANCE_PENDING 1 |
| CAP-003 | Transactional plan and mutual exclusion | 3 | NATIVE_ACCEPTANCE_PENDING 3 |
| CAP-004 | Checkpoint, resume and sync | 4 | NATIVE_ACCEPTANCE_PENDING 4 |
| CAP-005 | Reusable operations on elements and views | 8 | NATIVE_ACCEPTANCE_PENDING 6, NOT_SUPPORTED 2 |
| CAP-006 | Sheet, set, preset and verifiable issue | 7 | TESTED_UNIT 2, NATIVE_ACCEPTANCE_PENDING 3, NOT_SUPPORTED 2 |
| CAP-007 | Native and visual verification | 3 | NATIVE_ACCEPTANCE_PENDING 3 |
| CAP-008 | Sources, versions and independent cross-check | 3 | TESTED_UNIT 1, NATIVE_ACCEPTANCE_PENDING 1, DEFERRED 1 |
| CAP-009 | Geometric registration and mounting | 3 | NATIVE_ACCEPTANCE_PENDING 2, DEFERRED 1 |
| CAP-010 | Groups, variants and propagation | 2 | NATIVE_ACCEPTANCE_PENDING 1, DEFERRED 1 |
| CAP-011 | Physical networks and coordination | 2 | NATIVE_ACCEPTANCE_PENDING 1, DEFERRED 1 |
| CAP-012 | 2D riser graph and composition | 3 | NATIVE_ACCEPTANCE_PENDING 2, DEFERRED 1 |
| CAP-013 | Annotation legibility and demonstration views | 4 | NATIVE_ACCEPTANCE_PENDING 3, DEFERRED 1 |
| CAP-014 | Family editor and MEP connectors | 4 | TESTED_UNIT 1, NATIVE_ACCEPTANCE_PENDING 3 |
| CAP-015 | Events, handoff and extensions | 2 | TESTED_SIMULATED 1, DEFERRED 1 |

## By workflow

| WF | Name | Operations | Implemented (without 0.8.0 native acceptance) | Deferred / not supported |
|---|---|---|---|---|
| WF-01 | Configure template and view classification | 2 | 2 | 0 |
| WF-02 | Place lighting from drawings | 11 | 9 | 2 |
| WF-03 | Light mounting and visibility in RCP | 4 | 4 | 0 |
| WF-04 | Issue lighting set and QC sheet | 7 | 7 | 0 |
| WF-05 | Unit, panel and group prototypes | 4 | 3 | 1 |
| WF-06 | Model interior markups and clean up RCP DIM | 4 | 3 | 1 |
| WF-07 | Model and audit two physical feed families | 2 | 1 | 1 |
| WF-08 | Adjust crossings, colors and coordination sheet | 4 | 2 | 2 |
| WF-09 | Prepare a partial print set | 5 | 5 | 0 |
| WF-10 | Build a 2D single-line riser | 4 | 3 | 1 |
| WF-11 | Audit and prepare the riser for issue | 6 | 5 | 1 |
| WF-12 | Classroom exercises and family editing | 4 | 4 | 0 |
| WF-13 | Repair connectors and resume work | 16 | 15 | 1 |
| WF-14 | Transfer knowledge and guide the user | 1 | 1 | 0 |

## By Revit operation family

| Family | Rows | States |
|---|---|---|
| modeling | 2 | NATIVE_ACCEPTANCE_PENDING 1, NOT_SUPPORTED 1 |
| editing | 5 | NATIVE_ACCEPTANCE_PENDING 4, DEFERRED 1 |
| families | 4 | TESTED_UNIT 1, NATIVE_ACCEPTANCE_PENDING 3 |
| geometry | 3 | NATIVE_ACCEPTANCE_PENDING 2, DEFERRED 1 |
| MEP | 1 | DEFERRED 1 |
| views | 4 | NATIVE_ACCEPTANCE_PENDING 4 |
| sheets | 2 | NATIVE_ACCEPTANCE_PENDING 2 |
| annotations | 7 | NATIVE_ACCEPTANCE_PENDING 5, DEFERRED 2 |
| documents | 5 | TESTED_UNIT 2, NATIVE_ACCEPTANCE_PENDING 3 |
| interoperability | 1 | NOT_SUPPORTED 1 |
| coordination | 3 | NATIVE_ACCEPTANCE_PENDING 2, DEFERRED 1 |
| publishing | 1 | NOT_SUPPORTED 1 |
| administration | 6 | TESTED_UNIT 1, TESTED_SIMULATED 1, NATIVE_ACCEPTANCE_PENDING 3, NOT_SUPPORTED 1 |
| core | 12 | TESTED_UNIT 3, TESTED_SIMULATED 1, NATIVE_ACCEPTANCE_PENDING 7, DEFERRED 1 |

*Implemented* = the code exists in this build (with or without tests without Revit, see the Test column); for the user it only counts after the native campaign.
