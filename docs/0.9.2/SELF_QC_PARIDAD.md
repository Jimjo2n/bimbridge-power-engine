# Self-QC de paridad con pyRevit y handoff posterior al PR #30

Base del handoff: `8ce066f8857595c4cf643ece2b3d08a1c1a10f21`. Continuación local desde `main` después del PR #31 en `feat/self-qc-remaining`.

Cada ficha admite dos estados. `IMPLEMENTADO` requiere el flujo en el código y evidencia funcional fuera de Revit. Se usan los archivos originales disponibles y casos sintéticos con resultados esperados cuando faltan. Las observaciones históricas se identifican como tales. La aceptación en vivo y los benchmarks quedan fuera de este self-QC. El alcance de compatibilidad con Revit 2027 se aborda después del cierre.

## Handoff

| Requisito | Estado | Evidencia offline |
|---|---|---|
| Choques: conservar resultado de booleana, ausencia de sólidos y errores de extracción, transformación y cálculo | IMPLEMENTADO | `0b207a5`. Se mantienen índice, rayos, tolerancia y tramos. |
| Choques: una fila, clasificación conservadora, volumen parcial, cobertura y muestras por instancia de vínculo | IMPLEMENTADO | `GeoComputeTests`. El resumen paginado expone cobertura como metadato opcional. La clasificación se comprueba con cuerpos simulados. |
| Correlación y una transición de respuesta/timeout en el puente | IMPLEMENTADO | `c9dc513`. Identificador opcional fuera de los argumentos. Se informa si llegó a completarse el envío de la trama. |
| Recibos de escritura con clave, SourceBinding, auditoría de commit directo y cola | IMPLEMENTADO | `SourceBoundWrite_ReconnectsToOriginalReceiptAndRetryUsesTheSameOutcome` cubre escritura directa y trabajo en cola con pipe real, modelo simulado, pérdida de respuesta y un único efecto. `CommitReceipt_UsesAuditOutcomeAndPreservesReportedDifferences` verifica la auditoría. |
| Protocolo 2 y negociación de capacidades | IMPLEMENTADO | `tools/aceptacion/compat-protocolo2/probar.ps1` compiló y ejecutó puente anterior con host actual, puente actual con host anterior y ambos actuales. Verificó la consulta normal, el aviso al pedir un recibo a un host anterior y la negociación de `res` paginable. El host usa el motor de cada revisión con un modelo simulado. Note: this tool was removed in the BIMBRIDGE rename, because bridges before and after the rename use different pipe names. |
| Agregación incremental, proyección, revisión y destino | IMPLEMENTADO | Reutiliza filtros y normalización. Acumula por grupo y conserva la ruta de conjuntos guardados, conteo e instantáneas. |
| Lectura completa, presupuesto UTF-8 y detalle paginable | IMPLEMENTADO | `FullQueryRecoversAnOversizeCellThroughResThenContinuesWithCursor` recupera una celda UTF-8 mayor a 1 MiB vía `res` y verifica la página siguiente con el cursor y la misma revisión. Usa MCP y pipe reales con datos simulados. |
| Descubrimiento por relevancia en español e inglés | IMPLEMENTADO | Reutiliza `check name=cobertura` y aplica filtros antes de ordenar. Predeterminado de tres resultados con relevancia. Caso de prueba por ID, nombres, alias y filtro. |
| Piloto PDF: transportar requisitos de anfitrión y expectativas por elemento | IMPLEMENTADO | Lotes por contrato y parámetros; identidad de ítem opcional. El piloto admite familias por nivel y rechaza requisitos de anfitrión físico en la planificación. |
| Piloto PDF: releer antes de consolidar, asociar entradas e IDs, revertir diferencias | IMPLEMENTADO | Verificación compartida de aplicación y ensayo antes de `Assimilate`. Conserva verificación independiente separada. `PdfExecutionTests` cubre el flujo en la frontera del plan con resultados de prueba. |
| Espera del instalador, reapertura, cierre manual y convivencia de procesos | IMPLEMENTADO | `SetupTests` y `ZipUpdateTests` verifican dos lecturas consecutivas sin instancias, reapertura, cancelación con una de dos todavía abierta y espera sin cerrar el proceso del usuario. El EXE permite cerrar durante la espera y protege la escritura. |

