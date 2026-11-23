# BIMBRIDGE 0.5.0: delivery report

**Commit:** `c87e162` (tag `v0.5.0`), on top of `9271259` (0.4.0). **Date:** 18/10/2026.
**What is tested:** only without Revit. It compiles for Revit 2022–2026, 114 automated tests pass, and the washbasin script compiles against each year's API. **Nothing new in 0.5.0 has run inside Revit yet.** The steps to do so on the test machine are in `tools/acceptance/PROCEDURE.md`.

## 1. Corrected scope (F01–F06)

| Finding | What changed | Where |
|---|---|---|
| F01 exclusivity | The instructions of both profiles, the guides and the help window no longer say "never any other route" or "the AI never sends code". With the task authorized, the AI applies without asking for confirmation on each batch; the user pause and Hold remain. | `Bridge/CompactTools.cs`, `Bridge/Program.cs`, `Engine/Guides.cs`, `Addin/Ui/HelpWindow.cs`, `Addin/Revit/RequestHandler.cs` |
| F02 families rejected | RFAs are accepted in `exec`, `fam`, `doc` and in 7 `plan` operations (set, values, move, rotate, mirror, copy, delete). `geo` and `chk` answer `no_soportado` with the alternative instead of a generic rejection. | `RequestHandler.cs`, `Geo/GeoRunner.cs`, `LightingAuditRunner.cs` |
| F03 no general runner | New `exec` tool: C# over the public API, compiled with Roslyn 4.8 inside Revit (`roslyn\` folder, in its own load context in 2025–2026). It has three code forms and two modes: `consulta` is always rolled back, `ejecutar` uses `tx=auto`, `grupo` or `ninguna`. It includes a cache by hash, errors with the script line, JSON result, changes per document, classified warnings and an audited copy of each script. | `Scripting/Compiler.cs`, `Addin/Scripting/*`, `Engine/ScriptSource.cs`, `Addin/Revit/RequestHandler.Exec.cs` |
| F04 no family workflow | `act`/`fam`: `abrir` (EditFamily, reuses the open RFA) and `conector`, which creates, adopts or reconciles by semantic key and verifies face, normal, radius, system and duplicates. Also `param` (value, formula, create, associate), `cargar` (IFamilyLoadOptions and re-reading connectors from the project) and `caras`/`info`. | `Addin/Revit/Families/FamilyOps.cs` |
| F05 shifting document | `DocRegistry` assigns `dN` handles per instance. The document stays pinned across all slices of a job, and if it is closed the response is `destino_no_disponible`. Sets stay bound to their document. | `Addin/Revit/DocRegistry.cs`, `Engine/Sessions.cs`, all runners |
| F06 reconnection | The bridge pins the Revit instance. Writes never switch Revit on their own. Reads only move to a single Revit of the same year, and the AI receives a warning when that happens. | `Bridge/RevitConnection.cs` |

Also added:
- **Document operations** (`guardar`, `guardar_como`, `sync`, `pdf`, `cerrar`). Each runs as a job and is verified with file, date, size, SHA-256, PDF pages and the synchronization event.
- **Durable journal** `operaciones.jsonl` with `clave=` and `tarea=`: the same key is never applied twice, an interrupted operation requires reconciliation, and a key with different content is rejected.
- **Long responses by handle:** `res <id> desde=`.
- **Per-command status** within a batch.
- **Per-task tag rule:** `tags=uno|ninguno|ignorar`.
- **Limits:** 200 operations and 20,000 entries per plan.
- **F16:** if a link transform fails, the link is excluded with a warning; identity is no longer used silently.
- **F19:** the installer does not re-enable a disabled MCP and keeps Codex keys; the build checks exit codes.

## 2. Tools (real schema)

Dump of `tools/list` from the compiled bridge: `docs/TOOL_SCHEMA_compact.json` and `docs/TOOL_SCHEMA_complete.json`.

| Profile | 0.4.0 | 0.5.0 |
|---|---|---|
| Compact (default) | 3 tools, 2,356 characters (~590 tokens) | 5 tools (`revit`, `plan`, `commit`, `exec`, `act`), 5,810 characters (~1,450 tokens) |
| Complete | 14 tools, 15,647 characters | 19 tools, 20,264 characters |

The fixed cost of the compact profile grows by about 860 tokens per conversation. That is the price of having `exec` and `act` always available.

## 3. Capability and requirement matrix

- `docs/CAPABILITY_MATRIX.md`: generated from `Engine/Capabilities.cs`, and a test fails if they differ. For each command it states whether it applies to projects or families, the permission level and the real evidence.
- `docs/REQUIREMENTS_STATUS_0.5.0.csv`: status of R001–R071 with evidence and what is missing.

