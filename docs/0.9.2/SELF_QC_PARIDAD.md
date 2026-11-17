# Self-QC de paridad con pyRevit y handoff posterior al PR #30

Base del handoff: `8ce066f8857595c4cf643ece2b3d08a1c1a10f21`. Continuación local desde `main` después del PR #31 en `feat/self-qc-remaining`.

Cada comprobación tiene uno de dos estados. `IMPLEMENTADO` indica que el criterio de la fila está cubierto con evidencia. `NO IMPLEMENTADO` indica que falta código o una comprobación exigida. La última columna identifica cuál falta. Las pruebas nativas se harán cuando el usuario abra su instancia de laboratorio. Los benchmarks quedan para una fase posterior y están fuera de esta lista funcional. El cierre requiere todas las filas en `IMPLEMENTADO`. Revit 2027 empieza después de ese cierre.

## Handoff

| Requisito | Estado | Evidencia o pendiente |
|---|---|---|
| Choques: conservar resultado de booleana, ausencia de sólidos y errores de extracción, transformación y cálculo | IMPLEMENTADO | `0b207a5`. Se mantienen índice, rayos, tolerancia y tramos. |
| Choques: una fila, clasificación conservadora, volumen parcial, cobertura y muestras por instancia de vínculo | IMPLEMENTADO | `GeoComputeTests`. El resumen paginado expone cobertura como metadato opcional. Los casos sintéticos RVT quedan para la campaña nativa. |
| Correlación y una transición de respuesta/timeout en el puente | IMPLEMENTADO | `c9dc513`. Identificador opcional fuera de los argumentos. Se informa si llegó a completarse el envío de la trama. |
| Recibos de escritura con clave, SourceBinding, auditoría de commit directo y cola | NO IMPLEMENTADO | La prueba con pipe real pierde la respuesta, reconecta el puente, recupera el recibo persistido y comprueba que un reintento con SourceBinding no duplica el efecto. Cubre escritura directa y una tarea simulada con job. Falta comprobar los cierres de la cola real de Revit. |
| Protocolo 2 y negociación de capacidades | IMPLEMENTADO | `tools/aceptacion/compat-protocolo2/probar.ps1` compiló y ejecutó puente anterior con host actual, puente actual con host anterior y ambos actuales. Verificó la consulta normal, el aviso al pedir un recibo a un host anterior y la negociación de `res` paginable. El host usa el motor de cada revisión con un modelo simulado. |
| Agregación incremental, proyección, revisión y destino | IMPLEMENTADO | Reutiliza filtros y normalización. Acumula por grupo y conserva la ruta de conjuntos guardados, conteo e instantáneas. |
| Lectura completa, presupuesto UTF-8 y detalle paginable | IMPLEMENTADO | `FullQueryRecoversAnOversizeCellThroughResThenContinuesWithCursor` recupera una celda UTF-8 mayor a 1 MiB vía `res` y verifica la página siguiente con el cursor y la misma revisión. Usa MCP y pipe reales con datos simulados. |
| Descubrimiento por relevancia en español e inglés | IMPLEMENTADO | Reutiliza `check name=cobertura` y aplica filtros antes de ordenar. Predeterminado de tres resultados con relevancia. Caso de prueba por ID, nombres, alias y filtro. |
| Piloto PDF: transportar requisitos de anfitrión y expectativas por elemento | IMPLEMENTADO | Lotes por contrato y parámetros; identidad de ítem opcional. El piloto admite familias por nivel y rechaza requisitos de anfitrión físico en la planificación. |
| Piloto PDF: releer antes de consolidar, asociar entradas e IDs, revertir diferencias | IMPLEMENTADO | Verificación compartida de aplicación y ensayo antes de `Assimilate`. Conserva verificación independiente separada. Equiparación entre lo planificado, lo creado y el resultado nativo se debe ejecutar en la campaña. |
| Espera del instalador, reapertura, cierre manual y convivencia de procesos | NO IMPLEMENTADO | El EXE y la actualización desde ZIP exigen dos comprobaciones consecutivas con todas las instancias cerradas. El EXE permite cancelar la espera y bloquea el cierre de su ventana mientras escribe los archivos. Las pruebas simuladas cubren la reapertura y la cancelación con una de dos instancias todavía abierta. Falta comprobar el flujo completo en Windows con los procesos reales. |

## Plan de trabajo diario

