# Cobertura funcional de Revit — registro generado

Generado por `tools/inventory/RevitApiInventory registry` desde `src/PDI.RevitMcp.Engine/Coverage/CapabilityCatalog.cs` y el inventario offline de la API (`docs/0.9.1/inventory`). No editar a mano.

Estados: ninguna capacidad es `NATIVE_VERIFIED` mientras no exista la campaña nativa única de 0.9.1. `IMPLEMENTED_UNVERIFIED` = implementación tipada con pruebas de planificación offline; su efecto en Revit está pendiente de campaña.

## Capacidades por estado

- `OFFLINE_VERIFIED`: 11
- `IMPLEMENTED_UNVERIFIED`: 72
- `DYNAMIC_CSHARP_ONLY`: 92
- `MISSING`: 7
- `UNKNOWN`: 32
- `UNSUPPORTED_BY_PUBLIC_API`: 2
- `NOT_APPLICABLE`: 5

## Comandos de la cinta (PostableCommand, unión 2022–2026)

- Total distintos: 596
- Con capacidad enlazada: 596
- Estado `UNKNOWN`: 189 (sin analizar: 0; analizados sin evidencia suficiente, con investigación pendiente registrada: 189)
- Por estado: `DYNAMIC_CSHARP_ONLY` 256, `IMPLEMENTED_UNVERIFIED` 91, `MISSING` 8, `NOT_APPLICABLE` 48, `UNKNOWN` 189, `UNSUPPORTED_BY_PUBLIC_API` 4
- Por ejecutor: `DIRECT_REVIT_API` 91, `DYNAMIC_CSHARP` 256, `MANUAL_HANDOFF` 4, `UNAVAILABLE` 245
- Un comando enlazado a una capacidad DYNAMIC_CSHARP_ONLY sólo tiene API pública identificada; no es automatización completa.
- Dominio: el de la capacidad enlazada (596); la regla por nombre (`domain_by_name`) sólo es referencia y difiere en 246 comandos.

## Por dominio

| Dominio | Capacidades | Estados | Comandos de la cinta |
|---|---|---|---|
| F01 | 12 | IMPLEMENTED_UNVERIFIED 5, DYNAMIC_CSHARP_ONLY 3, MISSING 1, UNKNOWN 2, NOT_APPLICABLE 1 | 25 |
| F02 | 19 | OFFLINE_VERIFIED 1, IMPLEMENTED_UNVERIFIED 8, DYNAMIC_CSHARP_ONLY 7, UNKNOWN 2, NOT_APPLICABLE 1 | 40 |
| F03 | 8 | IMPLEMENTED_UNVERIFIED 3, DYNAMIC_CSHARP_ONLY 4, UNKNOWN 1 | 18 |
| F04 | 13 | IMPLEMENTED_UNVERIFIED 5, DYNAMIC_CSHARP_ONLY 7, UNKNOWN 1 | 17 |
| F05 | 6 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 3, MISSING 1, UNKNOWN 1 | 11 |
| F06 | 5 | IMPLEMENTED_UNVERIFIED 2, DYNAMIC_CSHARP_ONLY 2, UNKNOWN 1 | 17 |
| F07 | 6 | IMPLEMENTED_UNVERIFIED 2, DYNAMIC_CSHARP_ONLY 3, UNKNOWN 1 | 11 |
| F08 | 5 | DYNAMIC_CSHARP_ONLY 4, UNKNOWN 1 | 9 |
| F09 | 8 | IMPLEMENTED_UNVERIFIED 2, DYNAMIC_CSHARP_ONLY 5, MISSING 1 | 16 |
| F10 | 2 | MISSING 1, UNKNOWN 1 | 12 |
| F11 | 3 | DYNAMIC_CSHARP_ONLY 2, UNKNOWN 1 | 16 |
| F12 | 3 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 1, UNKNOWN 1 | 15 |
| F13 | 3 | DYNAMIC_CSHARP_ONLY 2, UNKNOWN 1 | 19 |
| F14 | 2 | DYNAMIC_CSHARP_ONLY 2 | 20 |
| F15 | 3 | DYNAMIC_CSHARP_ONLY 2, UNKNOWN 1 | 17 |
| F16 | 3 | IMPLEMENTED_UNVERIFIED 2, DYNAMIC_CSHARP_ONLY 1 | 13 |
| F17 | 7 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 4, UNKNOWN 2 | 32 |
| F18 | 3 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 2 | 2 |
| F19 | 1 | MISSING 1 | 1 |
| F20 | 8 | IMPLEMENTED_UNVERIFIED 3, DYNAMIC_CSHARP_ONLY 4, UNKNOWN 1 | 15 |
| F21 | 1 | MISSING 1 | 2 |
| F22 | 3 | DYNAMIC_CSHARP_ONLY 2, UNKNOWN 1 | 5 |
| F23 | 7 | IMPLEMENTED_UNVERIFIED 3, DYNAMIC_CSHARP_ONLY 3, UNKNOWN 1 | 24 |
| F24 | 14 | IMPLEMENTED_UNVERIFIED 8, DYNAMIC_CSHARP_ONLY 4, UNKNOWN 1, NOT_APPLICABLE 1 | 48 |
| F25 | 15 | IMPLEMENTED_UNVERIFIED 9, DYNAMIC_CSHARP_ONLY 2, UNKNOWN 4 | 44 |
| F26 | 4 | IMPLEMENTED_UNVERIFIED 2, DYNAMIC_CSHARP_ONLY 1, UNKNOWN 1 | 8 |
| F27 | 8 | IMPLEMENTED_UNVERIFIED 4, DYNAMIC_CSHARP_ONLY 4 | 10 |
| F28 | 6 | DYNAMIC_CSHARP_ONLY 5, UNKNOWN 1 | 26 |
| F29 | 3 | IMPLEMENTED_UNVERIFIED 1, UNSUPPORTED_BY_PUBLIC_API 2 | 5 |
| F30 | 4 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 2, UNKNOWN 1 | 8 |
| F31 | 8 | IMPLEMENTED_UNVERIFIED 3, DYNAMIC_CSHARP_ONLY 4, UNKNOWN 1 | 10 |
| F32 | 4 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 2, MISSING 1 | 13 |
| F33 | 2 | DYNAMIC_CSHARP_ONLY 1, UNKNOWN 1 | 6 |
| F34 | 6 | IMPLEMENTED_UNVERIFIED 1, DYNAMIC_CSHARP_ONLY 4, UNKNOWN 1 | 20 |
| F35 | 7 | OFFLINE_VERIFIED 5, IMPLEMENTED_UNVERIFIED 1, NOT_APPLICABLE 1 | 9 |
| F36 | 3 | OFFLINE_VERIFIED 1, IMPLEMENTED_UNVERIFIED 2 | 7 |
| F37 | 6 | OFFLINE_VERIFIED 4, UNKNOWN 1, NOT_APPLICABLE 1 | 25 |

