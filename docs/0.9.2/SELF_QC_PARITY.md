# pyRevit parity self-QC and handoff after PR #30

Handoff base: `8ce066f8857595c4cf643ece2b3d08a1c1a10f21`. Local continuation from `main` after PR #31 on `feat/self-qc-remaining`.

Each item has two possible states. `IMPLEMENTED` requires the workflow in the code and functional evidence outside Revit. The original files are used where available, and synthetic cases with expected results where they are missing. Historical observations are identified as such. Live acceptance and benchmarks are outside this self-QC. Compatibility scope with Revit 2027 is addressed after closure.

## Handoff

| Requirement | Status | Offline evidence |
|---|---|---|
| Clashes: keep the boolean result, absence of solids and extraction, transform and computation errors | IMPLEMENTED | `0b207a5`. Index, rays, tolerance and slices are kept. |
| Clashes: one row, conservative classification, partial volume, coverage and samples per link instance | IMPLEMENTED | `GeoComputeTests`. The paginated summary exposes coverage as optional metadata. Classification is checked with simulated bodies. |
| Correlation and a single response/timeout transition in the bridge | IMPLEMENTED | `c9dc513`. Optional identifier outside the arguments. It reports whether sending the frame completed. |
| Write receipts with key, SourceBinding, direct-commit audit and queue | IMPLEMENTED | `SourceBoundWrite_ReconnectsToOriginalReceiptAndRetryUsesTheSameOutcome` covers a direct write and a queued job with a real pipe, a simulated model, a lost response and a single effect. `CommitReceipt_UsesAuditOutcomeAndPreservesReportedDifferences` verifies the audit. |
| Protocol 2 and capability negotiation | IMPLEMENTED | `tools/aceptacion/compat-protocolo2/probar.ps1` built and ran the previous bridge with the current host, the current bridge with the previous host, and both current. It verified the normal query, the warning when a receipt is requested from a previous host, and the negotiation of paginable `res`. The host uses each revision's engine with a simulated model. Note: this tool was removed in the BIMBRIDGE rename, because bridges before and after the rename use different pipe names. |
| Incremental aggregation, projection, revision and target | IMPLEMENTED | Reuses filters and normalization. Accumulates per group and keeps the saved-set path, count and snapshots. |
| Full read, UTF-8 budget and paginable detail | IMPLEMENTED | `FullQueryRecoversAnOversizeCellThroughResThenContinuesWithCursor` recovers a UTF-8 cell larger than 1 MiB through `res` and verifies the next page with the cursor and the same revision. Uses real MCP and pipe with simulated data. |
| Relevance-based discovery in Spanish and English | IMPLEMENTED | Reuses `check name=cobertura` and applies filters before sorting. Default of three results with relevance. Test case by ID, names, aliases and filter. |
| PDF pilot: carry host requirements and per-element expectations | IMPLEMENTED | Batches by contract and parameters; optional item identity. The pilot accepts level-based families and rejects physical host requirements at planning. |
| PDF pilot: reread before consolidating, associate inputs and IDs, roll back differences | IMPLEMENTED | Shared verification for apply and trial before `Assimilate`. Keeps independent verification separate. `PdfExecutionTests` covers the workflow at the plan boundary with test results. |
| Installer wait, reopening, manual close and process coexistence | IMPLEMENTED | `SetupTests` and `ZipUpdateTests` verify two consecutive reads without instances, reopening, cancellation with one of two still open, and waiting without closing the user's process. The EXE allows closing during the wait and protects the write. |

## Daily work plan