| Ficha | Función o modificación | Estado | Qué falta |
|---|---|---|---|
| PY-01 | Fijar sesión, instancia y documento | NO IMPLEMENTADO | Repetir con dos modelos homónimos y corte de conexión. |
| PY-02 | Consultar y guardar un conjunto filtrado | IMPLEMENTADO | La ruta existente se conserva. El caso de relectura tras edición se prueba en Revit. |
| PY-03 | Inventario de 33 vistas y siete campos | NO IMPLEMENTADO | Se comprobó offline que la evidencia histórica contiene las 33 filas y los siete campos. Falta obtener la lectura del modelo con la compilación candidata y compararla por ID. |
| PY-04 | Referencias geométricas de vínculos | NO IMPLEMENTADO | `geo montaje` y `geo holgura` con `referencias=true` devuelven el elemento, la instancia vinculada y las coordenadas de la cara que controla en el anfitrión. Una prueba sintética distingue dos copias con el mismo ID local y verifica el punto elegido. Falta ejecutar el caso con dos vínculos de Revit. |
| PY-05 | Inventario PDF con notas, leyendas, inclusión y exclusión | NO IMPLEMENTADO | El inventario conserva ID, página, posición, forma, rótulo, leyenda y evidencia de revisión. Las decisiones se vinculan al PDF y gobiernan la colocación. Falta repetir la comparación de una zona de E-1.3.03 contra la selección manual. |
| PY-06 | Resolución de familias y sustitución dentro de grupos | NO IMPLEMENTADO | `group_variant` aplica el mapa explícito de tipos dentro de la variante y verifica categoría, montaje, anfitrión, coordenadas y orientación de miembros. Falta comprobar el grupo con downlights redondos, cuadrados y apliques wall-hosted en Revit. |
| PY-07 | Colocación idempotente | NO IMPLEMENTADO | La prueba offline comprueba que el plan excluye preexistencias de otro nivel, señala coincidencias compartidas y genera cero lotes al repetir con las dos luces presentes. Falta probar el cuadrante real. |
| PY-08 | Medición y ajuste en losa, rampa y cielo | NO IMPLEMENTADO | Se usa el ajuste existente y se verifica el nivel y posición del piloto. Falta el caso de los tres montajes y la etiqueta. |
| PY-09 | Mantenimiento de llamadas estáticas de luminarias | NO IMPLEMENTADO | `text_note` vincula la llamada a una luminaria por UID en el RVT. `llamadas_audit` detecta cambios y `text_note_refresh` regenera sólo las llamadas seleccionadas. Falta verificar en el RCP DIM el cambio de tipo, altura, posición y la separación de anotaciones. |
| PY-10 | QC independiente de inventario, vistas y tags | IMPLEMENTADO | `lighting_audit` acepta un inventario independiente en `manifiesto`. `ManifestInventory_DetectsMissingAndPhysicalDuplicates_WithoutCountingCrossViewReferencesTwice` demuestra faltantes, duplicados físicos y una referencia legítima visible y etiquetada en dos vistas con datos simulados. |
| PY-11 | Reglas de clasificación de tableros y desconectivos | NO IMPLEMENTADO | `power equipos` usa reglas por ámbito y código con evidencia por elemento. `equipos_audit` contrasta el tipo, posición, orientación y workset y admite un inventario observado para señalar sobrantes. Falta revisar en el modelo la unidad con dos tableros y la suite con un desconectivo HVAC. |
| PY-12 | Propagación de grupos y correspondencia de IDs | NO IMPLEMENTADO | `group_place` usa el tipo de variante elegido y verifica nivel, origen, giro, workset y miembros contra el prototipo; `reutilizar=true` reconoce la instancia ya comprobada. `grupos_audit` cruza los miembros por UID o firma unívoca. Falta probar los dos niveles y el reintento con un grupo reconstruido. |
| PY-13 | Terminales desde markups | NO IMPLEMENTADO | `power terminales` registra puntos y documenta reutilizaciones. `terminales_audit` contrasta tipo, posición, giro, UP/DN, workset y distingue llegada geométrica de conexión física. Falta reproducir el caso real de caja, dos sentidos y stub sin puerto. |
| PY-14 | Conduit, fittings y reencaminamiento tipado | NO IMPLEMENTADO | `conduit_route` admite reemplazar los tramos y codos acotados, conserva los extremos, reconecta terminales y revierte si Revit intenta borrar dependientes ajenos. Relee conexiones, tipo, diámetro, nivel, eje y workset antes de consolidar. Falta controlar los cinco recorridos en el deck de prueba. |
| PY-15 | Cambios por lote y presentación de sistemas | NO IMPLEMENTADO | Verificar el estado efectivo simultáneo de las dos vistas. |
| PY-16 | Reparar referencias de cotas, etiquetas y líderes | NO IMPLEMENTADO | `anotaciones_audit` lee referencias estables y presentación. `annotation_rebind` sustituye una etiqueta local por su nuevo elemento o una cota lineal por sus referencias explícitas, comprobando las referencias antes de consolidar. Falta el ejercicio de una cota y una etiqueta según el plan, con la anotación original todavía presente durante la reparación. |
| PY-17 | QC integral por ruta | NO IMPLEMENTADO | `ruta_audit` contrasta conectividad, volumen fuera de losas, stubs autorizados y choques sólidos por ruta. Los casos sin evidencia sólida se informan como no evaluables. Falta ejecutar el deck sembrado para comprobar los cuatro hallazgos esperados. |
| PY-18 | Diagnóstico de plantillas, filtros, vínculos y regiones | NO IMPLEMENTADO | `vista_audit` aporta evidencia simultánea de varias vistas, filtros activos, overrides RGB, vínculo, regiones y candidatos del colector. Falta validar la plantilla derivada y la visibilidad y el color efectivos del caso Power DN. |
| PY-19 | Callouts, cortes, 3D y láminas de QC | NO IMPLEMENTADO | Las operaciones existen. Falta revisar composición y legibilidad. |
| PY-20 | Grafo de riser a partir de PDF y Excel | NO IMPLEMENTADO | `power riser` lee el XLSX con procedencia por celda, coteja evidencia textual de las aristas PDF indicadas, reconcilia con precedencia explícita y genera operaciones de dibujo. `riser_geometria` exige los símbolos esperados y detecta rótulos repetidos. `power riser_audit` relee las fuentes. Los resultados largos se recuperan con `res` en fragmentos. Falta reproducir el sector real y validar legibilidad a escala. |
| PY-21 | Orden, preset y configuración persistente del set | NO IMPLEMENTADO | `sheet_set` guarda el orden y el objeto opcional `preset_pdf`. `doc pdf` los relee, detecta cambios de miembros y comprueba viewports y títulos contra el papel de la lámina. Falta guardar, reabrir y comprobar el set y el PDF de 26 láminas en el laboratorio. |
| PY-22 | Verificación del PDF de entrega | NO IMPLEMENTADO | `power pdf verify` compara orden, textos visibles, sello extraíble, dimensiones, RGB vectorial y SHA-256 contra el manifiesto y el PDF anterior. La prueba sintética distingue revisiones. Falta cotejar apariencia, recorte y alimentadores en la emisión real del riser. |
| PY-23 | Guardado, sincronización y recuperación interrumpida | NO IMPLEMENTADO | Recibos y destino de plan fijado. Falta el escenario completo con confirmación perdida y reanudación. |
| PY-24 | Preparar Batch Copy con intervención humana | NO IMPLEMENTADO | Traspaso existente. Repetir sobre el fixture de 88 muebles. |
| PY-25 | Confirmar monitorización después de guardar | NO IMPLEMENTADO | Reproducir el resultado de los 88 muebles con el candidato. |
| PY-26 | Editar RFA, recargar y comprobar conectores | NO IMPLEMENTADO | Probar las operaciones existentes sobre la copia de la RFA. |
| PY-27 | Drafting View, marco, nota y viewport | IMPLEMENTADO | Se añadió formato de nota completa en negrita y subrayado al flujo tipado. Pendiente la comprobación visual. |
| PY-28 | Índices, filtros de tablas y cantidades | IMPLEMENTADO | `check tabla_audit` compara IDs, cantidad, filtros y texto de celdas. `ScheduleAuditTests` verifica coincidencias, diferencias, índices inválidos, población no evaluable y el comando compacto. La lectura de la API de Revit queda para la validación nativa. |