## Capacidades

| ID | Capacidad | Estado | Estrategia | Limitaciones |
|---|---|---|---|---|
| F01-DOC-CLOSE-001 | Cerrar documento | `IMPLEMENTED_UNVERIFIED` | doc:cerrar |  |
| F01-DOC-CLOUD-001 | Guardar, abrir y gestionar modelos en la nube | `DYNAMIC_CSHARP_ONLY` | exec | requiere sesión de Autodesk y permisos del proyecto en la nube |
| F01-DOC-OPEN-001 | Abrir proyecto o familia desde ruta | `DYNAMIC_CSHARP_ONLY` | exec | abrir modelos exige autorización explícita del modelo; no se automatiza sobre proyectos del usuario |
| F01-DOC-PDF-001 | Exportar láminas a PDF | `IMPLEMENTED_UNVERIFIED` | doc:pdf |  |
| F01-DOC-PURGE-001 | Purgar elementos no usados | `MISSING` | none | sin implementación en BIMBRIDGE |
| F01-DOC-READ-001 | Listar documentos abiertos con identidad estable | `IMPLEMENTED_UNVERIFIED` | query:ctx |  |
| F01-DOC-REPAIR-001 | Reparar modelo central y restaurar copia de seguridad | `UNKNOWN` | none | investigación pendiente: sin API pública identificada para ambos diálogos; requiere flujo manual |
| F01-DOC-SAVE-001 | Guardar y guardar como | `IMPLEMENTED_UNVERIFIED` | doc:guardar |  |
| F01-DOC-STANDARDS-001 | Transferir normas de proyecto | `UNKNOWN` | none | investigación pendiente: no se ha localizado una API pública equivalente al diálogo; candidato: ElementTransformUtils.CopyElements entre documentos para tipos (no equivale a todas las categorías del diálogo) |
| F01-DOC-SYNC-001 | Sincronizar con el central | `IMPLEMENTED_UNVERIFIED` | doc:sync |  |
| F01-DOC-TEMPLATE-001 | Guardar como plantilla y crear proyecto desde plantilla | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F01-UI-SESSION-001 | Salir de Revit, opciones de la aplicación y archivos de ejemplo | `NOT_APPLICABLE` | none | acciones de la aplicación o de la sesión del usuario; no son operaciones sobre el modelo (cerrar un documento ya está en F01-DOC-CLOSE-001) |
| F02-EDIT-CUT-001 | Cortar y unir geometría, cambiar orden de unión | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F02-EDIT-MATCH-001 | Igualar propiedades de tipo | `IMPLEMENTED_UNVERIFIED` | typed:change_type | change_type necesita el tipo explícito; el «pincel» de la cinta lo toma de un elemento elegido. Efecto en Revit pendiente de la campaña nativa |
| F02-EDIT-PASTE-001 | Copiar al portapapeles y pegar alineado (vistas, niveles, mismo lugar) | `DYNAMIC_CSHARP_ONLY` | exec | el portapapeles de Revit no tiene API: el equivalente es CopyElements con transformación (copy tipado cubre traslado y nivel) |
| F02-EDIT-PIN-001 | Fijar y liberar elementos | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F02-EDIT-SCALE-001 | Escalar elementos | `UNKNOWN` | none | investigación pendiente: Revit sólo escala ciertos elementos 2D, imágenes y vínculos; sin método genérico en la API |
| F02-EDIT-SIMILAR-001 | Crear similar (colocar otro del mismo tipo) | `IMPLEMENTED_UNVERIFIED` | typed:place | place con el tipo del elemento de referencia; familias alojadas requieren anfitrión. Efecto en Revit pendiente de la campaña nativa |
| F02-EDIT-TRIM-001 | Recortar/extender y dividir con hueco | `UNKNOWN` | none | investigación pendiente: no hay método público de recorte; posible vía LocationCurve.Curve por categoría, a validar por tipo de elemento |
| F02-SEL-FILTER-001 | Guardar, cargar y editar selecciones guardadas | `DYNAMIC_CSHARP_ONLY` | exec | los conjuntos con nombre de BIMBRIDGE ($conjunto) viven en la sesión; esto persiste en el modelo |
| F02-SEL-SETS-001 | Conjuntos de ids con nombre ligados a documento | `OFFLINE_VERIFIED` | engine |  |
| F02-SEL-SHOW-001 | Seleccionar y encuadrar elementos para el usuario | `IMPLEMENTED_UNVERIFIED` | query:show |  |
| F02-UI-SELECT-001 | Seleccionar por id, ids de la selección y deshacer/rehacer | `NOT_APPLICABLE` | none | herramientas de la interfaz; BIMBRIDGE lee ids con query y deshace con un TransactionGroup por plan |
| F02-XFORM-ALIGN-001 | Alinear | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F02-XFORM-ARRAY-001 | Matriz lineal o radial | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F02-XFORM-COPY-001 | Copiar elementos | `IMPLEMENTED_UNVERIFIED` | typed:copy | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F02-XFORM-DELETE-001 | Eliminar elementos | `IMPLEMENTED_UNVERIFIED` | typed:delete | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F02-XFORM-MIRROR-001 | Reflejar elementos | `IMPLEMENTED_UNVERIFIED` | typed:mirror | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F02-XFORM-MOVE-001 | Mover elementos | `IMPLEMENTED_UNVERIFIED` | typed:move | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F02-XFORM-OFFSET-001 | Desfase | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F02-XFORM-ROTATE-001 | Rotar elementos | `IMPLEMENTED_UNVERIFIED` | typed:rotate | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F03-COORD-READ-001 | Leer coordenadas compartidas y puntos base | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F03-COORD-SHARED-001 | Coordenadas compartidas: informar, especificar, reubicar y girar norte | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F03-DATUM-GRID-001 | Crear rejilla recta | `IMPLEMENTED_UNVERIFIED` | typed:grid_create | sólo rejillas rectas; arcos y rejillas multisegmento siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F03-DATUM-LEVEL-001 | Crear nivel | `IMPLEMENTED_UNVERIFIED` | typed:level_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F03-DATUM-REFPLANE-001 | Crear plano de referencia | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F03-DATUM-SCOPEBOX-001 | Asignar caja de referencia a vistas | `IMPLEMENTED_UNVERIFIED` | typed:view_set | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F03-DATUM-SCOPEBOX-002 | Crear caja de referencia | `UNKNOWN` | none | no se ha identificado una API pública de creación de cajas de referencia; investigar antes de clasificar |
| F03-WORKPLANE-001 | Definir y mostrar plano de trabajo | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-KEYNOTE-001 | Configuración de notas clave | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-MAT-ASSET-001 | Recursos de apariencia y física de materiales | `UNKNOWN` | none | investigación pendiente: AppearanceAssetElement existe pero la edición de recursos tiene restricciones por versión; no verificado |
| F04-MAT-CREATE-001 | Crear o duplicar material | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-PARAM-GLOBAL-001 | Crear y asociar parámetro global | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-PARAM-PROJ-001 | Parámetros de proyecto y de categoría de familia | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-PARAM-READ-001 | Consultar parámetros (instancia y tipo) | `IMPLEMENTED_UNVERIFIED` | query:query |  |
| F04-PARAM-SET-001 | Fijar parámetro de instancia | `IMPLEMENTED_UNVERIFIED` | typed:set_param | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F04-PARAM-SHARED-001 | Añadir parámetro compartido a categorías | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-PARAM-TYPE-001 | Fijar parámetro de tipo | `IMPLEMENTED_UNVERIFIED` | typed:set_type_param | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F04-PARAM-VALUES-001 | Fijar valores distintos por elemento | `IMPLEMENTED_UNVERIFIED` | typed:set_values | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F04-PROJ-INFO-001 | Información y unidades del proyecto | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-STD-STYLES-001 | Patrones de línea y relleno, estilos y grosores de línea, estilos de objeto | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F04-TYPE-CHANGE-001 | Cambiar tipo de elementos | `IMPLEMENTED_UNVERIFIED` | typed:change_type | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F05-WALL-CREATE-001 | Crear muros por polilínea | `IMPLEMENTED_UNVERIFIED` | typed:wall_create | tramos rectos; sin uniones forzadas ni muros de perfil. Efecto en Revit pendiente de la campaña nativa |
| F05-WALL-JOIN-001 | Unir o separar geometría | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F05-WALL-JOINS-001 | Uniones de muros (permitir/impedir en extremos) | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F05-WALL-OPEN-001 | Hueco en muro | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F05-WALL-SPLIT-001 | Dividir muro | `MISSING` | none | sin implementación en BIMBRIDGE |
| F05-WALL-SWEEP-001 | Barridos y telares de muro, muro por cara | `UNKNOWN` | none | investigación pendiente: WallSweep.Create existe en algunas versiones; muro por cara depende de masas; sin verificar |
| F06-CEIL-CREATE-001 | Crear techo por contorno | `IMPLEMENTED_UNVERIFIED` | typed:ceiling_create | contorno explícito (no detección automática de recinto). Efecto en Revit pendiente de la campaña nativa |
| F06-EDGES-001 | Impostas, canalones, sofitos y bordes de losa; suelo y cubierta por cara | `UNKNOWN` | none | investigación pendiente: API de creación de estos barridos no confirmada en el listado; requiere investigación por versión |
| F06-FLOOR-CREATE-001 | Crear suelo por contorno | `IMPLEMENTED_UNVERIFIED` | typed:floor_create | un contorno sin huecos ni pendiente. Efecto en Revit pendiente de la campaña nativa |
| F06-OPENING-001 | Huecos verticales, de patinillo, buhardilla y por cara | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F06-ROOF-CREATE-001 | Crear cubierta por huella o extrusión | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F07-COMP-PLACE-001 | Colocar componente (familia cargada) | `IMPLEMENTED_UNVERIFIED` | typed:place | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F07-COMP-SNAP-001 | Ajustar a superficie (montaje real) | `IMPLEMENTED_UNVERIFIED` | typed:snap_to_surface | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F07-DOOR-WIN-001 | Colocar puerta o ventana en muro | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F07-INPLACE-001 | Modelar in situ y dibujar en cara/plano de trabajo | `UNKNOWN` | none | investigación pendiente: modelado in situ no tiene API pública de edición de familia in situ; colocación en cara/plano son opciones de place |
| F07-MODELLINE-001 | Líneas de modelo y simbólicas | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F07-MODELTEXT-001 | Texto de modelo 3D | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F08-CURTAIN-001 | Editar rejilla de muro cortina | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F08-CURTAIN-SYS-001 | Sistema cortina por cara y montantes | `UNKNOWN` | none | investigación pendiente: montantes vía CurtainGrid/Mullion; sistema por cara depende de masas; sin verificar |
| F08-RAIL-CREATE-001 | Crear barandilla por trayectoria | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F08-STAIR-CREATE-001 | Crear escalera por componentes | `DYNAMIC_CSHARP_ONLY` | exec | edición de escaleras exige StairsEditScope; sin verificador tipado |
| F08-STAIR-EDIT-001 | Recorrido, numeración y elementos sobre escaleras/rampas | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F09-AREA-001 | Áreas, contornos de área y cálculo de áreas/volúmenes | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F09-AREA-PLAN-001 | Crear plano de áreas y esquema | `MISSING` | none | sin implementación en BIMBRIDGE |
| F09-BOUNDARY-001 | Separadores de habitación y de espacio | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F09-COLORFILL-001 | Esquemas de color y leyendas de relleno | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F09-ROOM-CREATE-001 | Crear habitación en circuito cerrado | `IMPLEMENTED_UNVERIFIED` | typed:room_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F09-ROOM-READ-001 | Consultar habitaciones, áreas y espacios | `IMPLEMENTED_UNVERIFIED` | query:query |  |
| F09-SPACE-001 | Espacios MEP y su nomenclatura | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F09-TAG-001 | Etiquetas de habitación, área y espacio | `DYNAMIC_CSHARP_ONLY` | exec | tag tipado usa IndependentTag y no etiqueta recintos |
| F10-MASS-001 | Crear masa conceptual in situ | `MISSING` | none | modelado conceptual depende del editor de masas; sin operación tipada |
| F10-MASS-002 | Masas conceptuales: crear, colocar, mostrar y suelos de masa | `UNKNOWN` | none | investigación pendiente: edición de formas conceptuales en familias de masa; algunas tienen API (FamilyItemFactory), otras sólo UI |
| F11-PAD-001 | Plataformas de construcción | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F11-SITE-002 | Subregiones, líneas de propiedad, contornos, región explanada y componentes de emplazamiento | `UNKNOWN` | none | investigación pendiente: superficies topográficas (2022–2023) y Toposolid (2024+) tienen APIs distintas; cada herramienta requiere su verificación |
| F11-TOPO-001 | Crear toposólido (2024+) o superficie topográfica (≤2023) | `DYNAMIC_CSHARP_ONLY` | exec | Toposolid existe desde 2024 según el inventario; 2022–2023 usan TopographySurface |
| F12-BEAMSYS-001 | Sistemas de vigas y cerchas | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F12-JOINS-001 | Uniones viga/pilar y anotaciones de viga | `UNKNOWN` | none | investigación pendiente: parte en API de StructuralFramingUtils; sin verificar por herramienta |
| F12-STR-PLACE-001 | Colocar pilar o viga estructural (familia cargada) | `IMPLEMENTED_UNVERIFIED` | typed:place | colocación genérica por place; sin parámetros estructurales específicos ni verificación de uniones |
| F13-STEEL-001 | Conexiones y elementos de acero estructural | `DYNAMIC_CSHARP_ONLY` | exec | conexiones dependen de los tipos de conexión instalados; sin verificador tipado |
| F13-STEEL-CONN-001 | Conexiones estructurales | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F13-STEEL-FAB-002 | Placas, pernos, soldaduras, anclajes, conectores, despuntes y cortes | `UNKNOWN` | none | investigación pendiente: API de Advance Steel (Autodesk.Advance.*) fuera de RevitAPI.dll; no inventariada aquí |
| F14-REBAR-001 | Crear armadura desde curvas | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F14-REBAR-SETS-001 | Armadura por área, camino, malla y acopladores; recubrimientos | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F15-ANALYTICAL-001 | Leer o crear elementos analíticos (2023+) | `DYNAMIC_CSHARP_ONLY` | exec | AnalyticalModel (≤2022) se sustituye por AnalyticalElement en 2023 según el inventario |
| F15-LOADS-001 | Casos, combinaciones y cargas estructurales; vínculos analíticos | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F15-SETTINGS-001 | Ajustes estructurales | `UNKNOWN` | none | investigación pendiente: clase de ajustes estructurales a localizar en el listado por versión |
| F16-CONN-CONNECT-001 | Conectar elementos MEP por conectores | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F16-CONN-FAM-001 | Crear o adoptar conector en familia | `IMPLEMENTED_UNVERIFIED` | fam:conector |  |
| F16-MEP-PLACE-001 | Colocar equipos, terminales, aparatos y dispositivos MEP (familia cargada) | `IMPLEMENTED_UNVERIFIED` | typed:place | colocación genérica: no conecta a sistemas ni a conectores, ni aloja en caras. Efecto en Revit pendiente de la campaña nativa |
| F17-DUCT-CREATE-001 | Crear conducto entre puntos | `IMPLEMENTED_UNVERIFIED` | typed:duct_create | entre puntos; la conexión a conectores y los accesorios siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F17-FITTINGS-001 | Accesorios, uniones y conectores de conducto/tubería/bandeja/tubo | `UNKNOWN` | none | investigación pendiente: Document.NewElbowFitting/NewTeeFitting y conectores de familia; requieren contrato de conectividad (F16) antes de tiparlos |
| F17-FLEX-001 | Conducto flexible y conversión a flexible | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F17-INSUL-001 | Aislamiento de conductos y tuberías | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F17-PLACEHOLDER-001 | Marcadores de conducto y tubería y su conversión | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F17-REPORTS-001 | Informes de pérdida de presión y ajustes mecánicos | `UNKNOWN` | none | investigación pendiente: API de informes de presión no identificada; zonas y análisis de sistemas cambian entre 2022 y 2026 |
| F17-SYS-CREATE-001 | Crear sistema mecánico | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F18-FLEX-001 | Tubería flexible | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F18-PIPE-CREATE-001 | Crear tubería entre puntos | `IMPLEMENTED_UNVERIFIED` | typed:pipe_create | entre puntos; la conexión a conectores y los accesorios siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F18-SYS-CREATE-001 | Crear sistema de tuberías | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F19-SPRINKLER-001 | Colocar y conectar rociadores | `MISSING` | none | sin implementación en BIMBRIDGE |
| F20-ANALYTICAL-001 | Cargas de equipo y cargas por área (análisis eléctrico) | `UNKNOWN` | none | investigación pendiente: API de cargas analíticas eléctricas introducida en versiones recientes; verificar por versión |
| F20-CIRCUIT-CREATE-001 | Crear circuito eléctrico de fuerza | `IMPLEMENTED_UNVERIFIED` | typed:circuit_create | verifica miembros y cuadro; la compatibilidad de tensión y polos la decide Revit (si la rechaza, el plan se revierte). Efecto en Revit pendiente de la campaña nativa |
| F20-CIRCUIT-PANEL-001 | Asignar panel al crear el circuito | `IMPLEMENTED_UNVERIFIED` | typed:circuit_create | sólo al crear; reasignar el cuadro de un circuito existente sigue por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F20-CONDUIT-001 | Crear conduit o bandeja | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F20-LIGHT-QA-001 | QA de luminarias y etiquetas | `IMPLEMENTED_UNVERIFIED` | query:check |  |
| F20-PANELSCHED-001 | Crear tabla de cuadro eléctrico | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F20-SETTINGS-001 | Ajustes eléctricos, factores de demanda, clasificaciones de carga y cables | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F20-WIRE-CREATE-001 | Crear cableado | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F21-LOWVOLT-001 | Dispositivos y circuitos de datos, alarma y seguridad | `MISSING` | none | sin implementación en BIMBRIDGE |
| F22-FAB-001 | Colocar o leer partes de fabricación MEP | `DYNAMIC_CSHARP_ONLY` | exec | depende de la configuración de fabricación cargada |
| F22-FAB-PART-001 | Piezas de fabricación MEP y ajustes | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F22-PID-001 | P&ID Modeler | `UNKNOWN` | none | investigación pendiente: complemento de Autodesk con su propia API; no inventariado |
| F23-FAM-CLOUD-001 | Cargar familia de Autodesk (contenido en la nube) | `UNKNOWN` | none | investigación pendiente: servicio en la nube sin API pública conocida |
| F23-FAM-GEOM-001 | Sólidos y vacíos: extrusión, fundido, revolución, barrido y barrido fundido | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F23-FAM-INFO-001 | Información de familia y caras | `IMPLEMENTED_UNVERIFIED` | fam:info |  |
| F23-FAM-LOAD-001 | Cargar familia en proyecto desde carpeta autorizada | `IMPLEMENTED_UNVERIFIED` | fam:cargar |  |
| F23-FAM-NEW-001 | Nueva familia, cajetín o símbolo; guardar como familia de biblioteca | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F23-FAM-PARAM-001 | Crear o editar parámetro de familia | `IMPLEMENTED_UNVERIFIED` | fam:param |  |
| F23-FAM-TYPES-001 | Tipos de familia | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F24-UI-001 | Navegación, ventanas, ViewCube y modos de visualización de la interfaz | `NOT_APPLICABLE` | none | estado de la interfaz o de la ventana; no cambia datos del modelo que BIMBRIDGE deba planificar |
| F24-VIEW-3D-001 | Crear vista 3D isométrica con caja de sección | `IMPLEMENTED_UNVERIFIED` | typed:view_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-CAMERA-001 | Cámara (vista 3D en perspectiva) y planta estructural | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F24-VIEW-CAPTURE-001 | Capturar imagen de vista con resaltado temporal | `IMPLEMENTED_UNVERIFIED` | query:capture |  |
| F24-VIEW-CREATE-001 | Crear vista (planta, RCP, sección, callout) | `IMPLEMENTED_UNVERIFIED` | typed:view_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-DRAFT-001 | Crear vista de dibujo | `IMPLEMENTED_UNVERIFIED` | typed:view_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-DUP-001 | Duplicar vista (simple, con detalles o dependiente) | `IMPLEMENTED_UNVERIFIED` | typed:view_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-ELEV-001 | Crear alzado (marcador y vista) | `DYNAMIC_CSHARP_ONLY` | exec | view_create no admite alzados: sólo C# dinámico |
| F24-VIEW-FILTER-001 | Filtros de vista y sustitución por filtro | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F24-VIEW-HIDE-001 | Ocultar o mostrar elementos y categorías en vista | `IMPLEMENTED_UNVERIFIED` | typed:view_hide | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-MISC-001 | Región de plano, líneas ocultas por elemento, recorrido y etiquetas de vista | `UNKNOWN` | none | investigación pendiente: cada herramienta tiene una API distinta o ninguna; sin verificar |
| F24-VIEW-OVERRIDE-001 | Sustituciones gráficas y filtros de vista | `IMPLEMENTED_UNVERIFIED` | typed:view_override | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-SET-001 | Configurar vista (rango, recorte, escala, plantilla) | `IMPLEMENTED_UNVERIFIED` | typed:view_set | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F24-VIEW-TEMPLATE-001 | Crear plantilla desde la vista y aplicar sus parámetros | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F25-DETAIL-001 | Líneas, arcos, polígonos y rectángulos de detalle | `IMPLEMENTED_UNVERIFIED` | typed:detail_line | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-DETAIL-002 | Región de máscara, componente de detalle repetido, aislamiento de detalle, leyendas y componentes de leyenda | `UNKNOWN` | none | investigación pendiente: región de máscara = FilledRegion con tipo de máscara (no verificado); leyendas sin API de creación directa |
| F25-DETAILCOMP-001 | Componente de detalle | `IMPLEMENTED_UNVERIFIED` | typed:detail_component | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-DIM-001 | Crear cota entre referencias | `IMPLEMENTED_UNVERIFIED` | typed:dimension | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-DIM-002 | Cotas angulares, radiales, de diámetro y de longitud de arco; cotas temporales | `UNKNOWN` | none | investigación pendiente: NewDimension sólo crea cotas lineales; las demás necesitan API específica por versión |
| F25-KEYNOTE-001 | Notas clave de elemento, material y usuario; leyenda de notas clave | `UNKNOWN` | none | investigación pendiente: IndependentTag cubre algunas; verificar tipos de etiqueta por categoría |
| F25-REGION-001 | Región rellena | `IMPLEMENTED_UNVERIFIED` | typed:filled_region | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-REVCLOUD-001 | Nube de revisión | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F25-SPOT-001 | Cotas y coordenadas de punto y pendiente | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F25-SYMBOL-001 | Colocar símbolo de anotación | `IMPLEMENTED_UNVERIFIED` | typed:detail_component | familias basadas en vista; sin directrices. Efecto en Revit pendiente de la campaña nativa |
| F25-TAG-001 | Etiquetar elementos | `IMPLEMENTED_UNVERIFIED` | typed:tag | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-TAG-ALL-001 | Etiquetar todo lo no etiquetado (por vista) | `IMPLEMENTED_UNVERIFIED` | typed:tag | composición: query de lo no etiquetado + tag; no etiqueta recintos. Efecto en Revit pendiente de la campaña nativa |
| F25-TAG-MOVE-001 | Mover etiquetas | `IMPLEMENTED_UNVERIFIED` | typed:tag_move | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-TEXT-001 | Crear nota de texto | `IMPLEMENTED_UNVERIFIED` | typed:text_note | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F25-TEXT-REPLACE-001 | Buscar y reemplazar texto | `UNKNOWN` | none | investigación pendiente: factible con TextNote.Text + set_param, pero sin contrato ni verificador todavía |
| F26-SCHED-CREATE-001 | Crear tabla de planificación con campos | `IMPLEMENTED_UNVERIFIED` | typed:schedule_create | campos en orden; filtros, orden y agrupación siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa |
| F26-SCHED-EXPORT-001 | Exportar tabla a CSV | `IMPLEMENTED_UNVERIFIED` | doc:tabla_csv | efecto en Revit pendiente de la campaña nativa |
| F26-SCHED-GRAPH-001 | Tabla gráfica de pilares | `UNKNOWN` | none | investigación pendiente: sin API de creación identificada |
| F26-SCHED-SPECIAL-001 | Listas de láminas, de vistas, bloques de notas, tablas de revisiones, cómputo de materiales y tablas clave | `DYNAMIC_CSHARP_ONLY` | exec | schedule_create tipado sólo crea tablas de categoría |
| F27-PRINT-001 | Imprimir, configurar impresión y conjuntos | `DYNAMIC_CSHARP_ONLY` | exec | impresora del sistema: efecto externo sin rollback; doc pdf cubre PDF |
| F27-REVISION-001 | Crear y asignar revisiones a láminas | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F27-SCHED-ONSHEET-001 | Colocar tabla en lámina | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F27-SHEET-CREATE-001 | Crear lámina con cajetín | `IMPLEMENTED_UNVERIFIED` | typed:sheet_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F27-SHEET-PH-001 | Láminas de marcador de posición y cajetín | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F27-SHEET-PLACE-001 | Colocar vista en lámina | `IMPLEMENTED_UNVERIFIED` | typed:sheet_place | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F27-SHEET-SET-001 | Editar parámetros de lámina | `IMPLEMENTED_UNVERIFIED` | typed:sheet_set | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F27-SHEET-VIEW-001 | Colocar vista en lámina | `IMPLEMENTED_UNVERIFIED` | typed:sheet_place | efecto en Revit pendiente de la campaña nativa |
| F28-IMAGE-PDF-001 | Vincular o importar imagen y PDF; gestionar imágenes | `DYNAMIC_CSHARP_ONLY` | exec | PDF como ImageType según opciones por versión; sin verificar |
| F28-LINK-CAD-001 | Vincular o importar CAD | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F28-LINK-IFC-001 | Vincular y abrir IFC | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F28-LINK-RVT-001 | Vincular modelo RVT con posicionamiento | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F28-MANAGE-001 | Gestionar vínculos, modelos de coordinación, FormIt e insertar desde archivo | `UNKNOWN` | none | investigación pendiente: RevitLinkType.Reload/Unload existen; modelos de coordinación y FormIt sin verificar |
| F28-POINTCLOUD-001 | Nubes de puntos | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F29-CM-BATCH-001 | Copy/Monitor: copia por lotes con monitorización | `UNSUPPORTED_BY_PUBLIC_API` | postcommand | el inventario confirma que CopyMonitorOptions no existe en la API pública 2022–2026. PostCommand sólo abre la herramienta: no completa ni verifica el workflow (#23, #24). 0.9.1: preflight, traspaso manual veraz y verificación posterior en F29-CM-VERIFY-001 |
| F29-CM-VERIFY-001 | Copy/Monitor: preflight, traspaso manual y verificación de aparatos supervisados | `IMPLEMENTED_UNVERIFIED` | query:check copy_monitor | sólo lectura: la copia la hace el usuario con los pasos devueltos (no hay ejecutor de UI). Lectura en Revit pendiente de la campaña nativa |
| F29-CR-001 | Coordination Review: revisar y resolver avisos de monitorización | `UNSUPPORTED_BY_PUBLIC_API` | postcommand | sin API pública para Coordination Review; sólo flujo de UI supervisado |
| F30-CLOUD-001 | Colaborar en la nube, solicitudes de edición, historial y publicación | `UNKNOWN` | none | investigación pendiente: sin API pública identificada para estos diálogos |
| F30-OWN-001 | Liberar lo mío, recargar lo último y habilitar colaboración | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F30-WS-CREATE-001 | Crear workset | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F30-WS-SET-001 | Mover elementos a otro workset | `IMPLEMENTED_UNVERIFIED` | typed:set_workset | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F31-ASSEMBLY-001 | Crear ensamblaje | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F31-DEMOLISH-001 | Derribar (fase de derribo) | `IMPLEMENTED_UNVERIFIED` | typed:set_param | set_param sobre el parámetro de fase de derribo; la herramienta de la cinta usa la fase de la vista. Efecto en Revit pendiente de la campaña nativa |
| F31-GROUP-001 | Crear, colocar y desagrupar grupos | `IMPLEMENTED_UNVERIFIED` | typed:group_create | efecto en Revit pendiente de la campaña nativa única de 0.9.1 |
| F31-GROUP-DETAIL-001 | Colocar grupo de detalle | `IMPLEMENTED_UNVERIFIED` | typed:group_place | efecto en Revit pendiente de la campaña nativa |
| F31-GROUP-LOAD-001 | Cargar grupo en proyectos abiertos y asignar código de ensamblaje | `UNKNOWN` | none | investigación pendiente: sin API equivalente verificada |
| F31-OPTION-001 | Opciones de diseño | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F31-PARTS-001 | Crear piezas | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F31-PHASE-001 | Asignar fase de creación y derribo | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F32-ENERGY-001 | Modelo de análisis energético | `MISSING` | none | sin implementación en BIMBRIDGE |
| F32-ENERGY-002 | Crear y borrar modelo energético; generar análisis | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F32-GEO-CLASH-001 | Detección de interferencias y montaje real | `IMPLEMENTED_UNVERIFIED` | query:geo |  |
| F32-PATH-001 | Recorrido de evacuación | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F33-RENDER-002 | Renderizar, galería, nube y ajustes de sol | `UNKNOWN` | none | investigación pendiente: motor de render sin API pública de lanzamiento; SunAndShadowSettings sí existe para el sol |
| F33-RENDER-IMG-001 | Exportar imagen de vista | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F34-EXPORT-DWG-001 | Exportar DWG | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F34-EXPORT-IFC-001 | Exportar IFC con opciones | `IMPLEMENTED_UNVERIFIED` | doc:ifc | sin mapeo de property sets ni archivo de configuración IFC (siguen por C# dinámico). Efecto en Revit pendiente de la campaña nativa |
| F34-EXPORT-NWC-001 | Exportar NWC | `DYNAMIC_CSHARP_ONLY` | exec | requiere el exportador de Navisworks instalado |
| F34-EXPORT-OTHER-001 | Exportar FBX, DWF/DWFx, DGN, STL, OBJ, SAT y gbXML | `DYNAMIC_CSHARP_ONLY` | exec | accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas |
| F34-EXPORT-REPORTS-001 | Informes de habitación/área, ODBC, animaciones y tipos | `UNKNOWN` | none | investigación pendiente: sin API pública identificada para estos asistentes |
| F34-EXPORT-STEP-001 | Exportar STEP (2025+) | `DYNAMIC_CSHARP_ONLY` | exec | sólo Revit 2025–2026 (inventario) |
| F35-AGENTHOST-001 | Planes IR durables con recuperación entre procesos | `OFFLINE_VERIFIED` | engine | verificado contra el backend simulado; contra Revit, pendiente de campaña |
| F35-AUDIT-001 | Auditoría durable con group commit y recuperación | `OFFLINE_VERIFIED` | engine |  |
| F35-EXEC-001 | Ejecutar C# dinámico (consulta, ensayo, ejecutar) | `IMPLEMENTED_UNVERIFIED` | exec | no es un sandbox; efectos externos sin rollback |
| F35-IDENTITY-001 | Identidad nominal y correlación de solicitudes | `OFFLINE_VERIFIED` | engine |  |
| F35-OTHER-TOOLS-001 | Dynamo, macros, gestor de complementos y personalización | `NOT_APPLICABLE` | none | otras herramientas de automatización o personalización de la interfaz; BIMBRIDGE tiene su propio motor (exec, tareas, planes) |
| F35-QUERY-SNAP-001 | Lectura paginada desde una instantánea ligada a revisión | `OFFLINE_VERIFIED` | engine |  |
| F35-RECIPE-001 | Tareas por recetas con concesión de capacidades | `OFFLINE_VERIFIED` | engine |  |
| F36-HEALTH-001 | Salud del modelo: avisos, duplicados, parámetros vacíos y conectores MEP desconectados | `IMPLEMENTED_UNVERIFIED` | query:check model_health | sólo lectura; las comprobaciones estructurales de Revit (apoyos, consistencia analítica) no se reproducen. Lectura en Revit pendiente de la campaña nativa |
| F36-QC-INDEP-001 | Verificación independiente contra fuente declarada | `OFFLINE_VERIFIED` | engine |  |
| F36-QC-MANIFEST-001 | QC uno a uno contra manifiesto | `IMPLEMENTED_UNVERIFIED` | exec |  |
| F37-CONTEXT-001 | Modos de edición y boceto contextuales (editar boceto, centrar referencia, estado de forma, rutas, sustituciones, reconciliar alojamiento) | `UNKNOWN` | none | investigación pendiente: dependen del modo de edición activo de la interfaz; sin API pública equivalente identificada para cada uno |
| F37-DIAG-001 | Diagnóstico de conexión: Bridge, registro del cliente, herramientas, Revit | `OFFLINE_VERIFIED` | engine | las observaciones de registro por cliente se miden fuera del motor (instalador / Bridge) |
| F37-NWE-001 | Motor de workflows nativos: estados por evidencia, recibos e idempotencia | `OFFLINE_VERIFIED` | engine | sin ejecutor de UI en el producto: los flujos interactivos terminan en traspaso manual; la observación del modo nativo en Revit queda para la campaña nativa |
| F37-PDF-PLAN-001 | Plan de colocación desde PDF (documento → plan) | `OFFLINE_VERIFIED` | engine | valida el documento y el plan; la ejecución en Revit queda pendiente de campaña |
| F37-TARGET-001 | Destino estable por instancia y documento (multi-Revit) | `OFFLINE_VERIFIED` | engine | falta conectar la lista real de instancias del Bridge (diagnóstico de #25) |
| F37-UI-002 | Paletas, barras de estado, temas y controles de la interfaz | `NOT_APPLICABLE` | none | estado de la interfaz de Revit; no son operaciones del modelo |
