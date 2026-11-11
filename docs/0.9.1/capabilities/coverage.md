# Cobertura funcional de Revit — registro generado

Generado por `tools/inventory/RevitApiInventory registry` desde `src/PDI.RevitMcp.Engine/Coverage/CapabilityCatalog.cs` y el inventario offline de la API (`docs/0.9.1/inventory`). No editar a mano.

Estados: ninguna capacidad es `NATIVE_VERIFIED` mientras no exista la campaña nativa única de 0.9.1. `IMPLEMENTED_UNVERIFIED` = implementación tipada con pruebas de planificación offline; su efecto en Revit está pendiente de campaña.

## Capacidades por estado

- `OFFLINE_VERIFIED`: 8
- `IMPLEMENTED_UNVERIFIED`: 60
- `DYNAMIC_CSHARP_ONLY`: 40
- `MISSING`: 9
- `UNKNOWN`: 1
- `UNSUPPORTED_BY_PUBLIC_API`: 2

## Comandos de la cinta (PostableCommand, unión 2022–2026)

- Total distintos: 596
- Con capacidad enlazada: 107
- Sin analizar (`UNKNOWN`): 490
- Clasificados por regla de dominio: 531; sin regla (F37 por defecto): 65

## Por dominio

| Dominio | Capacidades | Estados | Comandos de la cinta |
|---|---|---|---|
| F01 | 7 | IMPLEMENTED_UNVERIFIED 5, DYNAMIC_CSHARP_ONLY 1, MISSING 1 | 15 |
| F02 | 10 | OFFLINE_VERIFIED 1, IMPLEMENTED_UNVERIFIED 6, DYNAMIC_CSHARP_ONLY 3 | 37 |
| F03 | 6 | IMPLEMENTED_UNVERIFIED 3, DYNAMIC_CSHARP_ONLY 2, UNKNOWN 1 | 19 |
| F04 | 8 | IMPLEMENTED_UNVERIFIED 5, DYNAMIC_CSHARP_ONLY 3 | 10 |
| F05 | 3 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 1, MISSING 1 | 11 |
| F06 | 3 | IMPLEMENTED_UNVERIFIED 2, DYNAMIC_CSHARP_ONLY 1 | 12 |
| F07 | 3 | IMPLEMENTED_UNVERIFIED 2, DYNAMIC_CSHARP_ONLY 1 | 13 |
| F08 | 3 | DYNAMIC_CSHARP_ONLY 3 | 10 |
| F09 | 3 | IMPLEMENTED_UNVERIFIED 2, MISSING 1 | 15 |
| F10 | 1 | MISSING 1 | 16 |
| F11 | 1 | DYNAMIC_CSHARP_ONLY 1 | 15 |
| F12 | 1 | IMPLEMENTED_UNVERIFIED 1 | 15 |
| F13 | 1 | DYNAMIC_CSHARP_ONLY 1 | 13 |
| F14 | 1 | DYNAMIC_CSHARP_ONLY 1 | 18 |
| F15 | 1 | DYNAMIC_CSHARP_ONLY 1 | 16 |
| F16 | 2 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 1 | 2 |
| F17 | 2 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 1 | 23 |
| F18 | 2 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 1 | 12 |
| F19 | 1 | MISSING 1 | 1 |
| F20 | 6 | IMPLEMENTED_UNVERIFIED 3, DYNAMIC_CSHARP_ONLY 3 | 24 |
| F21 | 1 | MISSING 1 | 9 |
| F22 | 1 | DYNAMIC_CSHARP_ONLY 1 | 3 |
| F23 | 3 | IMPLEMENTED_UNVERIFIED 3 | 24 |
| F24 | 9 | IMPLEMENTED_UNVERIFIED 8, DYNAMIC_CSHARP_ONLY 1 | 45 |
| F25 | 8 | IMPLEMENTED_UNVERIFIED 7, DYNAMIC_CSHARP_ONLY 1 | 41 |
| F26 | 2 | IMPLEMENTED_UNVERIFIED 1, MISSING 1 | 8 |
| F27 | 5 | IMPLEMENTED_UNVERIFIED 3, DYNAMIC_CSHARP_ONLY 2 | 5 |
| F28 | 2 | DYNAMIC_CSHARP_ONLY 2 | 25 |
| F29 | 2 | UNSUPPORTED_BY_PUBLIC_API 2 | 4 |
| F30 | 2 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 1 | 14 |
| F31 | 4 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 3 | 15 |
| F32 | 2 | IMPLEMENTED_UNVERIFIED 1, MISSING 1 | 11 |
| F33 | 1 | DYNAMIC_CSHARP_ONLY 1 | 6 |
| F34 | 3 | DYNAMIC_CSHARP_ONLY 3 | 20 |
| F35 | 6 | OFFLINE_VERIFIED 5, IMPLEMENTED_UNVERIFIED 1 | 4 |
| F36 | 2 | OFFLINE_VERIFIED 1, IMPLEMENTED_UNVERIFIED 1 | 0 |
| F37 | 2 | OFFLINE_VERIFIED 1, MISSING 1 | 65 |

