# Matriz de capacidades — BIMBRIDGE - Power Engine 0.9.0

Generada desde `src/PDI.RevitMcp.Engine/Capabilities.cs` (una prueba comprueba que coincide). Columna *evidencia*: lo que de verdad se comprobó. Lo nuevo de 0.6.0 todavía no se ha ejecutado dentro de Revit: está compilado para 2022–2026 y probado sin Revit; la prueba nativa es `tools/aceptacion/PROCEDIMIENTO_0.6.0.md`.

| Orden | Qué hace | Proyecto | Familia | Nivel | Desde | Evidencia | Notas |
|---|---|---|---|---|---|---|---|
| ctx / docs | contexto, documentos abiertos con handle dN, capacidades | sí | sí | R | 2022 | probado en Revit 2026 (lectura, 0.5.0, una muestra) | lista RVT y RFA, tipo, ruta, modificado, sólo lectura, activo |
| cat | catálogo (niveles, vistas, tipos, familias…) | sí | parcial | R | 2022 | probado en Revit 2024 (lectura, 0.2.x) | en familias sólo tipos/familias/vistas |
| q / get | consultas por categoría, nivel, tipo, conjunto | sí | sí | R | 2022 | probado en Revit 2024 (lectura, 0.2.x) | documento fijado en todos los tramos desde 0.5.0 |
| chk lighting_audit | QC de luminarias y etiquetas por vista | sí | no | R | 2022 | probado en Revit 2024 (lectura, 0.2.x) | perfil de etiquetas por tarea: tags=uno|ninguno|ignorar |
| geo | geometría: montaje, choques, rangos de vista, cuartos | sí | no | R | 2022 | probado en Revit 2024 (lectura, 0.2.x) | vínculos con transformación fallida se excluyen y se informan |
| show / cap | señalar y capturar vistas | sí | sí | R | 2022 | probado en Revit 2024 (lectura, 0.2.x) | sólo sobre el documento activo (necesita la interfaz) |
| chk model_health | salud del modelo: avisos agrupados, duplicados, parámetros requeridos vacíos, conectores MEP libres, láminas vacías y vistas sin lámina | sí | no | R | 2022 | compila 2022–2026 | sólo lectura, por tramos; no reproduce las comprobaciones estructurales de Revit |
| chk historial | historial de operaciones largas con filtros, progreso de las que siguen y consejo de reintento | sí | sí | R | 2022 | compila 2022–2026 | persistente en %LOCALAPPDATA%; nunca aconseja repetir lo incierto |
| chk cobertura | descubrimiento del catálogo F01–F37 bajo demanda: filas compactas paginadas y contrato completo por id | sí | sí | R | 2022 | compila 2022–2026 | índice inmutable en memoria; no entra al hilo de Revit ni lee ensamblados |
| chk copy_monitor | Copy/Monitor: preflight e inventario del vínculo, traspaso manual veraz y verificación posterior (sólo lectura) | sí | no | R | 2022 | compila 2022–2026 | no hay API pública de Copia por lotes: nunca declara 'copiado'; correspondencia origen→copia inferida por tipo y posición |
| plan / commit | 44 operaciones tipadas con diff y un Deshacer (0.8.0: dibujo 2D, cotas, componentes de detalle, conjunto de impresión; 0.9.1: niveles, rejillas, muros, suelos, techos, habitaciones, conductos, tuberías, circuitos y tablas) | sí | parcial | P/W | 2022 | compila 2022–2026 | en familias: set, values, move, rotate, mirror, copy, delete; las operaciones de modelado 0.9.1 sólo en proyectos |
| exec consulta | C# general sobre la API; todo se revierte al final | sí | sí | R | 2022 | compila + prueba sin Revit | compilador Roslyn 4.8; compilación probada contra la API de cada año sin Revit |
| exec ejecutar | C# general con transacción automática, grupo o sin transacción | sí | sí | W | 2022 | compila + prueba sin Revit | tx=auto|grupo|ninguna; avisos clasificados por FailureDefinitionId |
| fam info / caras | tipos, parámetros, fórmulas, GUID, conectores, caras planas | sí | sí | R | 2022 | probado en Revit 2026 (lectura, 0.5.0, una muestra) | en proyecto: por familia cargada (conectores de una instancia) |
| fam buscar / cargar_archivo | 0.8.0: índice de RFA sólo en carpetas_familias; cargar un RFA indexado y sin cambios (sha-256) | sí | no | R / W | 2022 | compila + prueba sin Revit | nunca explora discos; permiso cargar_archivo |
| fam abrir | EditFamily desde el proyecto → handle del RFA (reutiliza sólo si lo abrió este proyecto y esta familia) | sí | no | W | 2022 | compila 2022–2026 | 0.6.0: procedencia por proyecto + UniqueId de la familia |
| fam conector | crear o reconciliar conector por clave semántica y verificar | no | sí | W | 2022 | compila 2022–2026 | tubería, ducto, eléctrico, conduit, bandeja; radio o diámetro explícitos |
| fam param | valor, fórmula, crear y asociar parámetros de familia | no | sí | W | 2022 | compila 2022–2026 |  |
| fam cargar | cargar el RFA en el proyecto con IFamilyLoadOptions y revalidar | no | sí | W | 2022 | compila 2022–2026 | sobrescribir=valores|no |
| doc guardar / guardar_como | Save / SaveAs con verificación de archivo y hash | sí | sí | D o W | 2022 | compila 2022–2026 |  |
| doc sync | sincronizar con central y verificar | sí | no | D o W | 2022 | compila 2022–2026 | sólo modelos con worksharing |
| doc pdf | exportar hojas o un conjunto a PDF y verificar archivos | sí | no | D o W | 2022 | compila 2022–2026 | Document.Export(PDFExportOptions), existe desde 2022 |
| doc ifc | exportar IFC (2x3, 4 RV, 4 DTV; modelo o vista) y verificar el archivo | sí | no | D o W | 2022 | compila 2022–2026 | la transacción que Revit exige se revierte siempre: el modelo no cambia |
| doc tabla_csv | exportar una tabla de planificación a CSV y comparar filas | sí | no | D o W | 2022 | compila 2022–2026 | ViewSchedule.Export con coma y comillas dobles |
| doc cerrar | cerrar un documento que no es el activo | sí | sí | D o W | 2022 | compila 2022–2026 | Revit no permite cerrar el activo por la API |
| res | leer resultados largos guardados por handle | sí | sí | R | 2022 | compila + prueba sin Revit | 7 días en disco |
| clave= / tarea= | idempotencia y diario de operaciones durable | sí | sí | — | 2022 | compila + prueba sin Revit | commit, exec ejecutar, fam y doc |
| tarea iniciar / reanudar | tareas largas por unidades, reanudables, con barreras guardar/sync/pdf (0.6.0) | sí | sí | W | 2022 | compila + prueba con Revit simulado (motor/transporte) | motor probado con fallos inyectados; backend Revit sólo compilado |
| tarea estado / resumen / esperar | memoria de tarea entre clientes: resumen compacto, unidades, decisiones | sí | sí | R | 2022 | compila + prueba con Revit simulado (motor/transporte) | ambos perfiles (compacto y completo) |
| tarea reconciliar / cancelar | decisión explícita sobre unidades no verificables; cancelación entre unidades | sí | sí | W | 2022 | compila + prueba con Revit simulado (motor/transporte) |  |
| recetas incluidas | etiquetar_luces, parametro_por_lote, qc_manifiesto, montaje_soportes, exportar_laminas | sí | no | R/W | 2022 | compila 2022–2026 | compiladas contra la API de cada año por una prueba |
| Bim.* (scripts) | grupos, montaje con soporte real (rayos, vínculos), rango de vista, por qué no se ve, manifiesto | sí | parcial | R/W | 2022 | compila 2022–2026 | la lógica de QC (manifiesto, transformaciones, visibilidad) sí tiene pruebas sin Revit |
| metricas | caracteres por llamada y cliente para medir consumo (no tokens) | sí | sí | — | 2022 | compila + prueba sin Revit | local al puente |
| permisos | 0.8.0: 10 clases (leer, modificar proyecto/familia, cargar archivo, guardar, sincronizar, exportar, publicar, código dinámico, interfaz) | sí | sí | — | 2022 | compila + prueba sin Revit | config permisos; vacío = derivados de los niveles; publicar siempre denegado |
| Actualizar desde ZIP | 0.8.0: manifiesto, firma RSA, hashes, compatibilidad; se aplica al cerrar Revit, con reversión | sí | sí | — | 2022 | compila + prueba sin Revit | sólo desde la cinta; nunca cierra Revit |
| Setup /diagnostico | versión instalada, cargada en cada Revit, puente, hashes y reinicio pendiente | sí | sí | — | 2022 | compila + prueba sin Revit | el instalador verifica manifest.json antes y después de copiar |

## Por versión

| Revit | Runtime del add-in | Compilador del runner | API de referencia usada al compilar |
|---|---|---|---|
| 2022 | .NET Framework 4.8 | Roslyn 4.8 (en carpeta roslyn) | Nice3point 2022.1.80 |
| 2023 | .NET Framework 4.8 | Roslyn 4.8 (en carpeta roslyn) | Nice3point 2023.1.90 |
| 2024 | .NET Framework 4.8 | Roslyn 4.8 (en carpeta roslyn) | Nice3point 2024.3.60 |
| 2025 | .NET 8 | Roslyn 4.8 (contexto de carga propio) | Nice3point 2025.4.60 |
| 2026 | .NET 8 | Roslyn 4.8 (contexto de carga propio) | Nice3point 2026.4.10 |

El build mínimo real de cada año no está comprobado: compilar contra la API de referencia no prueba que un método exista en un build anterior. ctx informa el build exacto y el runner informa el error concreto (MissingMethodException) si un método no existe.
