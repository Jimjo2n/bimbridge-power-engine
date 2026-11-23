# Changes in 0.6.1 (since v0.6.0)

Verified: `test` = automated test without Revit; `processes` = test with real OS processes; `compiles` = compiles against the 2022–2026 API; `native` = inside Revit. **Nothing in 0.6.1 is `native`.**

| Change | Verified |
|---|---|
| Cross-process lease: `lease.lock` opened with `FileShare.None` around read-check-write (same PC) | processes (20 concurrent → 1 winner) |
| Monotonic fencing token in `lease.json`; renewal keeps it; another acquisition increments it; release does not delete the file | processes + test |
| `RenewLease`, `ValidateOwnership`, `GetCurrentLease`; old `lease.json` without the field = fencing 0 | test |
| Corrupt lease store or lock not obtained → explicit error, never "free" | test |
| The runner validates owner+fencing before each batch and barrier; failed renewal → `interrumpida` ("lease lost") without running anything else | test (lease stolen midway: 0 batches afterwards) |
| `AuditLog`: synchronous audit (WriteThrough+Flush), chained by sha256, `Verify` detects deleted/altered/truncated lines | test |
| `exec`: audited BEFORE running; if the audit fails, code that writes is rejected (fail closed). `aplicar_cola` also goes to the audit | compiles |
| Telemetry (`Journal`) counts lost lines and errors instead of hiding them | test |
| Semantic capability analysis in Roslyn (`CompileWithCapabilities`) | test (7 hostile patterns) |
| `TrustedInteractive` / `RestrictedAutonomous` profiles; tasks and recipes reject ungranted capabilities at start and on resume; grants (`capacidades=`) are part of the fingerprint | test (policy) / compiles (wiring) |
| Bridge: the pipe server must be the announced Revit PID and the Revit executable | test (rule) / compiles (Win32) |
| Version 0.6.1 | — |

## rc3 (critical review of rc2)

| Change | Verified |
|---|---|
| Grant key: v1 format, rc2 read support, atomic creation, invalid/missing without regenerating | test + processes |
| Strict document fingerprint in grants | test |
| Recipes: not instantiated in `tarea iniciar`; `RecipeGate` in constructor/Preparar/MoveNext/Verificar/Postcondicion; prior authorization in the engine | test (simulated backend) + compiles |
| `revit_transaccion` (Transaction/SubTransaction/TransactionGroup/PostCommand) always denied to autonomous code | test |
| AuditLog: head, re-read of the last record, date, quarantine with pending intents | test |
| AuditGate: `auditoria_incompleta` result; exec `aplicado_error_resultado` | test + compiles |
| AgentHost 0.7.0 (start) with simulated backend | test |
