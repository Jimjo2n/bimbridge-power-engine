# BIMBRIDGE Power Engine — arquitectura

El Power Engine es la parte de BIMBRIDGE que trabaja **fuera de Revit**: lee documentos, calcula geometría y prepara planes. El núcleo sigue siendo C#; Python y Rust son motores acotados que el núcleo llama por un contrato versionado. No hay otro orquestador.

```
 IA (Claude / Codex)
   │  MCP stdio
   ▼
 Bridge (C#, net8) ── herramienta "power" (no cambia el modelo)
   │                      │
   │ named pipe           ▼
   │              PowerEngineDispatcher (C#)  ← EngineConfig: carpetas_documentos, power_engine.*
   │                ├─ C# kernels (siempre disponibles)
   │                ├─ Rust Geometry Engine: FFI (DLL) o IPC (proceso)   ← sólo geometría
   │                └─ Python Document Worker (proceso aislado)          ← sólo documentos
   ▼
 Add-in en Revit (C# + Revit API): plan → commit tipado, diario, auditoría, verificación
```

## Reparto por lenguaje

| Lenguaje | Papel | Qué NO hace |
|---|---|---|
| C# | Núcleo permanente: contratos, despacho, registro de capacidades, registro plano–modelo, planificador, verificador, MEP, montantes, grupos, anotaciones, todas las operaciones de Revit | — |
| Python | Python Document Worker: `pdf_info`, `pdf_extract`, `pdf_tables`, `pdf_symbols`, `pdf_grids`, `docx_extract` | No llama a la API de Revit, no orquesta, no tiene red (sockets bloqueados), sólo abre las rutas autorizadas de cada trabajo |
| Rust | Rust Geometry Engine: `transform3`, `nearest`, `match`, `duplicates`, `segment_contacts`, `components` | No lee archivos, no toca documentos ni Revit |

C# ejecuta directamente cualquier operación cuando es lo más eficiente: el despachador elige por un modelo de coste medido (`Calibration`), y `docx_extract` tiene un equivalente .NET con resultado idéntico (prueba de equivalencia).

## Contrato Power Engine (pe=1)

Un mensaje JSON por línea por stdin/stdout. Campos: `pe`, `id` (correlación), `trabajo`, `args`, `rutas` (únicas rutas que el trabajo puede abrir), `limites` (`tiempo_ms`, `max_bytes`, `max_mem_mb`).

| Aspecto | Regla |
|---|---|
| Saludo | `hola` con `pe_min`/`pe_max`; el motor responde versión, contrato y capacidades. Contrato incompatible → `VERSION_INCOMPATIBLE` y el motor no se usa |
| Tiempo | El cliente C# mata el proceso al superar `tiempo_ms` y lo reinicia en el trabajo siguiente (`TIEMPO_AGOTADO`) |
| Cancelación | Token del núcleo: mata el proceso (`CANCELADO`). En Rust FFI se comprueba por trabajo hecho (cada 65 536 puntos visitados) |
| Tamaño | Petición y respuesta limitadas por `max_bytes` (`DEMASIADO_GRANDE`), lectura de línea con tope |
| Memoria | POSIX: `RLIMIT_AS` en el worker. Windows: el núcleo mide el pico y descarta el resultado (`LIMITE_MEMORIA`) |
| Errores | `OK`, `INVALIDO`, `RUTA_DENEGADA`, `NO_SOPORTADO`, `NO_DISPONIBLE`, `SIN_CONTENIDO`, `ERROR_MOTOR`, `TIEMPO_AGOTADO`, `CANCELADO`, `DEMASIADO_GRANDE`, `LIMITE_MEMORIA`, `VERSION_INCOMPATIBLE` |
| Telemetría | `motor_ms`, `ipc_ms`, `serializacion_ms`, `total_ms`, bytes de entrada y salida, memoria pico; métricas acumuladas `power_engine.<trabajo>@<motor>` |

Degradación controlada: sin Python, los trabajos PDF devuelven `NO_DISPONIBLE` y el resto sigue; sin Rust, todo corre en C#. `power estado` lo muestra.

