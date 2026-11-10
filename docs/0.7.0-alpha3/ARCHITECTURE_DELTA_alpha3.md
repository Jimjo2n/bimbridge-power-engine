# Cambios de arquitectura en alpha3

## Ruta nueva

```
PlanRunner ──Prepare/Execute/Observe/CheckPostconditions──▶ RevitBackend
RevitBackend ──RevitLink (PID fijo, FixedTarget)──▶ RevitConnection (hello, PipeServerCheck, heartbeat, timeouts)
RevitConnection ──JSON por named pipe──▶ Add-in RequestHandler: context / query / plan / commit [modo=ensayo]
RequestHandler ──PlanApplier (único motor de transacciones)──▶ Revit API
```

## Decisiones

| # | Decisión | Motivo |
|---|---|---|
| D1 | Reutilizar `RevitConnection` del Bridge mediante una referencia de proyecto | Decisión: reutilizar la capa IPC existente. Así se heredan el handshake, la comprobación del PID y los timeouts. |
| D2 | El ensayo pasa por `PlanApplier` con `Trial=true` | Decisión: reutilizar el motor de transacciones existente. El ensayo ejercita los mismos conflictos, lotes y verificación. |
| D3 | `exec consulta` sin transacción | Bajo el nivel R permitía cambios transitorios (efectos de IUpdater, ids consumidos). Ahora Revit los rechaza. |
| D4 | `prueba/test/dry_run/simular` significan ensayo y exigen W | Antes eran alias de consulta. El cambio es más restrictivo a propósito. |
| D5 | Estado `Ensayada` final, nunca contado como aplicado | Distingue entre verificado temporal, rollback verificado, persistente confirmado e incierto. |
| D6 | Si no se cumple todo, `RequiereReconciliar` | Un ensayo que no restaura nunca se presenta como éxito. |
| D7 | `--escritura` del operador debe coincidir con el modo del backend | La autorización es independiente del agente. |
| D8 | Commit persistente con `clave=opId#fencing` | Idempotencia en el add-in. Un timeout lanza `UncertainEffectException`, luego Recover y luego Observe; nunca se reintenta a ciegas. |
| D9 | Plan del add-in = exactamente los ids que cambian | Si el add-in planifica algo fuera del alcance, el plan se descarta antes del commit. |
| D10 | `PlanQueue`: un estado por plan bajo un candado | Corrige reprogramaciones perdidas y garantiza que un plan nunca corra dos veces a la vez. |
| D11 | La clave de concesiones se crea bajo un candado exclusivo | `File.Move` sin sobrescribir no es atómico en Unix dentro de .NET. |
| D12 | `ChainLog.Open` en streaming con callback | La memoria queda acotada por la línea más larga y no por el tamaño del diario. Se mantiene toda la validación. |
| D13 | Version 0.7.0 + InformationalVersion 0.7.0-alpha3 | El actualizador usa `Version.TryParse`, que no acepta sufijos. |

## Archivos

- **Nuevos:**
  - `AgentHost/Revit/RevitBackend.cs`
  - `AgentHost/Revit/RevitCli.cs`
  - `AgentHost/Service/PlanQueue.cs`
  - Pruebas `AgentHostRevitTests`, `PlanQueueTests` y `ChainLogStreamingTests`
- **Modificados:**
  - `Protocol/Semantics.cs`
  - `Addin/Revit/RequestHandler(.Exec).cs`
  - `Addin/Revit/PlanOps.cs`
  - `Addin/Revit/Writes/PlanApplier.cs`
  - `Bridge/RevitConnection.cs`, `CompactTools.cs` y `RevitTools.cs`
  - `Engine/Authorization.cs` y `Engine/Plans.cs`
  - `AgentHost/Exec/*` (ExecState, PlanRunner, ChainLog, ExecJournal, Backend)
  - `AgentHost/Service/*`
  - `AgentHost/Sim/DurableSimModel.cs`
  - `build.sh`
