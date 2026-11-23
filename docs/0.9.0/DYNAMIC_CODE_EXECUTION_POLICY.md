# Dynamic C# execution policy

Status: NATIVE_ACCEPTANCE_PENDING for this revision.

## Behavior

| Control | Default | Effect |
|---|---|---|
| **Block C# code injection** (BIMBRIDGE ribbon, Authority panel) | off | `exec` runs for authorized local agents |
| On | — | Every new `exec` (consulta, ensayo, ejecutar) is rejected with `inyeccion_csharp_bloqueada` before compiling |

- Persists in `%APPDATA%\Bimbridge\config.json` as `"bloquear_inyeccion_csharp": true|false`. If the file is damaged, it is treated as blocked (fail-closed).
- If the change cannot be saved, the button does not apply it and says so.
- It applies to new requests. It does not interrupt what has already entered the Revit thread.
- It is recorded in the audit (`bloquear_inyeccion_csharp: bloqueado|permitido`), with the person as author.

## Who is an «authorized local agent»

1. A process of the **same Windows user**. The pipe uses `PipeOptions.CurrentUserOnly` and an ACL that only admits that user. There is no network listener.
2. With the **level** from `config.json`: `exec ejecutar` and `ensayo` require W; `consulta` requires R. `runner: apagado` still turns off all `exec`.

Turning the block off does **not** open `exec` to external or anonymous connections: they do not exist.

## What was removed

- The 4-hour, memory-only «Trusted code». It is replaced by the single persistent control.
- Per-script SHA-256 authorization (`codigo_autorizado`) on the `exec` path. `ProfilePolicy.DenyDynamicCode` is still in the code, but is no longer called from `exec`.

## What is kept

- Durable audit before running code that writes; without the audit it does not run.
- `ReferencePolicy` for extra DLLs (`refs=`).
- Autonomous recipes and tasks with the restricted profile and their grants.
- Revit thread rules: Roslyn compilation happens off the thread; execution, in the dispatcher's `ExternalEvent`.
- Existing compilation cache (`ScriptHost`, content-based key). It was not extended in this revision.

## Limits (it is not a sandbox)

The code runs in the Revit process with the user's permissions. A `TransactionGroup` rolls back document changes, not files, network, saves or other documents. Typed operations (`plan`/`commit`) remain the path with verifiable guarantees: diff, conflict, durable intent, reconciliation.

**Organizational risk:** enabling C# by default may clash with the security policy of the environment where it is installed. The configuration must be reviewed before production use.