## Capacidades

| ID | Capacidad | Estado | Estrategia | Limitaciones |
|---|---|---|---|---|
| F01-DOC-CLOSE-001 | Cerrar documento | `IMPLEMENTED_UNVERIFIED` | doc:cerrar |  |
| F01-DOC-OPEN-001 | Abrir proyecto o familia desde ruta | `DYNAMIC_CSHARP_ONLY` | exec | abrir modelos exige autorización explícita del modelo; no se automatiza sobre proyectos del usuario |
| F01-DOC-PDF-001 | Exportar láminas a PDF | `IMPLEMENTED_UNVERIFIED` | doc:pdf |  |
| F01-DOC-PURGE-001 | Purgar elementos no usados | `MISSING` | none | sin implementación en BIMBRIDGE |
| F01-DOC-READ-001 | Listar documentos abiertos con identidad estable | `IMPLEMENTED_UNVERIFIED` | query:ctx |  |
| F01-DOC-SAVE-001 | Guardar y guardar como | `IMPLEMENTED_UNVERIFIED` | doc:guardar |  |
| F01-DOC-SYNC-001 | Sincronizar con el central | `IMPLEMENTED_UNVERIFIED` | doc:sync |  |
| F02-SEL-SETS-001 | Conjuntos de ids con nombre ligados a documento | `OFFLINE_VERIFIED` | engine |  |
| F02-SEL-SHOW-001 | Seleccionar y encuadrar elementos para el usuario | `IMPLEMENTED_UNVERIFIED` | query:show |  |
| F02-XFORM-ALIGN-001 | Alinear | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F02-XFORM-ARRAY-001 | Matriz lineal o radial | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F02-XFORM-COPY-001 | Copiar elementos | `IMPLEMENTED_UNVERIFIED` | typed:copy | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F02-XFORM-DELETE-001 | Eliminar elementos | `IMPLEMENTED_UNVERIFIED` | typed:delete | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F02-XFORM-MIRROR-001 | Reflejar elementos | `IMPLEMENTED_UNVERIFIED` | typed:mirror | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F02-XFORM-MOVE-001 | Mover elementos | `IMPLEMENTED_UNVERIFIED` | typed:move | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F02-XFORM-OFFSET-001 | Desfase | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F02-XFORM-ROTATE-001 | Rotar elementos | `IMPLEMENTED_UNVERIFIED` | typed:rotate | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F03-COORD-READ-001 | Leer coordenadas compartidas y puntos base | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F03-DATUM-GRID-001 | Crear rejilla recta | `IMPLEMENTED_UNVERIFIED` | typed:grid_create | sólo rejillas rectas; arcos y rejillas multisegmento siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F03-DATUM-LEVEL-001 | Crear nivel | `IMPLEMENTED_UNVERIFIED` | typed:level_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F03-DATUM-REFPLANE-001 | Crear plano de referencia | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F03-DATUM-SCOPEBOX-001 | Asignar caja de referencia a vistas | `IMPLEMENTED_UNVERIFIED` | typed:view_set | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F03-DATUM-SCOPEBOX-002 | Crear caja de referencia | `UNKNOWN` | none | no se ha identificado una API pública de creación de cajas de referencia; investigar antes de clasificar |
| F04-MAT-CREATE-001 | Crear o duplicar material | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-PARAM-GLOBAL-001 | Crear y asociar parámetro global | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-PARAM-READ-001 | Consultar parámetros (instancia y tipo) | `IMPLEMENTED_UNVERIFIED` | query:query |  |
| F04-PARAM-SET-001 | Fijar parámetro de instancia | `IMPLEMENTED_UNVERIFIED` | typed:set_param | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F04-PARAM-SHARED-001 | Añadir parámetro compartido a categorías | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-PARAM-TYPE-001 | Fijar parámetro de tipo | `IMPLEMENTED_UNVERIFIED` | typed:set_type_param | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F04-PARAM-VALUES-001 | Fijar valores distintos por elemento | `IMPLEMENTED_UNVERIFIED` | typed:set_values | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F04-TYPE-CHANGE-001 | Cambiar tipo de elementos | `IMPLEMENTED_UNVERIFIED` | typed:change_type | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F05-WALL-CREATE-001 | Crear muros por polilínea | `IMPLEMENTED_UNVERIFIED` | typed:wall_create | tramos rectos; sin uniones forzadas ni muros de perfil. Efecto en Revit pendiente de la campaña nativa |
| F05-WALL-JOIN-001 | Unir o separar geometría | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F05-WALL-SPLIT-001 | Dividir muro | `MISSING` | none | sin implementación en BIMBRIDGE |
| F06-CEIL-CREATE-001 | Crear techo por contorno | `IMPLEMENTED_UNVERIFIED` | typed:ceiling_create | contorno explícito (no detección automática de recinto). Efecto en Revit pendiente de la campaña nativa |
| F06-FLOOR-CREATE-001 | Crear suelo por contorno | `IMPLEMENTED_UNVERIFIED` | typed:floor_create | un contorno sin huecos ni pendiente. Efecto en Revit pendiente de la campaña nativa |
| F06-ROOF-CREATE-001 | Crear cubierta por huella o extrusión | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F07-COMP-PLACE-001 | Colocar componente (familia cargada) | `IMPLEMENTED_UNVERIFIED` | typed:place | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F07-COMP-SNAP-001 | Ajustar a superficie (montaje real) | `IMPLEMENTED_UNVERIFIED` | typed:snap_to_surface | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F07-DOOR-WIN-001 | Colocar puerta o ventana en muro | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F08-CURTAIN-001 | Editar rejilla de muro cortina | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F08-RAIL-CREATE-001 | Crear barandilla por trayectoria | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F08-STAIR-CREATE-001 | Crear escalera por componentes | `DYNAMIC_CSHARP_ONLY` | exec | edición de escaleras exige StairsEditScope; sin verificador tipado |
| F09-AREA-PLAN-001 | Crear plano de áreas y esquema | `MISSING` | none | sin implementación en BIMBRIDGE |
| F09-ROOM-CREATE-001 | Crear habitación en circuito cerrado | `IMPLEMENTED_UNVERIFIED` | typed:room_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F09-ROOM-READ-001 | Consultar habitaciones, áreas y espacios | `IMPLEMENTED_UNVERIFIED` | query:query |  |
| F10-MASS-001 | Crear masa conceptual in situ | `MISSING` | none | modelado conceptual depende del editor de masas; sin operación tipada |
| F11-TOPO-001 | Crear toposólido (2024+) o superficie topográfica (≤2023) | `DYNAMIC_CSHARP_ONLY` | exec | Toposolid existe desde 2024 según el inventario; 2022–2023 usan TopographySurface |
| F12-STR-PLACE-001 | Colocar pilar o viga estructural (familia cargada) | `IMPLEMENTED_UNVERIFIED` | typed:place | colocación genérica por place; sin parámetros estructurales específicos ni verificación de uniones |
| F13-STEEL-001 | Conexiones y elementos de acero estructural | `DYNAMIC_CSHARP_ONLY` | exec | conexiones dependen de los tipos de conexión instalados; sin verificador tipado |
| F14-REBAR-001 | Crear armadura desde curvas | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F15-ANALYTICAL-001 | Leer o crear elementos analíticos (2023+) | `DYNAMIC_CSHARP_ONLY` | exec | AnalyticalModel (≤2022) se sustituye por AnalyticalElement en 2023 según el inventario |
| F16-CONN-CONNECT-001 | Conectar elementos MEP por conectores | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F16-CONN-FAM-001 | Crear o adoptar conector en familia | `IMPLEMENTED_UNVERIFIED` | fam:conector |  |
| F17-DUCT-CREATE-001 | Crear conducto entre puntos | `IMPLEMENTED_UNVERIFIED` | typed:duct_create | entre puntos; la conexión a conectores y los accesorios siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F17-SYS-CREATE-001 | Crear sistema mecánico | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F18-PIPE-CREATE-001 | Crear tubería entre puntos | `IMPLEMENTED_UNVERIFIED` | typed:pipe_create | entre puntos; la conexión a conectores y los accesorios siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F18-SYS-CREATE-001 | Crear sistema de tuberías | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F19-SPRINKLER-001 | Colocar y conectar rociadores | `MISSING` | none | sin implementación en BIMBRIDGE |
| F20-CIRCUIT-CREATE-001 | Crear circuito eléctrico de fuerza | `IMPLEMENTED_UNVERIFIED` | typed:circuit_create | verifica miembros y cuadro; la compatibilidad de tensión y polos la decide Revit (si la rechaza, el plan se revierte). Efecto en Revit pendiente de la campaña nativa |
| F20-CIRCUIT-PANEL-001 | Asignar panel al crear el circuito | `IMPLEMENTED_UNVERIFIED` | typed:circuit_create | sólo al crear; reasignar el cuadro de un circuito existente sigue por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F20-CONDUIT-001 | Crear conduit o bandeja | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F20-LIGHT-QA-001 | QA de luminarias y etiquetas | `IMPLEMENTED_UNVERIFIED` | query:check |  |
| F20-PANELSCHED-001 | Crear tabla de cuadro eléctrico | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F20-WIRE-CREATE-001 | Crear cableado | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F21-LOWVOLT-001 | Dispositivos y circuitos de datos, alarma y seguridad | `MISSING` | none | sin implementación en BIMBRIDGE |
| F22-FAB-001 | Colocar o leer partes de fabricación MEP | `DYNAMIC_CSHARP_ONLY` | exec | depende de la configuración de fabricación cargada |
| F23-FAM-INFO-001 | Información de familia y caras | `IMPLEMENTED_UNVERIFIED` | fam:info |  |
| F23-FAM-LOAD-001 | Cargar familia en proyecto desde carpeta autorizada | `IMPLEMENTED_UNVERIFIED` | fam:cargar |  |
| F23-FAM-PARAM-001 | Crear o editar parámetro de familia | `IMPLEMENTED_UNVERIFIED` | fam:param |  |
| F24-VIEW-3D-001 | Crear vista 3D isométrica con caja de sección | `IMPLEMENTED_UNVERIFIED` | typed:view_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-CAPTURE-001 | Capturar imagen de vista con resaltado temporal | `IMPLEMENTED_UNVERIFIED` | query:capture |  |
| F24-VIEW-CREATE-001 | Crear vista (planta, RCP, sección, callout) | `IMPLEMENTED_UNVERIFIED` | typed:view_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-DRAFT-001 | Crear vista de dibujo | `IMPLEMENTED_UNVERIFIED` | typed:view_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-DUP-001 | Duplicar vista (simple, con detalles o dependiente) | `IMPLEMENTED_UNVERIFIED` | typed:view_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-ELEV-001 | Crear alzado (marcador y vista) | `DYNAMIC_CSHARP_ONLY` | exec | view_create no admite alzados: sólo C# dinámico |
| F24-VIEW-HIDE-001 | Ocultar o mostrar elementos y categorías en vista | `IMPLEMENTED_UNVERIFIED` | typed:view_hide | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-OVERRIDE-001 | Sustituciones gráficas y filtros de vista | `IMPLEMENTED_UNVERIFIED` | typed:view_override | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-SET-001 | Configurar vista (rango, recorte, escala, plantilla) | `IMPLEMENTED_UNVERIFIED` | typed:view_set | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-DETAIL-001 | Líneas, arcos, polígonos y rectángulos de detalle | `IMPLEMENTED_UNVERIFIED` | typed:detail_line | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-DETAILCOMP-001 | Componente de detalle | `IMPLEMENTED_UNVERIFIED` | typed:detail_component | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-DIM-001 | Crear cota entre referencias | `IMPLEMENTED_UNVERIFIED` | typed:dimension | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-REGION-001 | Región rellena | `IMPLEMENTED_UNVERIFIED` | typed:filled_region | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-REVCLOUD-001 | Nube de revisión | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F25-TAG-001 | Etiquetar elementos | `IMPLEMENTED_UNVERIFIED` | typed:tag | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-TAG-MOVE-001 | Mover etiquetas | `IMPLEMENTED_UNVERIFIED` | typed:tag_move | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-TEXT-001 | Crear nota de texto | `IMPLEMENTED_UNVERIFIED` | typed:text_note | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F26-SCHED-CREATE-001 | Crear tabla de planificación con campos | `IMPLEMENTED_UNVERIFIED` | typed:schedule_create | campos en orden; filtros, orden y agrupación siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F26-SCHED-EXPORT-001 | Exportar tabla a CSV | `MISSING` | none | sin implementación en BIMBRIDGE |
| F27-REVISION-001 | Crear y asignar revisiones a láminas | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F27-SCHED-ONSHEET-001 | Colocar tabla en lámina | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F27-SHEET-CREATE-001 | Crear lámina con cajetín | `IMPLEMENTED_UNVERIFIED` | typed:sheet_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F27-SHEET-PLACE-001 | Colocar vista en lámina | `IMPLEMENTED_UNVERIFIED` | typed:sheet_place | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F27-SHEET-SET-001 | Editar parámetros de lámina | `IMPLEMENTED_UNVERIFIED` | typed:sheet_set | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F28-LINK-CAD-001 | Vincular o importar CAD | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F28-LINK-RVT-001 | Vincular modelo RVT con posicionamiento | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F29-CM-BATCH-001 | Copy/Monitor: copia por lotes con monitorización | `UNSUPPORTED_BY_PUBLIC_API` | postcommand | el inventario confirma que CopyMonitorOptions no existe en la API pública 2022–2026. PostCommand sólo abre la herramienta: no completa ni verifica el workflow (#23, #24). Requiere el motor de workflows nativos supervisados |
| F29-CR-001 | Coordination Review: revisar y resolver avisos de monitorización | `UNSUPPORTED_BY_PUBLIC_API` | postcommand | sin API pública para Coordination Review; sólo flujo de UI supervisado |
| F30-WS-CREATE-001 | Crear workset | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F30-WS-SET-001 | Mover elementos a otro workset | `IMPLEMENTED_UNVERIFIED` | typed:set_workset | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F31-ASSEMBLY-001 | Crear ensamblaje | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F31-GROUP-001 | Crear, colocar y desagrupar grupos | `IMPLEMENTED_UNVERIFIED` | typed:group_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F31-OPTION-001 | Opciones de diseño | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F31-PHASE-001 | Asignar fase de creación y derribo | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F32-ENERGY-001 | Modelo de análisis energético | `MISSING` | none | sin implementación en BIMBRIDGE |
| F32-GEO-CLASH-001 | Detección de interferencias y montaje real | `IMPLEMENTED_UNVERIFIED` | query:geo |  |
| F33-RENDER-IMG-001 | Exportar imagen de vista | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F34-EXPORT-DWG-001 | Exportar DWG | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F34-EXPORT-IFC-001 | Exportar IFC con opciones | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F34-EXPORT-NWC-001 | Exportar NWC | `DYNAMIC_CSHARP_ONLY` | exec | requiere el exportador de Navisworks instalado |
| F35-AGENTHOST-001 | Planes IR durables con recuperación entre procesos | `OFFLINE_VERIFIED` | engine | verificado contra el backend simulado; contra Revit, pendiente de campaña |
| F35-AUDIT-001 | Auditoría durable con group commit y recuperación | `OFFLINE_VERIFIED` | engine |  |
| F35-EXEC-001 | Ejecutar C# dinámico (consulta, ensayo, ejecutar) | `IMPLEMENTED_UNVERIFIED` | exec | no es un sandbox; efectos externos sin rollback |
| F35-IDENTITY-001 | Identidad nominal y correlación de solicitudes | `OFFLINE_VERIFIED` | engine |  |
| F35-QUERY-SNAP-001 | Lectura paginada desde una instantánea ligada a revisión | `OFFLINE_VERIFIED` | engine |  |
| F35-RECIPE-001 | Tareas por recetas con concesión de capacidades | `OFFLINE_VERIFIED` | engine |  |
| F36-QC-INDEP-001 | Verificación independiente contra fuente declarada | `OFFLINE_VERIFIED` | engine |  |
| F36-QC-MANIFEST-001 | QC uno a uno contra manifiesto | `IMPLEMENTED_UNVERIFIED` | exec |  |
| F37-NWE-001 | Motor de workflows nativos supervisados (UI asistida con recibos) | `MISSING` | none | PostCommand sólo encola un comando; no hay recibo ni verificación. Pendiente del motor de #23 |
| F37-PDF-PLAN-001 | Plan de colocación desde PDF (documento → plan) | `OFFLINE_VERIFIED` | engine | valida el documento y el plan; la ejecución en Revit queda pendiente de campaña |