| Item | Function or change | Status | Offline evidence |
|---|---|---|---|
| PY-01 | Pin session, instance and document | IMPLEMENTED | `TwoRevitInstances_RequireAnExplicitTarget_AndStaleHandlesCannotWrite`, `Resolver_AmbiguousTitle_ListsCandidates_AndPicksNone` and `Identity_WrongPid_ReadOnlyLevels_Workshared_AndOtherDocument_AreRefused` cover instances, same-name documents and refusing a write to the wrong target. |
| PY-02 | Query and save a filtered set | IMPLEMENTED | Query engine, saved sets and fixed target. `PlanOutsideScope_IsRejected_BeforeAnyCommit` protects the exported selection and `QuerySnapshotTests` covers the paginated reread per revision. |
| PY-03 | Inventory of 33 views and seven fields | IMPLEMENTED | The historical responses from BIMBRIDGE and from pyRevit CPython and IronPython were reprocessed. 33 IDs matched on all seven fields, with three views on each of the 11 sheets. |
| PY-04 | Geometric references of links | IMPLEMENTED | `geo montaje` and `geo holgura` with `referencias=true` return element, instance and host point. `MountReportIdentifiesTheControllingLinkedCopyAndHostFacePoint` verifies offset copies with the same local ID. |
| PY-05 | PDF inventory with notes, legends, inclusion and exclusion | IMPLEMENTED | In two crops of the original PDF E-1.3.03, two L8 were confirmed and the S device and two secondary strokes were excluded. `pdf symbols` groups identical prints and keeps provenance. `Pdf_Register_Plan_Simulate_Verify` checks registration to model coordinates with synthetic anchors. |
| PY-06 | Family resolution and substitution inside groups | IMPLEMENTED | `group_variant` validates category and mounting. `VariantOnlyChangesTheTwoSpecifiedTypesAndPreservesTheWallHostedMember` detects accidental host changes and checks the substitution of the two types in the snapshot. |
| PY-07 | Idempotent placement | IMPLEMENTED | `PlannerReusesEachExistingFixtureOnceAndOnlyOnTheRequestedLevel` checks the level, shared matches and zero batches on repeat. `Pdf_Register_Plan_Simulate_Verify` covers quantity, center and orientation. |
| PY-08 | Measurement and snapping on slab, ramp and ceiling | IMPLEMENTED | `GeoComputeTests` covers flat surfaces and ramps, clearance, embedment and missing support. `snap_to_surface` prepares the snap; `set_workset` changes the assignment and the managed callout reads the fixture height. |
| PY-09 | Maintenance of static fixture callouts | IMPLEMENTED | `text_note`, `llamadas_audit` and `text_note_refresh` link by UID and update the selection. `ManagedCalloutDetectsAChangedTypeHeightOrAnchor` verifies text, type change and offset; `Annotation_NoCollisions_WhenSpaceExists_LeaderWhenFar_LegibilityChecked` covers legibility and collision on paper. |
| PY-10 | Independent QC of inventory, views and tags | IMPLEMENTED | `ManifestInventory_DetectsMissingAndPhysicalDuplicates_WithoutCountingCrossViewReferencesTwice` checks missing items, physical duplicates and a legitimate reference in two views. |
| PY-11 | Classification rules for panels and disconnects | IMPLEMENTED | `EquipmentRulesKeepBothUnitPanelsAndPlaceOnlyTheDocumentedSuiteDisconnect` keeps both panels and limits the suite to the documented HVAC disconnect. |
| PY-12 | Group propagation and ID correspondence | IMPLEMENTED | `group_place` checks prototype, level, rotation, workset and members and allows reuse. `Groups_VariantsDetectedUnderRotationAndMirror` verifies variants; `PreservesStableIdentityAndOnlyMapsUnambiguousRebuiltMembers` checks the correspondence after a rebuild. |
| PY-13 | Terminals from markups | IMPLEMENTED | `RotatedMarkupProducesOneIdempotentPlacementAndPreservesTheExistingBox` and `InvalidDirectionIsRejectedAndAmbiguousRegistrationDoesNotProducePlans` validate coordinates, reuse and direction. `terminales_audit` distinguishes geometric arrival from connection. |
| PY-14 | Conduit, fittings and typed rerouting | IMPLEMENTED | `ConduitReplacementAndNewTerminalAttachmentsAreDistinctContracts` and `ConduitRoute_RejectsCollapsedAndTwoDimensionalSegments` validate the contract. `ConduitRouteScope` captures the endpoints; applying rereads continuity, diameter, level, axis and workset and rolls back if there are deletions out of scope. |
| PY-15 | Batch changes and system presentation | IMPLEMENTED | `Rc1_BatchWrite_DifferentValuesPerElement_OneCommit_Verified` verifies the batch. `vista_audit expectativas=` compares per view and element in the two-view Power/MSDE case, with a case that detects the regression. |
| PY-16 | Repair dimension, tag and leader references | IMPLEMENTED | `AnnotationRepairRequiresOneAnnotationAndExplicitDestinationReferences` verifies the inputs. `annotation_rebind` recreates linear dimensions and local tags keeping the format and validates before removing the original annotation. |
| PY-17 | End-to-end QC per route | IMPLEMENTED | `ruta_audit` reports per route and level. `Mep_ThreeLayersStaySeparate` separates topology, proximity and physical connection. `GeoComputeTests` tests the distinction between candidates, solid clash and evidence failure; `RouteAuditCheck` applies the containment computation with solid difference. |
| PY-18 | Diagnosis of templates, filters, links and regions | IMPLEMENTED | `ChecksPowerAndMsdeIndependentlyInSystemAndCompositeViews` verifies expectations for template, filter, configured color and candidates. `vista_audit` also reports the link and `ViewRange_CeilingPlan_InsideAboveForeignAndCrop` checks the range analysis. |
| PY-19 | Callouts, sections, 3D and QC sheets | IMPLEMENTED | `CoordinationSheetKeepsEachViewportBoundToItsPlannedView` validates the plan of a sheet with a section, 3D and a callout. `Geometry_RectPolygonArea_AndPaperViewRoundTrip` verifies paper conversions. |
| PY-20 | Riser graph from PDF and Excel | IMPLEMENTED | `RiserSourceWorkflowTests` verifies reconciliation, omitted branch, meter and open end. The reader processed 34 edges of a `ONE_LINE` segment and 226 of `UNIT FEEDER` in the original workbook, with cell address. The historical graphs and the PDF were located. |
| PY-21 | Order, preset and persistent configuration of the set | IMPLEMENTED | The original register with 26 unique sheets in explicit order was checked. `SavedSetAcceptsUnorderedMembershipAndRejectsSubstitutionOrDuplicate` verifies the same production rules for members, preset and paper tolerance. |
| PY-22 | Verification of the delivery PDF | IMPLEMENTED | `pdf verify` accepted the original one-page riser PDF with a QC seal and magenta feeders. The other copy differed in fingerprint and lacked the seal. The render confirmed seal, crop and color in the first copy. |
| PY-23 | Save, sync and interrupted recovery | IMPLEMENTED | `PersistentCommit_TimesOutAfterApplying_IsObserved_NotRepeated`, `Nrt05_PersistentWrite_SavedAndVerifiedOnDisk_NoDuplicateCommit` and `V080_Handoff_AfterCrash_ProposesReconcile_NotRepeat_AndAfterResumeIsDone` cover simulated persistence, resume and absence of duplicates. `DocOps.Sync` reads status, central and event. |
| PY-24 | Prepare Batch Copy with human intervention | IMPLEMENTED | `Preflight_FreezesTheWholeObjective_AndARepeatedPreflightCannotReplaceIt` and `NoUiExecutor_EndsInATruthfulManualHandoff_AndOpenListsOnlyUnfinished`. The history of the 88 pieces documents the manual handoff for Batch Copy and Finish. |
| PY-25 | Confirm monitoring after saving | IMPLEMENTED | `Synthetic88_PerfectMonitoredCopy_Verifies_EvenWithARotatedLink` and `SeededDefects_AreEachReportedInTheirOwnBucket`. The original records have 88 monitored copies, 88 matches, zero wrong links and a confirmed save. |
| PY-26 | Edit RFA, reload and check connectors | IMPLEMENTED | `fam conector` records key, face and characteristics. `fam cargar verificar_puertos=true` compares the sample of the RFA's active type and detects flow, system and size mismatches without relying on the total. `ReloadChecksNamedPortsEvenWhenTheirTotalHasNotChanged` verifies the three ports with synthetic data. |
| PY-27 | Drafting View, frame, note and viewport | IMPLEMENTED | `DrawingOps_ValidInputs_Parse` covers the view and the detail operations, and `text_note` applies bold and underline to the whole note. |
| PY-28 | Indexes, schedule filters and quantities | IMPLEMENTED | `ScheduleAuditTests` verifies matches, differences, invalid indexes, non-evaluable population and the compact command. |

