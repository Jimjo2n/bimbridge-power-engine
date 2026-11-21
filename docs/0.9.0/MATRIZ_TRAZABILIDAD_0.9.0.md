# Traceability matrix 0.9.0 — Power Engine

Chain: **Workflow → Capability → Component → Operation → Test → Result**. "Result" is always NOT native unless stated otherwise; every test in Revit is **BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION**.

| Workflow | Capability (status) | Component | Language | Operation | Tests | Result |
|---|---|---|---|---|---|---|
| PDF→model | pdf.info (AVAILABLE) | bimbridge_docworker/pdf.py | Python | `power pdf info` | test_worker (contract, userunit, rotation, encryption) | passes |
| PDF→model | pdf.extract (AVAILABLE) | pdf.py | Python | `power pdf extract` | test_worker extract, scanned→SIN_CONTENIDO, 40 pages | passes |
| PDF→model | pdf.tables (AVAILABLE) | pdf.py | Python | `power pdf tables` | test_worker tables, grid discarded | passes |
| PDF→model | pdf.symbols (AVAILABLE) | pdf.py | Python | `power pdf symbols` | test_worker symbols against ground truth (3 variants, 74 candidates) | passes |
| PDF→model | pdf.grids (AVAILABLE) | pdf.py | Python | `power pdf grids` | test_worker grids and bubbles; PdfToModelWorkflowTests (30 anchors) | passes |
| Specifications | docx.extract (AVAILABLE) | docx.py / DocxExtractor.cs | Python / C# | `power docx` | .NET = Python equivalence, XXE rejected | passes |
| Scanned PDF | pdf.scanned_interpretation (REQUIRES_AUTHORIZATION) | — | external | — | — | not implemented; requires authorization (would send images) |
| Geometry | geo.transform3 / nearest / match / duplicates / segment_contacts / components (AVAILABLE) | GeometryKernels.cs, kernels.rs, ffi.rs | C# + Rust | dispatcher | C#=FFI=IPC equivalence at 0/1/7/500/6000; invalid inputs; cancellation; cargo test 6 | passes |
| PDF→model | wf.registration (AVAILABLE) | Registration.cs | C# | `power flujo` | RegistrationTests 6; rotation, scale, bad anchors, affine, reflection | passes |
| PDF→model | wf.placement_plan (AVAILABLE) | PlacementPlanner.cs, PdfToPlan.cs | C# | `power flujo`, `power-engine flujo-pdf` | 72 placed ±0.02 ft in 3 PDFs; escalation to ambiguous; batches ≤500; PowerCommandsTests | passes |
| PDF→model | wf.placement_execute (NATIVE_TEST_PENDING) | add-in `place` | C# + Revit API | `plan` → `commit` | N9-03, N9-04 | BLOCKED |
| QA/QC | wf.verify_placement (AVAILABLE) | PlacementVerifier.cs, IndependentVerifier.cs | C# | library / AgentHost | 5 seeded defects detected one by one; `no_independiente` with the same source | passes |
| MEP | mep.network (AVAILABLE) | MepNetwork.cs | C# (+Rust) | `power red` | AdvancedEnginesTests (C# and Rust), PowerCommandsTests red | passes; native read N9-05 BLOCKED |
| Risers | mep.riser (AVAILABLE) | RiserGraph.cs | C# | library | AdvancedEnginesTests | passes (no `power` command yet) |
| Groups | groups.variants (AVAILABLE) | GroupVariants.cs | C# | library | AdvancedEnginesTests | passes (no `power` command yet) |
| Annotation | annot.layout (EXPERIMENTAL) | AnnotationLayout.cs | C# | library | AdvancedEnginesTests | passes on synthetic data; N9-06 BLOCKED |
| Plan regions | revit.plan_region_edit (API_LIMITATION) | — | — | — | — | Revit 2024 does not expose PlanRegion editing |
| Benchmark | bench.pyrevit (DEFERRED) | Benchmarks.cs | C# | B1–B8 | catalog with corrected references (Hardening090Tests) | pyRevit restricted; NO_MEDIDO |
| Benchmark | Power Engine | PowerEngineBench.cs | C# | `power-engine bench|calibrar` | PowerCommandsTests bench | measured in a Linux container; PC pending |
| Update | mandatory signature in production (P0-1/2) | ZipUpdate.cs, Profiles.cs | C# | Update from ZIP | ZipUpdateTests 16 | passes |
| QA/QC | demonstrable independence (P0-3) | IndependentVerifier.cs | C# | `verificar_independiente` | Hardening090Tests | passes |
| Delivery | critical receipts with revision and provenance (P0-4) | Delivery.cs | C# | receipts | Hardening090Tests | passes |
| 2D drawing | degenerate and collinear (P1-5) | Draw2D.cs | C# | `detail_*` | Hardening090Tests | passes |
| Installation | Power Engine next to the bridge | SetupCore.cs, build.sh, vendor_python.py | C# / Python | installer | SetupTests PowerEngine (hash) | passes |
