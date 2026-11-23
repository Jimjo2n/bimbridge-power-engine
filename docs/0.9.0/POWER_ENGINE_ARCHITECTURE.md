# BIMBRIDGE Power Engine — architecture

The Power Engine is the part of BIMBRIDGE that works **outside Revit**: it reads documents, computes geometry and prepares plans. The core is still C#; Python and Rust are bounded engines that the core calls through a versioned contract. There is no other orchestrator.

```
 AI (Claude / Codex)
   │  MCP stdio
   ▼
 Bridge (C#, net8) ── "power" tool (does not change the model)
   │                      │
   │ named pipe           ▼
   │              PowerEngineDispatcher (C#)  ← EngineConfig: carpetas_documentos, power_engine.*
   │                ├─ C# kernels (always available)
   │                ├─ Rust Geometry Engine: FFI (DLL) or IPC (process)   ← geometry only
   │                └─ Python Document Worker (isolated process)          ← documents only
   ▼
 Add-in in Revit (C# + Revit API): typed plan → commit, journal, audit, verification
```

## Split by language

| Language | Role | What it does NOT do |
|---|---|---|
| C# | Permanent core: contracts, dispatch, capability registry, drawing–model registration, planner, verifier, MEP, risers, groups, annotations, all Revit operations | — |
| Python | Python Document Worker: `pdf_info`, `pdf_extract`, `pdf_tables`, `pdf_symbols`, `pdf_grids`, `docx_extract` | Does not call the Revit API, does not orchestrate, has no network (sockets blocked), only opens the paths authorized for each job |
| Rust | Rust Geometry Engine: `transform3`, `nearest`, `match`, `duplicates`, `segment_contacts`, `components` | Does not read files, does not touch documents or Revit |

C# runs any operation directly when that is most efficient: the dispatcher chooses by a measured cost model (`Calibration`), and `docx_extract` has a .NET equivalent with an identical result (equivalence test).

## Power Engine contract (pe=1)

One JSON message per line over stdin/stdout. Fields: `pe`, `id` (correlation), `trabajo`, `args`, `rutas` (the only paths the job may open), `limites` (`tiempo_ms`, `max_bytes`, `max_mem_mb`).

| Aspect | Rule |
|---|---|
| Handshake | `hola` with `pe_min`/`pe_max`; the engine answers version, contract and capabilities. Incompatible contract → `VERSION_INCOMPATIBLE` and the engine is not used |
| Time | The C# client kills the process when `tiempo_ms` is exceeded and restarts it on the next job (`TIEMPO_AGOTADO`) |
| Cancellation | Core token: kills the process (`CANCELADO`). In Rust FFI it is checked by work done (every 65,536 points visited) |
| Size | Request and response limited by `max_bytes` (`DEMASIADO_GRANDE`), line reads with a cap |
| Memory | POSIX: `RLIMIT_AS` in the worker. Windows: the core measures the peak and discards the result (`LIMITE_MEMORIA`) |
| Errors | `OK`, `INVALIDO`, `RUTA_DENEGADA`, `NO_SOPORTADO`, `NO_DISPONIBLE`, `SIN_CONTENIDO`, `ERROR_MOTOR`, `TIEMPO_AGOTADO`, `CANCELADO`, `DEMASIADO_GRANDE`, `LIMITE_MEMORIA`, `VERSION_INCOMPATIBLE` |
| Telemetry | `motor_ms`, `ipc_ms`, `serializacion_ms`, `total_ms`, input and output bytes, peak memory; accumulated metrics `power_engine.<job>@<engine>` |

Controlled degradation: without Python, PDF jobs return `NO_DISPONIBLE` and everything else continues; without Rust, everything runs in C#. `power estado` shows it.

## PDF → model workflow

1. **Extraction** (Python): `pdf_grids` (grids with bubbles and "A/1" intersections) and `pdf_symbols` (candidates by geometric signature, legend and label, with confidence). **Page** coordinates; never Revit ones.
2. **Interpretation**: symbols without a legend are only accepted with consistent labels (≥80 %, ≥3 shapes, valid code; confidence 0.7); the rest stays ambiguous.
3. **Registration** (C#): anchors = intersections with the same name in the drawing and in the model. Similarity (Umeyama) or affine; removes bad anchors, checks the scale against the sheet's and composes with the link transform. States: `ok`, `anclas_insuficientes`, `degenerado`, `residuo_excesivo`, `escala_incoherente`, `ancla_invalida`.
4. **BIM plan** (C#, deterministic): stable id per source, type by rule, height or mounting, workset, orientation. Escalates to ambiguous: no type, ambiguous type, low confidence, no height, near-duplicate, conflict with an existing element. `place` batches of ≤500 points.
5. **Execution**: the same typed ops as always (`plan` → `commit`, trial available). The Power Engine does not write to Revit.
6. **QA/QC**: `PlacementVerifier` requires an independent source (a different SHA-256 and lineage from the construction) and returns a compact diff: omitted, displaced, type, rotation, height, support, visibility, duplicate, additional.

## Advanced engines

| Engine | What it distinguishes |
|---|---|
| `MepNetwork` | Three layers that do not mix: proximity (hint), geometric continuity, physical connection through connectors. Reports "touches without connecting", "connected with a gap", open ends, incompatibles |
| `RiserGraph` | Diagram versus physical components: a 2D line is not a conduit. Missing branches, duplicates, crossings and labels crossed through |
| `GroupVariants` | Local signature without origin, rotation or mirror; the majority variant is the reference; deviations per level |
| `AnnotationLayout` | Placement in paper inches with a cost (collisions, distance, alignment, leader), local repair and minimum legibility of 3/32" |

## Capability discovery

`power cap <text>` returns short cards; `power cap <id>` the full card. States: `AVAILABLE`, `EXPERIMENTAL`, `REQUIRES_AUTHORIZATION`, `NATIVE_TEST_PENDING`, `DEFERRED`, `API_LIMITATION`. Something not implemented is not marked as impossible.

## Configuration (`%APPDATA%\Bimbridge\config.json`)

```json
{
  "carpetas_documentos": ["C:\\Projects\\Drawings"],
  "power_engine": { "python": "C:\\Python312\\python.exe", "geometria": "auto" }
}
```

Without `carpetas_documentos` no document is opened. In the lab profile `laboratorio.carpetas` also counts. Empty paths: they are looked up next to the bridge (`bin\power_engine\...`, `bin\bimbridge_power_engine.dll`). The Python interpreter is not packaged; the pure dependencies are (pdfminer.six and charset-normalizer, pinned by SHA-256; `cryptography` is replaced and is only missing for AES-encrypted PDFs).

## Security

- Python: child process without a console, `-I -B`, network blocked, real paths (no links) checked against prefixes authorized per job, DOCX without DTD (XXE), document text marked as data (`sospechosos`), never obeyed.
- Rust: no I/O; non-finite inputs rejected; cooperative cancellation.
- The `power` tool is read-only for the model. Whatever changes Revit still goes through the add-in with permissions, profile, journal and audit.
