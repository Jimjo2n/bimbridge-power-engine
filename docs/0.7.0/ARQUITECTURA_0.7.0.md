# Bimbridge 0.7.0-alpha1 — Autonomous execution architecture

Base: v0.6.1-rc3 (commit 7be12ad). Branch: `feature/0.7.0-agenthost`. Nothing in rc3 was modified (`git diff 7be12ad -- src/` only touches `src/Bimbridge.AgentHost/`).
**Writes go ONLY to a simulated backend. Revit is not touched; the Revit adapter has writes disabled.**

## Layers

```
document (untrusted) ─► RequirementExtractor ─► RequirementInterpreter ─► PlanBuilder ─► PlanDraft (untrusted)
                                                                                              │ IrValidator (OpSpec catalog)
                                                                                              ▼
                                                                                   ValidatedPlan (immutable, hash)
                                                                                              │
           AgentHostService (bounded queue, CurrentUserOnly pipe) ──► PlanRunner ──► IExecutionBackend
                                                                         │   │             ├─ SimulatedBackend → DurableSimModel (WAL + snapshot, on disk)
                                                                         │   │             └─ RevitTaskAdapter (maps to rc3 recipes; write = rejected)
                                                                         │   └─ PlanLease (= rc3 Engine TaskStore/lease, with fencing)
                                                                         └─ ExecJournal: diario.jsonl (authority, hash chain) + estado.json (sealed checkpoint)
```

## IR (schema 2) — `Ir.cs`, `IrValidator.cs`, `PlanGraph.cs`
- Immutable plan (`ValidatedPlan`/`ValidatedOp`, deeply frozen dictionaries). Only the execution state is mutable.
- Operation id = `op-` + 20 hex of sha256(doc, fingerprint, scope, operation, parameters, source identity). Same intent with a different source ⇒ different id. A semantic key detects duplicates.
- Authorization, postconditions, idempotency and retry class are **derived** from the OpSpec catalog; the plan cannot declare them (`campo_no_declarable`).
- Length units and coordinate system are mandatory; a numeric value requires `unidades`.
- Dependencies: deterministic Kahn order; cycles rejected.
- Supported precondition: `objetivos` min/max. Explicit `permitir_vacio` (AH-05). Retries 0–3.

## Durable execution — `Exec/`
- **Journal** (`ChainLog`): chained JSONL (seq, prev, h), each line with WriteThrough + Flush(true). Broken tail ⇒ trimmed; corruption in the middle ⇒ `estado_corrupto` error, not recovered (AH-03).
- **Checkpoint** `estado.json`: derived and sealed; if it does not match the journal it is discarded and the journal wins.
- Validation before recovery (AH-03): fencing, legal transitions, known ops, known states, schema, plan/document.
- **Recovery with postconditions (AH-01)**: an operation found `en_ejecucion`/`ejecutada` after a crash is observed in the model (`Observe` → Observado/NoObservado/Parcial/Incierto/SinObjetivos). It is only marked `verificada` if `CheckPostconditions` is satisfied; partial or uncertain effect ⇒ `requiere_reconciliar`; proven effect without evidence ⇒ `requiere_verificacion`. Something already committed is never re-run.
- **Exclusive ownership (AH-04)**: `PlanLease` reuses the rc3 Engine lease (`TaskStore`, id "plan"), default TTL 30 s, renewal at TTL/3. Each owner receives an increasing fencing token that is written to the journal and that the model requires on every commit; a zombie with an old fencing token is rejected.
- **Empty set (AH-05)**: no targets ⇒ `fallida(sin_objetivos)` without writing, unless `permitir_vacio=true` ⇒ verified with evidence "0 targets".

## Durable simulated model (AH-02) — `Sim/`
`modelo.json` (sealed snapshot, gen, tx per op, fencing) + `modelo.<gen>.wal` (one line per transaction). Survives real child processes (the tests launch `dotnet AgentHost.dll` and kill it with SIGKILL). Configurable faults: reject, transient, QC deviations, delay.

## Service — `Service/`
`BackgroundService` (.NET Generic Host, not a Windows service) with a bounded `Channel`, N workers, a `CurrentUserOnly` named pipe, line-delimited JSON. Commands: salud, enviar, lista, resumen, siguiente, excepciones, cambios, fallos, informe, pausar, reanudar, cancelar, apagar. On startup it re-queues active plans with pending work. Orderly shutdown: finishes the operation in progress, checkpoints, releases the lease.

## Documents — `Documents/DocumentPipeline.cs`
Document content is untrusted data. Requirements are extracted with page/section/original text and sha256. Only unambiguous ones are converted; the rest stays under review with a reason (`lenguaje_ambiguo`, `unidades_no_explicitas`, `contenido_sospechoso`, `categoria_desconocida`, `dependencia_no_ejecutable`, `duplicado_de`, `alcance_sin_nivel`). `VerifySources` detects an altered source.

## Revit adapter — `Revit/RevitTaskAdapter.cs`
Translates validated operations into `TaskSubmission`s of the rc3 recipes (`parametro_por_lote`, `qc_manifiesto`, …) with idempotency key = op id and required grant `revit_modificar`. `WritesEnabled=false`; `Execute` returns `sin_conexion_alpha1`. There is no access to Revit API objects.

## Candidate kernel (P2) — `Kernels/MountKernelBench.cs`
See RENDIMIENTO_0.7.0.md. Rust was not implemented.