**Evidence scope:** Revit models and operations were represented through contracts, simulations and previous records. The available PDFs were read with the current worker. The project's files and captures remain on the user's machine.

Offline closure result: **39 IMPLEMENTED, 0 NOT IMPLEMENTED**. All 818 functional .NET tests and the 18 focused tests of the PDF and XLSX worker passed. The 2022–2026 add-ins built.

Example in the compact profile:

```text
chk tabla_audit tabla="Lighting Fixtures" cantidad_esperada=2 ids_esperados=[11,22] celdas_esperadas=[{"fila":0,"columna":1,"texto":"2"}]
```

Indexes start at zero inside the visible body. The quantity counts unique elements. For quantities the schedule shows grouped, compare the cell text. The population of schedules with links or embedded schedules returns `no_evaluable` when a comparison is requested. To compare filters, reuse the objects with `campo`, `parametro_id`, `operador` and `valor` returned in `filtros`.

```json
{"op":"sheet_set","nombre":"Entrega","hojas":["E-102","E-101"],"preset_pdf":{"papel":"Default","color":"gris","zoom":"ajustar","combinar":true}}
```

After applying the plan and saving the project, `doc pdf conjunto=Entrega` uses that order and the saved settings. `orden=numero` sorts by sheet number. Historical sets without a saved configuration keep the order by number.

