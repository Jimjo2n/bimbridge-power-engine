# Self-QC de paridad con pyRevit y handoff posterior al PR #30

Base: `8ce066f8857595c4cf643ece2b3d08a1c1a10f21`. Rama local: `feat/daily-workflow-parity`.

Cada comprobación tiene uno de dos estados. `IMPLEMENTADO` indica que el criterio de la fila está cubierto con evidencia. `NO IMPLEMENTADO` indica que falta código o una comprobación exigida. La última columna identifica cuál falta. Las pruebas nativas se harán cuando el usuario abra su instancia de laboratorio. Los benchmarks quedan para una fase posterior y están fuera de esta lista funcional. El cierre requiere todas las filas en `IMPLEMENTADO`. Revit 2027 empieza después de ese cierre.

## Handoff

| Requisito | Estado | Evidencia o pendiente |
|---|---|---|
| Choques: conservar resultado de booleana, ausencia de sólidos y errores de extracción, transformación y cálculo | IMPLEMENTADO | `0b207a5`. Se mantienen índice, rayos, tolerancia y tramos. |
| Choques: una fila, clasificación conservadora, volumen parcial, cobertura y muestras por instancia de vínculo | IMPLEMENTADO | `GeoComputeTests`. El resumen paginado expone cobertura como metadato opcional. Los casos sintéticos RVT quedan para la campaña nativa. |
| Correlación y una transición de respuesta/timeout en el puente | IMPLEMENTADO | `c9dc513`. Identificador opcional fuera de los argumentos. Se informa si llegó a completarse el envío de la trama. |
| Recibos de escritura con clave, SourceBinding, auditoría de commit directo y cola | NO IMPLEMENTADO | Consulta de intento por sesión o agente y huella del documento. Falta una matriz de desconexión real de pipe, reconexión con binarios cruzados, repeticiones y cierres de cola. |
| Protocolo 2 y negociación de capacidades | NO IMPLEMENTADO | Se añadieron campos y capacidades opcionales. Faltan las tres combinaciones de binarios como prueba del entregable. |
| Agregación incremental, proyección, revisión y destino | IMPLEMENTADO | Reutiliza filtros y normalización. Acumula por grupo y conserva la ruta de conjuntos guardados, conteo e instantáneas. |
| Lectura completa, presupuesto UTF-8 y detalle paginable | NO IMPLEMENTADO | Presupuesto de trama, preservación de estado/cursor/recibo y fragmentos recuperables por `res`. Falta comprobar funcionalmente que el cursor y `res` recuperan el contenido completo en orden, incluso con una fila que supera el presupuesto. |
| Descubrimiento por relevancia en español e inglés | IMPLEMENTADO | Reutiliza `check name=cobertura` y aplica filtros antes de ordenar. Predeterminado de tres resultados con relevancia. Caso de prueba por ID, nombres, alias y filtro. |
| Piloto PDF: transportar requisitos de anfitrión y expectativas por elemento | IMPLEMENTADO | Lotes por contrato y parámetros; identidad de ítem opcional. El piloto admite familias por nivel y rechaza requisitos de anfitrión físico en la planificación. |
| Piloto PDF: releer antes de consolidar, asociar entradas e IDs, revertir diferencias | IMPLEMENTADO | Verificación compartida de aplicación y ensayo antes de `Assimilate`. Conserva verificación independiente separada. Equiparación entre lo planificado, lo creado y el resultado nativo se debe ejecutar en la campaña. |
| Espera del instalador, reapertura, cierre manual y convivencia de procesos | NO IMPLEMENTADO | La prueba requiere la instancia autorizada. |

## Plan de trabajo diario