## Plan de trabajo diario

| Ficha | Función o modificación | Estado | Evidencia offline |
|---|---|---|---|
| PY-01 | Fijar sesión, instancia y documento | IMPLEMENTADO | `TwoRevitInstances_RequireAnExplicitTarget_AndStaleHandlesCannotWrite`, `Resolver_AmbiguousTitle_ListsCandidates_AndPicksNone` y `Identity_WrongPid_ReadOnlyLevels_Workshared_AndOtherDocument_AreRefused` cubren instancias, homónimos y rechazo de una escritura al destino equivocado. |
| PY-02 | Consultar y guardar un conjunto filtrado | IMPLEMENTADO | Motor de consulta, conjuntos guardados y destino fijo. `PlanOutsideScope_IsRejected_BeforeAnyCommit` protege la selección exportada y `QuerySnapshotTests` cubre la relectura paginada por revisión. |
| PY-03 | Inventario de 33 vistas y siete campos | IMPLEMENTADO | Se reprocesaron las respuestas históricas de BIMBRIDGE y de pyRevit CPython e IronPython. Coincidieron 33 IDs en los siete campos, con tres vistas por cada una de las 11 láminas. |
| PY-04 | Referencias geométricas de vínculos | IMPLEMENTADO | `geo montaje` y `geo holgura` con `referencias=true` devuelven elemento, instancia y punto anfitrión. `MountReportIdentifiesTheControllingLinkedCopyAndHostFacePoint` verifica copias desplazadas con el mismo ID local. |
| PY-05 | Inventario PDF con notas, leyendas, inclusión y exclusión | IMPLEMENTADO | En dos recortes del PDF original E-1.3.03 se confirmaron dos L8 y se excluyeron el dispositivo S y dos trazos secundarios. `pdf symbols` agrupa impresiones idénticas y conserva la procedencia. `Pdf_Register_Plan_Simulate_Verify` comprueba el registro a coordenadas del modelo con anclas sintéticas. |
| PY-06 | Resolución de familias y sustitución dentro de grupos | IMPLEMENTADO | `group_variant` valida categoría y montaje. `VariantOnlyChangesTheTwoSpecifiedTypesAndPreservesTheWallHostedMember` detecta cambios accidentales de anfitrión y comprueba la sustitución de los dos tipos en la instantánea. |
| PY-07 | Colocación idempotente | IMPLEMENTADO | `PlannerReusesEachExistingFixtureOnceAndOnlyOnTheRequestedLevel` comprueba el nivel, las coincidencias compartidas y cero lotes al repetir. `Pdf_Register_Plan_Simulate_Verify` cubre cantidad, centro y orientación. |
| PY-08 | Medición y ajuste en losa, rampa y cielo | IMPLEMENTADO | `GeoComputeTests` cubre superficies planas y rampas, holgura, empotramiento y ausencia de soporte. `snap_to_surface` prepara el ajuste; `set_workset` cambia la asignación y la llamada gestionada lee la altura de la luminaria. |
| PY-09 | Mantenimiento de llamadas estáticas de luminarias | IMPLEMENTADO | `text_note`, `llamadas_audit` y `text_note_refresh` enlazan por UID y actualizan la selección. `ManagedCalloutDetectsAChangedTypeHeightOrAnchor` verifica texto, cambio de tipo y desplazamiento; `Annotation_NoCollisions_WhenSpaceExists_LeaderWhenFar_LegibilityChecked` cubre legibilidad y colisión en papel. |
| PY-10 | QC independiente de inventario, vistas y tags | IMPLEMENTADO | `ManifestInventory_DetectsMissingAndPhysicalDuplicates_WithoutCountingCrossViewReferencesTwice` comprueba faltantes, duplicados físicos y una referencia legítima en dos vistas. |
| PY-11 | Reglas de clasificación de tableros y desconectivos | IMPLEMENTADO | `EquipmentRulesKeepBothUnitPanelsAndPlaceOnlyTheDocumentedSuiteDisconnect` conserva ambos tableros y limita la suite al desconectivo HVAC documentado. |
| PY-12 | Propagación de grupos y correspondencia de IDs | IMPLEMENTADO | `group_place` comprueba prototipo, nivel, giro, workset y miembros y admite reutilización. `Groups_VariantsDetectedUnderRotationAndMirror` verifica variantes; `PreservesStableIdentityAndOnlyMapsUnambiguousRebuiltMembers` comprueba la correspondencia tras reconstrucción. |
| PY-13 | Terminales desde markups | IMPLEMENTADO | `RotatedMarkupProducesOneIdempotentPlacementAndPreservesTheExistingBox` e `InvalidDirectionIsRejectedAndAmbiguousRegistrationDoesNotProducePlans` validan coordenadas, reutilización y dirección. `terminales_audit` distingue llegada geométrica y conexión. |
| PY-14 | Conduit, fittings y reencaminamiento tipado | IMPLEMENTADO | `ConduitReplacementAndNewTerminalAttachmentsAreDistinctContracts` y `ConduitRoute_RejectsCollapsedAndTwoDimensionalSegments` validan el contrato. `ConduitRouteScope` captura los extremos; la aplicación relee continuidad, diámetro, nivel, eje y workset y revierte si hay borrados fuera de alcance. |
| PY-15 | Cambios por lote y presentación de sistemas | IMPLEMENTADO | `Rc1_BatchWrite_DifferentValuesPerElement_OneCommit_Verified` verifica el lote. `vista_audit expectativas=` compara por vista y elemento en el caso Power/MSDE de dos vistas, con un caso que detecta la regresión. |
| PY-16 | Reparar referencias de cotas, etiquetas y líderes | IMPLEMENTADO | `AnnotationRepairRequiresOneAnnotationAndExplicitDestinationReferences` verifica las entradas. `annotation_rebind` recrea cotas lineales y etiquetas locales conservando el formato y valida antes de quitar la anotación original. |
| PY-17 | QC integral por ruta | IMPLEMENTADO | `ruta_audit` informa por ruta y nivel. `Mep_ThreeLayersStaySeparate` separa topología, proximidad y conexión física. `GeoComputeTests` prueba la distinción de candidatos, choque sólido y fallo de evidencia; `RouteAuditCheck` aplica el cálculo de contención con diferencia de sólidos. |
| PY-18 | Diagnóstico de plantillas, filtros, vínculos y regiones | IMPLEMENTADO | `ChecksPowerAndMsdeIndependentlyInSystemAndCompositeViews` verifica expectativas de plantilla, filtro, color configurado y candidatos. `vista_audit` también informa el vínculo y `ViewRange_CeilingPlan_InsideAboveForeignAndCrop` comprueba el análisis del rango. |
| PY-19 | Callouts, cortes, 3D y láminas de QC | IMPLEMENTADO | `CoordinationSheetKeepsEachViewportBoundToItsPlannedView` valida el plan de una lámina con corte, 3D y callout. `Geometry_RectPolygonArea_AndPaperViewRoundTrip` verifica las conversiones de papel. |
| PY-20 | Grafo de riser a partir de PDF y Excel | IMPLEMENTADO | `RiserSourceWorkflowTests` verifica reconciliación, derivación omitida, medidor y extremo abierto. El lector procesó 34 aristas de un tramo de `ONE_LINE` y 226 de `UNIT FEEDER` en el libro original, con dirección de celda. Se localizaron los grafos históricos y el PDF. |
| PY-21 | Orden, preset y configuración persistente del set | IMPLEMENTADO | Se comprobó el registro original con 26 hojas únicas en orden explícito. `SavedSetAcceptsUnorderedMembershipAndRejectsSubstitutionOrDuplicate` verifica las mismas reglas productivas de miembros, preset y tolerancia de papel. |
| PY-22 | Verificación del PDF de entrega | IMPLEMENTADO | `pdf verify` aceptó el PDF original del riser de una página con sello QC y alimentadores magenta. La otra copia difirió por huella y ausencia de sello. El render confirmó sello, recorte y color en la primera copia. |
| PY-23 | Guardado, sincronización y recuperación interrumpida | IMPLEMENTADO | `PersistentCommit_TimesOutAfterApplying_IsObserved_NotRepeated`, `Nrt05_PersistentWrite_SavedAndVerifiedOnDisk_NoDuplicateCommit` y `V080_Handoff_AfterCrash_ProposesReconcile_NotRepeat_AndAfterResumeIsDone` cubren persistencia simulada, reanudación y ausencia de duplicado. `DocOps.Sync` lee estado, central y evento. |
| PY-24 | Preparar Batch Copy con intervención humana | IMPLEMENTADO | `Preflight_FreezesTheWholeObjective_AndARepeatedPreflightCannotReplaceIt` y `NoUiExecutor_EndsInATruthfulManualHandoff_AndOpenListsOnlyUnfinished`. El histórico de las 88 piezas acredita el traspaso manual para Batch Copy y Finish. |
| PY-25 | Confirmar monitorización después de guardar | IMPLEMENTADO | `Synthetic88_PerfectMonitoredCopy_Verifies_EvenWithARotatedLink` y `SeededDefects_AreEachReportedInTheirOwnBucket`. Los registros originales tienen 88 copias monitorizadas, 88 coincidencias, cero vínculos equivocados y guardado confirmado. |
| PY-26 | Editar RFA, recargar y comprobar conectores | IMPLEMENTADO | `fam conector` registra clave, cara y características. `fam cargar verificar_puertos=true` compara la muestra del tipo activo del RFA y detecta discrepancias de flujo, sistema y tamaño sin depender del total. `ReloadChecksNamedPortsEvenWhenTheirTotalHasNotChanged` verifica los tres puertos con datos sintéticos. |
| PY-27 | Drafting View, marco, nota y viewport | IMPLEMENTADO | `DrawingOps_ValidInputs_Parse` cubre la vista y las operaciones de detalle, y `text_note` aplica negrita y subrayado a la nota completa. |
| PY-28 | Índices, filtros de tablas y cantidades | IMPLEMENTADO | `ScheduleAuditTests` verifica coincidencias, diferencias, índices inválidos, población no evaluable y el comando compacto. |