```text
chk vista_audit vistas=711,712 ids=12345,12346
chk lighting_audit views=711,712 exacto=true manifiesto={"elementos":[{"ref":"L-01","x":1000,"y":2000,"nivel":"L1","tipos":["A"]}]}
pdf verify ruta="C:/entrega/actual.pdf" anterior="C:/entrega/anterior.pdf" esperado=[{"textos":["E-101","QC REV 02"],"papel_pt":[612,792],"rgb_vectorial":[[255,0,255]]}]
```

`pdf verify` is used from `power`. `sha256_esperado=` checks the file against the export receipt. To review symbols, run `pdf symbols ruta=… paginas=[1] trazabilidad=true` in `power`. Pass to `flujo` the decisions with `id_simbolo`, `decision` (`incluir` or `excluir`), `evidencia` and `codigo` when the classification is corrected. When `decisiones` is passed, the workflow generates placements only for the reviewed inclusions.

```text
chk llamadas_audit vista=711
chk grupos_audit grupo=8200
chk anotaciones_audit ids=101,102
chk ruta_audit ruta=R-01 nivel=L15 ids=201,202,203 inicio=[0,0,9] fin=[20,10,9] losas=[{"id":301,"vinculo":401}] obstaculos=[{"id":302,"vinculo":401}]
```

```json
{"op":"text_note","view":711,"punto":[12,30],"luminaria":951,"altura_param":"Mounting Height","negrita":true}
{"op":"text_note_refresh","ids":"$llamadas_desactualizadas"}
{"op":"group_place","tipo":"Unit A - Deck 16","nivel":"LEVEL 16","puntos":[[12,30,0]],"rot":90,"prototipo":8200,"reutilizar":true}
{"op":"conduit_route","tipo":"EMT","nivel":"LEVEL 15","diametro":"1.25 in","puntos":[[0,0,9],[20,0,9],[20,10,9]],"reemplazar_ids":[201,202,203]}
{"op":"annotation_rebind","ids":[101],"destino":205}
```

To reconcile a rebuilt group, keep `instantanea_miembros` from the first `grupos_audit` and pass it as `anterior` when checking the new group. To repair a dimension, read the geometric targets and pass their complete ordered list in `refs_estables`. Repair the annotations first when they depend on the previous run and the new one was created as an independent operation.

`power riser` returns `esperado_dibujo` with the edges and their labels. Pass that value to `chk riser_geometria vista=… estilo=… esperado_dibujo=[…]`. Use the `observado` result in `power riser_audit` together with the same PDF, XLSX and precedence criterion.

In workbooks with several sections, `fila_encabezado`, `fila_inicio` and `fila_final` bound the read. `columnas` maps file headers to `id`, `origen`, `destino` and, where applicable, `abierto`.

`chk vista_audit` accepts `expectativas=[{"vista":711,"id":12345,"plantilla_id":501,"filtro_id":601,"filtro_activo":true,"linea_proyeccion":[255,0,255],"en_colector_de_vista":true}]`. The comparison uses the color configured in the given filter and presence in the view's collector. `fam cargar doc=d2 en=d1 verificar_puertos=true` compares the named connectors of the RFA's active type with those of a placed instance of that type.

When `power` returns `formato_res=json_fragmentos`, run the command given in `lectura` and follow the cursor to the last page. Concatenate only the data lines, without added line breaks, to recover the original JSON.

## Historical offline evidence BM-01

Materializing and grouping was compared against accumulating per group, with the same synthetic rows. 30 repetitions per variant and size were used after warm-up. Equality of the serialized result was checked before timing.

| Rows | Median materialized → incremental | p95 materialized → incremental | KB allocated materialized → incremental |
|---|---:|---:|---:|
| 1,000 | 0.48 → 0.35 ms | 0.94 → 0.48 ms | 475 → 458 |
| 10,000 | 5.62 → 4.01 ms | 9.07 → 4.78 ms | 4,635 → 4,378 |
| 50,000 | 26.48 → 9.60 ms | 27.82 → 11.03 ms | 22,825 → 21,800 |

The trial measures projection and aggregation outside Revit, without transport. The two variants ran in consecutive blocks. The benchmark campaign is left for later.

The functional suite can be run with `dotnet test tests/Bimbridge.Tests -c Release --filter 'FullyQualifiedName!~MeasurementTests&FullyQualifiedName!~Bench_'`. Remote CI and Actions minutes used on this branch: zero.
