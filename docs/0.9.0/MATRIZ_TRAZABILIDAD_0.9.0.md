# Matriz de trazabilidad 0.9.0 — Power Engine

Cadena: **Flujo → Capacidad → Componente → Operación → Prueba → Resultado**. "Resultado" es siempre NO nativo salvo que se diga lo contrario; toda prueba en Revit está **BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION**.

| Flujo | Capacidad (estado) | Componente | Lenguaje | Operación | Pruebas | Resultado |
|---|---|---|---|---|---|---|
| PDF→modelo | pdf.info (AVAILABLE) | bimbridge_docworker/pdf.py | Python | `power pdf info` | test_worker (contrato, userunit, rotación, cifrado) | pasa |
| PDF→modelo | pdf.extract (AVAILABLE) | pdf.py | Python | `power pdf extract` | test_worker extract, escaneado→SIN_CONTENIDO, 40 páginas | pasa |
| PDF→modelo | pdf.tables (AVAILABLE) | pdf.py | Python | `power pdf tables` | test_worker tablas, rejilla descartada | pasa |
| PDF→modelo | pdf.symbols (AVAILABLE) | pdf.py | Python | `power pdf symbols` | test_worker símbolos contra verdad (3 variantes, 74 candidatos) | pasa |
| PDF→modelo | pdf.grids (AVAILABLE) | pdf.py | Python | `power pdf grids` | test_worker ejes y burbujas; PdfToModelWorkflowTests (30 anclas) | pasa |
| Especificaciones | docx.extract (AVAILABLE) | docx.py / DocxExtractor.cs | Python / C# | `power docx` | equivalencia .NET = Python, XXE rechazado | pasa |
| PDF escaneado | pdf.scanned_interpretation (REQUIRES_AUTHORIZATION) | — | externo | — | — | no implementado; requiere autorización (enviaría imágenes) |
| Geometría | geo.transform3 / nearest / match / duplicates / segment_contacts / components (AVAILABLE) | GeometryKernels.cs, kernels.rs, ffi.rs | C# + Rust | despachador | equivalencia C#=FFI=IPC en 0/1/7/500/6000; entradas inválidas; cancelación; cargo test 6 | pasa |
| PDF→modelo | wf.registration (AVAILABLE) | Registration.cs | C# | `power flujo` | RegistrationTests 6; giro, escala, anclas malas, afín, reflejo | pasa |
| PDF→modelo | wf.placement_plan (AVAILABLE) | PlacementPlanner.cs, PdfToPlan.cs | C# | `power flujo`, `power-engine flujo-pdf` | 72 colocados ±0,02 ft en 3 PDF; escalado a ambiguo; lotes ≤500; PowerCommandsTests | pasa |
| PDF→modelo | wf.placement_execute (NATIVE_TEST_PENDING) | add-in `place` | C# + Revit API | `plan` → `commit` | N9-03, N9-04 | BLOCKED |
| QA/QC | wf.verify_placement (AVAILABLE) | PlacementVerifier.cs, IndependentVerifier.cs | C# | biblioteca / AgentHost | 5 defectos sembrados detectados 1 a 1; `no_independiente` con la misma fuente | pasa |
| MEP | mep.network (AVAILABLE) | MepNetwork.cs | C# (+Rust) | `power red` | AdvancedEnginesTests (C# y Rust), PowerCommandsTests red | pasa; lectura nativa N9-05 BLOCKED |
| Montantes | mep.riser (AVAILABLE) | RiserGraph.cs | C# | biblioteca | AdvancedEnginesTests | pasa (sin orden `power` todavía) |
| Grupos | groups.variants (AVAILABLE) | GroupVariants.cs | C# | biblioteca | AdvancedEnginesTests | pasa (sin orden `power` todavía) |
| Anotación | annot.layout (EXPERIMENTAL) | AnnotationLayout.cs | C# | biblioteca | AdvancedEnginesTests | pasa en sintético; N9-06 BLOCKED |
| Regiones de plano | revit.plan_region_edit (API_LIMITATION) | — | — | — | — | Revit 2024 no expone la edición de PlanRegion |
| Benchmark | bench.pyrevit (DEFERRED) | Benchmarks.cs | C# | B1–B8 | catálogo con referencias corregidas (Hardening090Tests) | pyRevit restringido; NO_MEDIDO |
| Benchmark | Power Engine | PowerEngineBench.cs | C# | `power-engine bench|calibrar` | PowerCommandsTests bench | medido en contenedor Linux; PC pendiente |
| Actualización | firma obligatoria en producción (P0-1/2) | ZipUpdate.cs, Profiles.cs | C# | Actualizar desde ZIP | ZipUpdateTests 16 | pasa |
| QA/QC | independencia demostrable (P0-3) | IndependentVerifier.cs | C# | `verificar_independiente` | Hardening090Tests | pasa |
| Entrega | recibos críticos con revisión y procedencia (P0-4) | Delivery.cs | C# | recibos | Hardening090Tests | pasa |
| Dibujo 2D | degenerados y colineales (P1-5) | Draw2D.cs | C# | `detail_*` | Hardening090Tests | pasa |
| Instalación | Power Engine junto al puente | SetupCore.cs, build.sh, vendor_python.py | C# / Python | instalador | SetupTests PowerEngine (hash) | pasa |
