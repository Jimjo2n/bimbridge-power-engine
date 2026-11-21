# Security model 0.6.1

## Honest limit
The C# code that the MCP compiles runs **inside the Revit process with the user's permissions**. .NET (Framework 4.8 and 8) has no in-process sandbox. What 0.6.1 adds is a **policy gate** before running, plus durable audit; it is not isolation. Real isolation would require a separate process without access to the Revit API (see ROADMAP_0.7.0).

## Profiles
| Profile | When | What it allows |
|---|---|---|
| TrustedInteractive | `exec` in a session with a person (Hold/Apply queue available) | everything, durably audited before running; if the audit fails, queries only |
| RestrictedAutonomous | `tarea iniciar` / `tarea reanudar` (recipes that run on their own) | Revit API inside the engine's transaction, `ctx.Artefacto`, engine barriers |

In autonomous mode the following is **always denied**: processes, network, reflection (incl. `dynamic`, `Activator`, `Type.GetType`, `Invoke`), native code (`extern`, `unsafe`, `Marshal`), registry, process exit, environment variables, threads, closing/opening documents.
**Grantable** with `capacidades=[...]` (recorded in the task fingerprint): `archivo_escritura`, `revit_guardar`, `revit_sync`, `revit_exportar`. Without a grant, save/sync/PDF only via `Unidad.Guardar/Sync/Pdf`, which the engine runs as verified barriers.

The policy is applied twice: at start and on resume (the recipe is recompiled from the task's copy and its SHA-256 is checked first).

## Recipe integrity vs authenticity
- Integrity: the task keeps the exact copy and its SHA-256; resuming with an altered copy is rejected (since 0.6.0).
- Authenticity: **there is no signature**. Anyone with write access to `%LOCALAPPDATA%\Bimbridge\recetas` can drop in a recipe. 0.6.1 mitigation: the capability policy limits what it can do. Recipe signing: ROADMAP_0.7.0.

## Bridge / pipe
- Pipe for the current user only (ACL in 2022–2024, `CurrentUserOnly` in 2025–2026), no network; max frame 16 MB (as before).
- New: the client checks with `GetNamedPipeServerProcessId` that the server is the announced PID and that the executable is Revit (prevents another process from taking the name).

## Audit
`%LOCALAPPDATA%\Bimbridge\auditoria\auditoria.jsonl`: one line per `exec_codigo` (with the sha256 of the code and the path of the full copy) and per `aplicar_cola`. Chain `prev` = sha256 of the previous line. `AuditLog.Verify` detects deletion, editing or a truncated line. It does not protect against someone rewriting the whole file with a new chain (there is no external anchor).

## What does NOT change
No silent installs, nothing hidden from IT, no outbound network from the add-in.