## Flujo PDF → modelo

1. **Extracción** (Python): `pdf_grids` (ejes con burbuja e intersecciones "A/1") y `pdf_symbols` (candidatos por firma geométrica, leyenda y rótulo, con confianza). Coordenadas de **página**; nunca de Revit.
2. **Interpretación**: símbolos sin leyenda sólo se aceptan con rótulos consistentes (≥80 %, ≥3 formas, código válido; confianza 0,7); el resto queda ambiguo.
3. **Registro** (C#): anclas = intersecciones con el mismo nombre en el plano y en el modelo. Semejanza (Umeyama) o afín; elimina anclas malas, comprueba la escala contra la de la lámina y compone con la transformación del vínculo. Estados: `ok`, `anclas_insuficientes`, `degenerado`, `residuo_excesivo`, `escala_incoherente`, `ancla_invalida`.
4. **Plan BIM** (C#, determinista): id estable por fuente, tipo por regla, altura o montaje, workset, orientación. Escala a ambiguo: sin tipo, tipo ambiguo, confianza baja, sin altura, casi duplicado, conflicto con existente. Lotes `place` de ≤500 puntos.
5. **Ejecución**: las mismas ops tipadas de siempre (`plan` → `commit`, ensayo disponible). El Power Engine no escribe en Revit.
6. **QA/QC**: `PlacementVerifier` exige una fuente independiente (otro SHA-256 y otro linaje que la construcción) y devuelve un diff compacto: omitido, desplazado, tipo, giro, altura, soporte, visibilidad, duplicado, adicional.

## Motores avanzados

| Motor | Qué distingue |
|---|---|
| `MepNetwork` | Tres capas que no se mezclan: proximidad (pista), continuidad geométrica, conexión física por conectores. Informa "toca sin conectar", "conectado con hueco", extremos abiertos, incompatibles |
| `RiserGraph` | Diagrama frente a componentes físicos: una línea 2D no es un conduit. Ramas faltantes, duplicados, cruces y rótulos atravesados |
| `GroupVariants` | Firma local sin origen, giro ni espejo; la variante mayoritaria es la referencia; desviaciones por nivel |
| `AnnotationLayout` | Colocación en pulgadas de papel con coste (colisiones, distancia, alineación, leader), reparación local y legibilidad mínima 3/32" |

## Descubrimiento de capacidades

`power cap <texto>` devuelve fichas breves; `power cap <id>` la ficha completa. Estados: `AVAILABLE`, `EXPERIMENTAL`, `REQUIRES_AUTHORIZATION`, `NATIVE_TEST_PENDING`, `DEFERRED`, `API_LIMITATION`. Algo sin implementar no se marca como imposible.

## Configuración (`%APPDATA%\PDI.RevitMcp\config.json`)

```json
{
  "carpetas_documentos": ["C:\\Proyectos\\Planos"],
  "power_engine": { "python": "C:\\Python312\\python.exe", "geometria": "auto" }
}
```

Sin `carpetas_documentos` no se abre ningún documento. En el perfil de laboratorio también cuentan `laboratorio.carpetas`. Rutas vacías: se buscan junto al puente (`bin\power_engine\...`, `bin\bimbridge_power_engine.dll`). El intérprete de Python no se empaqueta; las dependencias puras sí (pdfminer.six y charset-normalizer, fijadas por SHA-256; `cryptography` se sustituye y sólo falta para PDF con cifrado AES).

## Seguridad

- Python: proceso hijo sin consola, `-I -B`, red bloqueada, rutas reales (sin enlaces) contra prefijos autorizados por trabajo, DOCX sin DTD (XXE), texto del documento marcado como datos (`sospechosos`), nunca obedecido.
- Rust: sin E/S; entradas no finitas rechazadas; cancelación cooperativa.
- La herramienta `power` es de sólo lectura para el modelo. Lo que cambia Revit sigue pasando por el add-in con permisos, perfil, diario y auditoría.
