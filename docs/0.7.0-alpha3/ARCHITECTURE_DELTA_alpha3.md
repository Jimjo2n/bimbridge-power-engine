# Architecture changes in alpha3

## New path

```
PlanRunner ──Prepare/Execute/Observe/CheckPostconditions──▶ RevitBackend
RevitBackend ──RevitLink (fixed PID, FixedTarget)──▶ RevitConnection (hello, PipeServerCheck, heartbeat, timeouts)
RevitConnection ──JSON over named pipe──▶ Add-in RequestHandler: context / query / plan / commit [modo=ensayo]
RequestHandler ──PlanApplier (single transaction engine)──▶ Revit API
```

## Decisions

| # | Decision | Reason |
|---|---|---|
| D1 | Reuse the Bridge's `RevitConnection` through a project reference | Decision: reuse the existing IPC layer. This inherits the handshake, the PID check and the timeouts. |
| D2 | The trial goes through `PlanApplier` with `Trial=true` | Decision: reuse the existing transaction engine. The trial exercises the same conflicts, batches and verification. |
| D3 | `exec consulta` without a transaction | Under level R it allowed transient changes (IUpdater effects, consumed ids). Now Revit rejects them. |
| D4 | `prueba/test/dry_run/simular` mean trial and require W | They used to be query aliases. The change is deliberately more restrictive. |
| D5 | Final `Ensayada` state, never counted as applied | Distinguishes between temporarily verified, verified rollback, committed persistent and uncertain. |
| D6 | If not everything holds, `RequiereReconciliar` | A trial that does not restore is never presented as a success. |
| D7 | The operator's `--escritura` must match the backend mode | Authorization is independent of the agent. |
| D8 | Persistent commit with `clave=opId#fencing` | Idempotency in the add-in. A timeout throws `UncertainEffectException`, then Recover and then Observe; never a blind retry. |
| D9 | Add-in plan = exactly the ids that change | If the add-in plans anything outside the scope, the plan is discarded before the commit. |
| D10 | `PlanQueue`: one state per plan under a lock | Fixes lost reschedules and guarantees that a plan never runs twice at the same time. |
| D11 | The grant key is created under an exclusive lock | `File.Move` without overwrite is not atomic on Unix in .NET. |
| D12 | Streaming `ChainLog.Open` with a callback | Memory is bounded by the longest line, not by the journal size. All validation is kept. |
| D13 | Version 0.7.0 + InformationalVersion 0.7.0-alpha3 | The updater uses `Version.TryParse`, which does not accept suffixes. |

## Files

- **New:**
  - `AgentHost/Revit/RevitBackend.cs`
  - `AgentHost/Revit/RevitCli.cs`
  - `AgentHost/Service/PlanQueue.cs`
  - Tests `AgentHostRevitTests`, `PlanQueueTests` and `ChainLogStreamingTests`
- **Modified:**
  - `Protocol/Semantics.cs`
  - `Addin/Revit/RequestHandler(.Exec).cs`
  - `Addin/Revit/PlanOps.cs`
  - `Addin/Revit/Writes/PlanApplier.cs`
  - `Bridge/RevitConnection.cs`, `CompactTools.cs` and `RevitTools.cs`
  - `Engine/Authorization.cs` and `Engine/Plans.cs`
  - `AgentHost/Exec/*` (ExecState, PlanRunner, ChainLog, ExecJournal, Backend)
  - `AgentHost/Service/*`
  - `AgentHost/Sim/DurableSimModel.cs`
  - `build.sh`