| Status | Requirements |
|---|---|
| Done | 33, most marked "without Revit" |
| Partial | 10 |
| Unchanged in this version | 16 |
| Pending | 12 |

The ten P0 items (R001–R010) have code. Still missing: R006 (the set lacks generation/revision) and the automated test for R007. None is demonstrated in Revit.

## 4. Tests (evidence)

`dotnet test`: **114 pass, 0 fail** (previously 92). The new ones are:

| Test | What it shows |
|---|---|
| `ScriptingTests.Washbasin_CompilesAgainstEachYearsApi` ×5 | `tools/acceptance/washbasin_exec.cs` compiles against RevitAPI/RevitAPIUI 2022, 2023, 2024, 2025 and 2026 and against each year's add-in |
| `ScriptingTests.ExpressionAndBody_Compile` ×5, `Errors_PointToTheUsersLine`, `Wrap_*` | the three code forms; the error points at the user's line |
| `DurabilityTests` (7) | journal: no repeat after restart, interrupted → reconcile, different fingerprint → rejection; results by handle; sets per document; matrix = code; tag rule |
| `EndToEnd...ExecAndAct_PassArgumentsAndStopAfterFailure` | the bridge passes `codigo/modo/tx/doc/args/clave/tarea` unchanged; `act` stops after a failure and marks it; `revit fam` does not write |
| `SetupTests.Update_KeepsMcpOff_AndCodexExtraKeys` | F19/A31 |

These tests found and fixed two real bugs:
- `ctx.ToMm()` was static and did not compile the way anyone would write it.
- A DLL without a manifest among the references broke every compilation (CS1509).

A third fix came from reviewing code that cannot be tested here. In Revit 2022–2024 the compiled script would not have found the add-in, which Revit loads with LoadFrom; it is now resolved against what is already loaded. This remains untested until it runs in Revit.

## 5. Measurements (not an equivalent benchmark)

Compilation of the washbasin script measured in this container (2.8 GHz Xeon, 4 cores), without Revit:

| Year | First | Subsequent (median of 7) |
|---|---|---|
| 2022 | 2,697 ms (includes loading Roslyn) | 128 ms |
| 2023 | 231 ms | 140 ms |
| 2024 | 290 ms | 124 ms |
| 2025 | 524 ms | 89 ms |
| 2026 | 227 ms | 107 ms |

Inside Revit, the cache by hash means repeating a script does not recompile it.

**There is no equivalent comparison with pyRevit (R068/R069).** The same model and the same operation must be run with both engines and the QC compared; this is step 5 of the procedure. The 0.2.x figures against pyRevit were for queries, not families.

## 6. Delivery

In `/mnt/project-files/mcp-revit/0.5.0/`:
- `Bimbridge.Setup_0.5.0.zip` contains the installer `Bimbridge.Setup.exe`, with the 2022–2026 add-in + `roslyn\` + bridge. The historical environment used 0.4.0 on Revit 2024; Revit 2026 was outside the scope of that validation.
- `Bimbridge_codigo_0.5.0_c87e162.zip` is the exact source code of the commit. The binary carries `0.5.0+c87e162…`.
- `MANIFIESTO_0.5.0.txt` holds the SHA-256 of the zips, the installer and each installed file.

## 7. Remaining limits

1. **Nothing in 0.5.0 has run in Revit.** The washbasin, the long job, recovery and the equivalent benchmark are still missing.
2. `exec` runs on the Revit thread: a long script blocks Revit while it runs. There is no thread yielding between units (R019) and no scheduler with per-document barriers (R020).
3. There is no Python adapter and no pyRevit helpers (R013): `exec` is C# only.
4. Sets are still per conversation and are lost on reconnect. Jobs and plans with `tarea=` are recovered (R023 partial).
5. There are no checkpoints inside a long job (R017/R018). Recovery is per operation with `clave=`.
6. Unchanged in this version: groups (R031–R034), hosting and orientation (R035–R040), views, templates and dimensions (R046–R051) and PDF sources (R064–R066). `exec` can handle them case by case, but there are no typed operations or specific QC.
7. **Washbasin:**
   - the front is taken as the family's −Y (Front view); if it is not, pass `frente=`;
   - the In or Bidirectional flow is still undecided and will not change until the operator says so.
8. It compiles with the Nice3point reference packages, not with the installed DLLs. `build.ps1 -RevitRoot` allows compiling against the real ones on the PC.
9. In Revit 2022–2024, Roslyn and its dependencies (System.Memory, Immutable…) share the same domain. If another add-in loads different versions there may be a clash; this risk is untested.
