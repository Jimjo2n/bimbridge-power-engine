# Capability matrix — BIMBRIDGE - Power Engine 0.9.3

Generated from `src/Bimbridge.Engine/Capabilities.cs` (a test checks that it matches). *Evidence* column: what was actually checked. What is new in 0.6.0 has not yet run inside Revit: it is compiled for 2022–2027 and tested without Revit; the native test is `tools/acceptance/PROCEDURE_0.6.0.md`.

| Command | What it does | Project | Family | Level | Since | Evidence | Notes |
|---|---|---|---|---|---|---|---|
| ctx / docs | context, open documents with handle dN, capabilities | yes | yes | R | 2022 | tested in Revit 2026 (read, 0.5.0, one sample) | lists RVT and RFA, type, path, modified, read-only, active |
| cat | catalog (levels, views, types, families…) | yes | partial | R | 2022 | tested in Revit 2024 (read, 0.2.x) | in families only types/families/views |
| q / get | queries by category, level, type, set | yes | yes | R | 2022 | tested in Revit 2024 (read, 0.2.x) | document pinned across all pages since 0.5.0 |
| chk lighting_audit | QC of fixtures and tags per view | yes | no | R | 2022 | tested in Revit 2024 (read, 0.2.x) | per-task tag profile: tags=uno|ninguno|ignorar |
| geo | geometry: mounting, clashes, view ranges, rooms | yes | no | R | 2022 | tested in Revit 2024 (read, 0.2.x) | links whose transform fails are excluded and reported |
| show / cap | point at and capture views | yes | yes | R | 2022 | tested in Revit 2024 (read, 0.2.x) | only on the active document (needs the UI) |
| chk model_health | model health: grouped warnings, duplicates, empty required parameters, free MEP connectors, empty sheets and views not on a sheet | yes | no | R | 2022 | compiles 2022–2027 | read-only, paged; does not reproduce Revit's structural checks |
| chk historial | history of long operations with filters, progress of running ones and retry advice | yes | yes | R | 2022 | compiles 2022–2027 | persisted in %LOCALAPPDATA%; never advises repeating anything uncertain |
| chk cobertura | on-demand discovery of the F01–F37 catalog: compact paged rows and full contract by id | yes | yes | R | 2022 | compiles 2022–2027 | immutable in-memory index; does not enter the Revit thread or read assemblies |
| chk copy_monitor | Copy/Monitor: link preflight and inventory, truthful manual handoff and later verification (read-only) | yes | no | R | 2022 | compiles 2022–2027 | there is no public batch Copy API: never declares 'copied'; source→copy correspondence inferred by type and position |
| plan / commit | 46 typed operations with diff and a single Undo (0.8.0: 2D drawing, dimensions, detail components, sheet set; 0.9.1: levels, grids, walls, floors, ceilings, rooms, ducts, pipes, circuits and schedules; also straight conduit and runs with elbows) | yes | partial | P/W | 2022 | compiles 2022–2027 | in families: set, values, move, rotate, mirror, copy, delete; the 0.9.1 modeling operations only in projects |
| exec consulta | general C# over the API; everything is rolled back at the end | yes | yes | R | 2022 | compiles + tested without Revit | Roslyn 4.8 compiler; compilation tested against each year's API without Revit |
| exec ejecutar | general C# with automatic transaction, group or no transaction | yes | yes | W | 2022 | compiles + tested without Revit | tx=auto|grupo|ninguna; warnings classified by FailureDefinitionId |
| fam info / caras | types, parameters, formulas, GUID, connectors, planar faces | yes | yes | R | 2022 | tested in Revit 2026 (read, 0.5.0, one sample) | in a project: per loaded family (connectors of one instance) |
| fam buscar / cargar_archivo | 0.8.0: RFA index only in carpetas_familias; load an indexed, unchanged RFA (sha-256) | yes | no | R / W | 2022 | compiles + tested without Revit | never scans disks; cargar_archivo permission |
| fam abrir | EditFamily from the project → RFA handle (reused only if this project opened it for this family) | yes | no | W | 2022 | compiles 2022–2027 | 0.6.0: provenance by project + family UniqueId |
| fam conector | create or reconcile a connector by semantic key and verify | no | yes | W | 2022 | compiles 2022–2027 | pipe, duct, electrical, conduit, cable tray; explicit radius or diameter |
| fam param | value, formula, create and associate family parameters | no | yes | W | 2022 | compiles 2022–2027 |  |
| fam cargar | load the RFA into the project with IFamilyLoadOptions and revalidate | no | yes | W | 2022 | compiles 2022–2027 | sobrescribir=valores|no |
| doc guardar / guardar_como | Save / SaveAs with file and hash verification | yes | yes | D or W | 2022 | compiles 2022–2027 |  |
| doc sync | synchronize with central and verify | yes | no | D or W | 2022 | compiles 2022–2027 | only workshared models |
| doc pdf | export sheets or a set to PDF and verify the files | yes | no | D or W | 2022 | compiles 2022–2027 | Document.Export(PDFExportOptions), available since 2022 |
| doc ifc | export IFC (2x3, 4 RV, 4 DTV; model or view) and verify the file | yes | no | D or W | 2022 | compiles 2022–2027 | the transaction Revit requires is always rolled back: the model does not change |
| doc tabla_csv | export a schedule to CSV and compare rows | yes | no | D or W | 2022 | compiles 2022–2027 | ViewSchedule.Export with comma and double quotes |
| doc cerrar | close a document that is not the active one | yes | yes | D or W | 2022 | compiles 2022–2027 | Revit does not allow closing the active one through the API |
| res | read long results stored by handle | yes | yes | R | 2022 | compiles + tested without Revit | 7 days on disk |
| clave= / tarea= | idempotency and durable operation journal | yes | yes | — | 2022 | compiles + tested without Revit | commit, exec ejecutar, fam and doc |
| tarea iniciar / reanudar | long tasks split into units, resumable, with save/sync/pdf barriers (0.6.0) | yes | yes | W | 2022 | compiles + tested with simulated Revit (engine/transport) | engine tested with injected faults; Revit backend only compiled |
| tarea estado / resumen / esperar | task memory across clients: compact summary, units, decisions | yes | yes | R | 2022 | compiles + tested with simulated Revit (engine/transport) | both profiles (compact and full) |
| tarea reconciliar / cancelar | explicit decision on unverifiable units; cancellation between units | yes | yes | W | 2022 | compiles + tested with simulated Revit (engine/transport) |  |
| built-in recipes | etiquetar_luces, parametro_por_lote, qc_manifiesto, montaje_soportes, exportar_laminas | yes | no | R/W | 2022 | compiles 2022–2027 | compiled against each year's API by a test |
| Bim.* (scripts) | groups, mounting with real support (rays, links), view range, why it is not visible, manifest | yes | partial | R/W | 2022 | compiles 2022–2027 | the QC logic (manifest, transforms, visibility) does have tests without Revit |
| metricas | characters per call and client to measure consumption (not tokens) | yes | yes | — | 2022 | compiles + tested without Revit | local to the bridge |
| permisos | 0.8.0: 10 classes (read, modify project/family, load file, save, sync, export, publish, dynamic code, UI) | yes | yes | — | 2022 | compiles + tested without Revit | config permisos; empty = derived from the levels; publish always denied |
| Update online (GitHub) | 0.9.1: latest public Release of the repository, no account; verifies published SHA-256, size and version before installing | yes | yes | — | 2022 | compiles + tested without Revit | only from the ribbon and only on click; needs a published Release; actualizacion_online=no disables it |
| Update from ZIP | 0.8.0: manifest, RSA signature, hashes, compatibility; applied when Revit closes, with rollback | yes | yes | — | 2022 | compiles + tested without Revit | only from the ribbon; never closes Revit |
| Setup /diagnostico | installed version, loaded in each Revit, bridge, hashes and pending restart | yes | yes | — | 2022 | compiles + tested without Revit | the installer verifies manifest.json before and after copying |

## By version

| Revit | Add-in runtime | Runner compiler | Reference API used for compiling |
|---|---|---|---|
| 2022 | .NET Framework 4.8 | Roslyn 4.8 (in roslyn folder) | Nice3point 2022.1.80 |
| 2023 | .NET Framework 4.8 | Roslyn 4.8 (in roslyn folder) | Nice3point 2023.1.90 |
| 2024 | .NET Framework 4.8 | Roslyn 4.8 (in roslyn folder) | Nice3point 2024.3.60 |
| 2025 | .NET 8 | Roslyn 4.8 (own load context) | Nice3point 2025.4.60 |
| 2026 | .NET 8 | Roslyn 4.8 (own load context) | Nice3point 2026.4.10 |
| 2027 | .NET 10 | Roslyn 4.8 (own load context) | Nice3point 2027.0.0 |

The real minimum build for each year is not verified: compiling against the reference API does not prove that a method exists in an earlier build. ctx reports the exact build and the runner reports the concrete error (MissingMethodException) if a method does not exist.