**Alcance de la evidencia:** los modelos y operaciones de Revit se representaron mediante contratos, simulaciones y registros previos. Los PDF disponibles se leyeron con el worker actual. Los archivos y las capturas del proyecto permanecen en el equipo del usuario.

Resultado del cierre offline: **39 IMPLEMENTADO, 0 NO IMPLEMENTADO**. Pasaron las 818 pruebas funcionales .NET y las 18 pruebas focalizadas del worker PDF y XLSX. Compilaron los complementos 2022–2026.

Ejemplo en el perfil compacto:

```text
chk tabla_audit tabla="Lighting Fixtures" cantidad_esperada=2 ids_esperados=[11,22] celdas_esperadas=[{"fila":0,"columna":1,"texto":"2"}]
```

Los índices empiezan en cero dentro del cuerpo visible. La cantidad es de elementos únicos. Para cantidades que la tabla muestra agrupadas, compare el texto de la celda. La población de tablas con vínculos o tablas incrustadas devuelve `no_evaluable` cuando se pide contrastarla. Para comparar filtros, reutilice los objetos con `campo`, `parametro_id`, `operador` y `valor` devueltos en `filtros`.

```json
{"op":"sheet_set","nombre":"Entrega","hojas":["E-102","E-101"],"preset_pdf":{"papel":"Default","color":"gris","zoom":"ajustar","combinar":true}}
```

