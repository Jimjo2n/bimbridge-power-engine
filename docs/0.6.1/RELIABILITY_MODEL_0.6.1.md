# Reliability model 0.6.1

## Lease and fencing
- Scope: **one PC**. Exclusion uses an exclusive file lock (`lease.lock`, `FileShare.None`) in the task folder. On Windows it is an OS share lock; on Linux .NET uses `flock`. Network or synced (OneDrive) task folders are not supported for exclusion.
- `lease.json`: `dueno, instancia, vence, fencing, liberada`. Fencing increments on every acquisition that is not a renewal by the current owner. Release marks `liberada` (does not delete), so fencing never goes backwards.
- Point of authority: before each batch and each barrier the runner calls `ValidateOwnership(owner, fencing)` or, every TTL/3, `RenewLease(owner, fencing)`. Any failure → `LeaseLostException` → task `interrumpida`, without running anything else.
- Residual window: an already validated batch that is running in Revit finishes even if the lease expires during it (a half-done transaction cannot be aborted from outside). The batch is bounded by the per-visit budget (ms) and the TTL is 60 s, 20× the typical budget.
- Fail safe: unreadable lease file or lock not obtained within 10 s → explicit error; never treated as free.

## Commit → journal window
No design changes from 0.6.0 (it already meets "classify, never blindly re-run"): an `inicio` without a `fin` is verified (created UniqueIds → the recipe's Verificar → safe inference by generation and save) and whatever cannot be proven stays `desconocida` until `tarea reconciliar`. The 0.6.0 fault-injection tests remain green.

## Audit vs telemetry
- Telemetry (`Journal`): asynchronous, may drop lines, now counts them (`Dropped`, `WriteErrors`).
- Audit (`AuditLog`): synchronous and durable; a failure is visible and, for a writing `exec`, blocks execution.

## Save / sync / export
Only as engine barriers when everything before is committed (0.6.0). In 0.6.1, additionally, an autonomous recipe cannot call `Document.Save/SynchronizeWithCentral/Export/Close` directly without an explicit grant.
