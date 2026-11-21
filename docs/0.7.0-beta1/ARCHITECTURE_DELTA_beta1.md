# beta1 architecture delta (over alpha3)

No new frameworks. Everything is C#/.NET; no Python or Rust.

| Piece | Change |
|---|---|
| Add-in `DocOps.VerifyOnDisk` | `verificar_disco` verb of `doc`. Requires a saved local .rvt, not workshared and without changes. Computes the file's SHA-256, opens a temporary copy with `Application.OpenDocumentFile` and reads `LookupParameter(p)` for 1 to 200 ids. Closes with `Close(false)` and deletes the copy. Requires level D or W, like the rest of `doc`. |
| AgentHost `RevitCli.Nrt05Run` | The harness core is testable with a fake add-in: it receives the connection already open. |
| AgentHost `OpState.DirectiveName` | State mapping (see below). |
| Setup.Core `SetupCore.CopyOver` | Clears the read-only attribute before overwriting. `Platform.LastWarning` collects non-fatal failures. |
| build.sh / build.ps1 | Clean tree required and `SourceRevisionId=HEAD`. `BIMBRIDGE_ALLOW_DIRTY=1` for development only. |

## States (execution state contract → existing OpState)
| Design decision | OpState |
|---|---|
| PREPARED | `validada` |
| AUTHORIZED | `validada` with a current authorization for the plan (GrantStore + the operator's `RealWrites`) |
| EXECUTING | `en_ejecucion` |
| COMMITTED | `ejecutada` |
| VERIFIED | `verificada` |
| ROLLED_BACK | `ensayada` (rolled-back trial) |
| REJECTED | `bloqueada`, `cancelada` or `fallida` without a write attempt |
| OUTCOME_UNKNOWN | `requiere_verificacion` or `requiere_reconciliar` |

An IPC success is never VERIFIED: `ejecutada` only moves to `verificada` after observing the model. Trial semantics (TrialGuard) do not apply to `persistente`.

## exec and task audit (§6)
- `exec modo=ejecutar` already distinguishes these cases:
  - `revertido`: nothing applied, can be repeated.
  - `parcial`: there are committed changes; reconcile.
  - `desconocido`: Revit neither committed nor rolled back.

  It checks the status of Commit, Assimilate and RollBack. The result is serialized before the commit. No new findings.
- After an interruption, the idempotency key (`opId#fencing`) and the add-in journal prevent a second commit: it is observed and not repeated. Covered by the NRT-03 tests (5 crash points) and the persistent timeout test.

## IT policy (§12)
- **Identifiable providers:**
  - Each MCP client (Claude Code or Codex) is registered separately, and each session is recorded in the add-in journal with its id.
  - AgentHost records pid, pipe and instance on every call (`llamadas.jsonl`).
- **Restriction:** the add-in level configuration (`R`, `P`, `W`, `D`) is the per-instance switch:
  - without `W` there are no writes;
  - without `D` (or `W`) there is no save and no verificar_disco;
  - "Pause AI" rejects everything.
- **Separation by mode:** `lectura`, `ensayo` (literal rollback) and `persistente`.
  - The mode is set by the operator when launching the AgentHost; the plan cannot widen it.
  - `persistente` requires a local file, not workshared, not read-only and without a queue.
- **Audit:** plan journal (`diario.jsonl`), add-in operation journal (idempotency key), `llamadas.jsonl` with ms and bytes, and a JSON report for each NRT.