Tras aplicar el plan y guardar el proyecto, `doc pdf conjunto=Entrega` usa ese orden y los ajustes guardados. `orden=numero` ordena por número de plano. Los conjuntos históricos sin configuración guardada conservan el orden por número.

```text
chk vista_audit vistas=711,712 ids=12345,12346
chk lighting_audit views=711,712 exacto=true manifiesto={"elementos":[{"ref":"L-01","x":1000,"y":2000,"nivel":"L1","tipos":["A"]}]}
pdf verify ruta="C:/entrega/actual.pdf" anterior="C:/entrega/anterior.pdf" esperado=[{"textos":["E-101","QC REV 02"],"papel_pt":[612,792],"rgb_vectorial":[[255,0,255]]}]
```

`pdf verify` se usa desde `power`. `sha256_esperado=` contrasta el archivo con el recibo de exportación. Para revisar símbolos, ejecute `pdf symbols ruta=… paginas=[1] trazabilidad=true` en `power`. Pase a `flujo` las decisiones con `id_simbolo`, `decision` (`incluir` o `excluir`), `evidencia` y `codigo` cuando se corrija la clasificación. Al pasar `decisiones`, el flujo genera colocaciones únicamente para las inclusiones revisadas.

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

Para reconciliar un grupo reconstruido, conserve `instantanea_miembros` del primer `grupos_audit` y pásela como `anterior` cuando compruebe el grupo nuevo. Para reparar una cota, lea los destinos geométricos y pase su lista completa ordenada en `refs_estables`. Repare primero las anotaciones cuando dependen del recorrido anterior y se ha creado el nuevo como operación independiente.