**Revit 2027: pendiente de comenzar.** El alcance funcional continúa abierto.

La comprobación local compiló el add-in para Revit 2022–2026 y el proyecto del instalador. Pasaron 808 pruebas .NET en la suite sin benchmarks, y después pasaron las 30 pruebas del instalador y de actualización ZIP con el caso de cancelación añadido. Rust pasó 13 pruebas y Python 32, con dos omitidas. La aceptación en Revit sigue aplazada.

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

Cuando `power` devuelve `formato_res=json_fragmentos`, ejecute la orden que indica `lectura` y siga el cursor hasta la última página. Concatene sólo las líneas de datos, sin saltos añadidos, para recuperar el JSON original.

## Evidencia histórica offline BM-01

Se comparó materializar y agrupar frente a acumular por grupo, con las mismas filas sintéticas. Se usaron 30 repeticiones por variante y tamaño tras el calentamiento. Se comprobó la igualdad del resultado serializado antes de cronometrar.

| Filas | Mediana materializado → incremental | p95 materializado → incremental | KB asignados materializado → incremental |
|---|---:|---:|---:|
| 1.000 | 0,48 → 0,35 ms | 0,94 → 0,48 ms | 475 → 458 |
| 10.000 | 5,62 → 4,01 ms | 9,07 → 4,78 ms | 4.635 → 4.378 |
| 50.000 | 26,48 → 9,60 ms | 27,82 → 11,03 ms | 22.825 → 21.800 |

El ensayo mide proyección y agregación fuera de Revit, sin transporte. Las dos variantes se ejecutaron en bloques consecutivos. La campaña de benchmarks queda para más adelante.

Comandos de validación: `dotnet test tests/PDI.RevitMcp.Tests -c Release --filter …`, `dotnet run -c Release --project tools/bench/WorkflowBench -- <informe.md> <informe.json> 7` y `./build.ps1 -Python <CPython>`. CI remoto y minutos de Actions usados en esta rama: cero.