| Ficha | Función o modificación | Estado | Qué falta |
|---|---|---|---|
| PY-01 | Fijar sesión, instancia y documento | NO IMPLEMENTADO | Repetir con dos modelos homónimos y corte de conexión. |
| PY-02 | Consultar y guardar un conjunto filtrado | IMPLEMENTADO | La ruta existente se conserva. El caso de relectura tras edición se prueba en Revit. |
| PY-03 | Inventario de 33 vistas y siete campos | NO IMPLEMENTADO | Repetir el control histórico con la compilación candidata. |
| PY-04 | Referencias geométricas de vínculos | NO IMPLEMENTADO | Id por instancia del vínculo en choques. Falta comprobar la cara transformada en el caso de dos instancias. |
| PY-05 | Inventario PDF con notas, leyendas, inclusión y exclusión | NO IMPLEMENTADO | Completar trazabilidad por símbolo y validar excepciones reales. |
| PY-06 | Resolución de familias y sustitución dentro de grupos | NO IMPLEMENTADO | Añadir reglas y verificar montaje y orientación por grupo. |
| PY-07 | Colocación idempotente | NO IMPLEMENTADO | Piloto con expectativas verificadas. Falta probar reintento con preexistencias en el cuadrante. |
| PY-08 | Medición y ajuste en losa, rampa y cielo | NO IMPLEMENTADO | Se usa el ajuste existente y se verifica el nivel y posición del piloto. Falta el caso de los tres montajes y la etiqueta. |
| PY-09 | Mantenimiento de llamadas estáticas de luminarias | NO IMPLEMENTADO | Asociación durable, detección y regeneración selectiva. |
| PY-10 | QC independiente de inventario, vistas y tags | NO IMPLEMENTADO | Unificar los tres conteos y demostrar distinción entre duplicado y visibilidad en dos vistas. |
| PY-11 | Reglas de clasificación de tableros y desconectivos | NO IMPLEMENTADO | Resolver y probar las excepciones de unidades y suites. |
| PY-12 | Propagación de grupos y correspondencia de IDs | NO IMPLEMENTADO | Variantes por nivel, reconciliación y reintento de un grupo reconstruido. |
| PY-13 | Terminales desde markups | NO IMPLEMENTADO | Transformar markup, verificar UP/DN y distinguir conexión MEP de llegada geométrica. |
| PY-14 | Conduit, fittings y reencaminamiento tipado | NO IMPLEMENTADO | Creación de segmento y de recorrido nuevo con codos, extremos libres y verificación previa a consolidar. Falta reemplazar recorridos existentes, enlazar terminales y preservar elementos ajenos. |
| PY-15 | Cambios por lote y presentación de sistemas | NO IMPLEMENTADO | Verificar el estado efectivo simultáneo de las dos vistas. |
| PY-16 | Reparar referencias de cotas, etiquetas y líderes | NO IMPLEMENTADO | Correspondencia anterior→nuevo y reparación de referencias en las vistas DIM. |
| PY-17 | QC integral por ruta | NO IMPLEMENTADO | Mayor cobertura de choques y continuidad del recorrido nuevo. Falta informe por ruta con contención en losa, stubs externos y validación del falso positivo. |
| PY-18 | Diagnóstico de plantillas, filtros, vínculos y regiones | NO IMPLEMENTADO | Completar la visibilidad efectiva del caso Power DN. |
| PY-19 | Callouts, cortes, 3D y láminas de QC | NO IMPLEMENTADO | Las operaciones existen. Falta revisar composición y legibilidad. |
| PY-20 | Grafo de riser a partir de PDF y Excel | NO IMPLEMENTADO | Reconciliación de fuentes, construcción y auditoría independiente. |
| PY-21 | Orden, preset y configuración persistente del set | NO IMPLEMENTADO | La exportación admite el orden de una lista explícita y papel. Falta persistir orden y preset en el set y comprobar el área de papel. |
| PY-22 | Verificación del PDF de entrega | NO IMPLEMENTADO | Exportación con parámetros y recibo de archivos. Falta comprobar sello, color, contenido por página y revisión frente a un PDF anterior. |
| PY-23 | Guardado, sincronización y recuperación interrumpida | NO IMPLEMENTADO | Recibos y destino de plan fijado. Falta el escenario completo con confirmación perdida y reanudación. |
| PY-24 | Preparar Batch Copy con intervención humana | NO IMPLEMENTADO | Traspaso existente. Repetir sobre el fixture de 88 muebles. |
| PY-25 | Confirmar monitorización después de guardar | NO IMPLEMENTADO | Reproducir el resultado de los 88 muebles con el candidato. |
| PY-26 | Editar RFA, recargar y comprobar conectores | NO IMPLEMENTADO | Probar las operaciones existentes sobre la copia de la RFA. |
| PY-27 | Drafting View, marco, nota y viewport | IMPLEMENTADO | Se añadió formato de nota completa en negrita y subrayado al flujo tipado. Pendiente la comprobación visual. |
| PY-28 | Índices, filtros de tablas y cantidades | NO IMPLEMENTADO | Añadir un verificador de población, filtros y cantidades. |

**Revit 2027: pendiente de comenzar.** El alcance funcional continúa abierto.

## Evidencia histórica offline BM-01

Se comparó materializar y agrupar frente a acumular por grupo, con las mismas filas sintéticas. Se usaron 30 repeticiones por variante y tamaño tras el calentamiento. Se comprobó la igualdad del resultado serializado antes de cronometrar.

| Filas | Mediana materializado → incremental | p95 materializado → incremental | KB asignados materializado → incremental |
|---|---:|---:|---:|
| 1.000 | 0,48 → 0,35 ms | 0,94 → 0,48 ms | 475 → 458 |
| 10.000 | 5,62 → 4,01 ms | 9,07 → 4,78 ms | 4.635 → 4.378 |
| 50.000 | 26,48 → 9,60 ms | 27,82 → 11,03 ms | 22.825 → 21.800 |

El ensayo mide proyección y agregación fuera de Revit, sin transporte. Las dos variantes se ejecutaron en bloques consecutivos. La campaña de benchmarks queda para más adelante.

Comandos de validación: `dotnet test tests/PDI.RevitMcp.Tests -c Release --filter …`, `dotnet run -c Release --project tools/bench/WorkflowBench -- <informe.md> <informe.json> 7` y `./build.ps1 -Python <CPython>`. CI remoto y minutos de Actions usados en esta rama: cero.