`power riser` devuelve `esperado_dibujo` con las aristas y sus rótulos. Pase ese valor a `chk riser_geometria vista=… estilo=… esperado_dibujo=[…]`. Use el resultado `observado` en `power riser_audit` junto con el mismo PDF, XLSX y criterio de precedencia.

En libros con varias secciones, `fila_encabezado`, `fila_inicio` y `fila_final` delimitan la lectura. `columnas` asigna encabezados del archivo a `id`, `origen`, `destino` y, si aplica, `abierto`.

`chk vista_audit` admite `expectativas=[{"vista":711,"id":12345,"plantilla_id":501,"filtro_id":601,"filtro_activo":true,"linea_proyeccion":[255,0,255],"en_colector_de_vista":true}]`. La comparación usa el color configurado en el filtro indicado y la presencia en el colector de la vista. `fam cargar doc=d2 en=d1 verificar_puertos=true` coteja los conectores nombrados del tipo activo del RFA con los de una instancia colocada de ese tipo.

Cuando `power` devuelve `formato_res=json_fragmentos`, ejecute la orden que indica `lectura` y siga el cursor hasta la última página. Concatene sólo las líneas de datos, sin saltos añadidos, para recuperar el JSON original.

## Evidencia histórica offline BM-01

Se comparó materializar y agrupar frente a acumular por grupo, con las mismas filas sintéticas. Se usaron 30 repeticiones por variante y tamaño tras el calentamiento. Se comprobó la igualdad del resultado serializado antes de cronometrar.

| Filas | Mediana materializado → incremental | p95 materializado → incremental | KB asignados materializado → incremental |
|---|---:|---:|---:|
| 1.000 | 0,48 → 0,35 ms | 0,94 → 0,48 ms | 475 → 458 |
| 10.000 | 5,62 → 4,01 ms | 9,07 → 4,78 ms | 4.635 → 4.378 |
| 50.000 | 26,48 → 9,60 ms | 27,82 → 11,03 ms | 22.825 → 21.800 |

El ensayo mide proyección y agregación fuera de Revit, sin transporte. Las dos variantes se ejecutaron en bloques consecutivos. La campaña de benchmarks queda para más adelante.

La suite funcional se puede ejecutar con `dotnet test tests/Bimbridge.Tests -c Release --filter 'FullyQualifiedName!~MeasurementTests&FullyQualifiedName!~Bench_'`. CI remoto y minutos de Actions usados en esta rama: cero.
