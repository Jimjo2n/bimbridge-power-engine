# Test report for 0.7.0-alpha3

## Summary

| Set | Passed | Failed | Blocked | Where |
|---|---|---|---|---|
| Full suite (without Revit) | 343 | 0 | 0 | `build.sh` on Linux, .NET 8; log in `logs/tests_full.log` and `logs/build.log` |
| Build of the 2022–2026 add-in, Bridge, Setup and AgentHost | 5/5 versions | 0 | 0 | `logs/build.log` and `logs/publish_agenthost.log` |
| Native NRT-04 (Revit 2024, disposable copy) | — | — | **PENDING** | Needs the test machine and its owner's authorization to install alpha3 |
| Native NRT-03 | — | — | **PENDING (beta2)** | Prepared; the simulated version passes |

There used to be 326 tests, with one failing (`Key_ConcurrentInitialisation_ProducesOneKey`, which already failed on 7be12ad). Now there are 343 and all pass.

## New tests (17)

| Test | What it shows |
|---|---|
| `AgentHostRevitTests.Trial_FromAgentHost_IsEnsayada_ModelUnchanged_NothingPersisted` | The full path AgentHost → pipe → plan/commit trial: status `ensayada`, identical model and no persistent commit |
| `…WritesNeedOperatorAuthorization_AndMatchingMode` | Without `RealWrites`, or with a different mode, the plan is rejected before touching anything |
| `…Identity_WrongPid_ReadOnlyLevels_Workshared_AndOtherDocument_AreRefused` | Rejections for a foreign PID, level R, workshared document and a different fingerprint; never auto-selects |
| `…TrialThatDoesNotRestore_IsNeverReportedAsEnsayada` | A trial that leaves changes ends in `requiere_reconciliar` |
| `…PlanOutsideScope_IsRejected_BeforeAnyCommit` | If the add-in plans ids outside the scope, it is discarded without a commit |
| `…PersistentCommit_TimesOutAfterApplying_IsObserved_NotRepeated` | Timeout after applying: uncertain effect, observed, 1 commit and verified |
| `…Semantics_ConsultaIsRead_EnsayoIsWrite` | `exec` classification |
| `…Nrt03_AgentHostCrash_AtEachPoint_ResumesWithoutDuplicateCommit` ×5 | AgentHost crash before sending, after a possible commit, after running without recording, during verification and after verifying without recording. In all 5 cases: 1 commit, verified and the out-of-scope element intact |
| `PlanQueueTests` ×3 | An Enqueue during execution causes a single repeat and never concurrent runs; 600 enqueues across 10 plans with 6 workers without overlaps; full queue and idempotency |
| `ChainLogStreamingTests` ×2 | A journal of about 40 MB and 40,000 records opens in streaming mode; corruption in the middle is still an error and a truncated tail is trimmed |

In addition, `IdentityJournalTests.ExecMode_Aliases_AndSpaces("PRUEBA")` now expects `ensayo` instead of `consulta`. This is an intentional and more restrictive change.

## P1: root causes

- **A, grant key.** On Unix, `File.Move(src, dst)` without overwrite checks the destination and then calls `rename()`, which does overwrite. Two creators could both "win" and end up with different keys (2 of 16 stores in the reproductions). Fix: creation under an exclusive lock (`FileShare.None`) and re-reading from disk. Result: 10 of 10 runs green, including the 6-process test.
- **B, service queue.** `_queued` was cleared before taking `_slots`. An `Enqueue` during execution queued it again; another worker took it, `TryAdd` failed and the plan was silently discarded. In addition, `reanudar` during a pending pause was ignored. Fix: `PlanQueue` with the states EnCola, Ejecutando and EjecutandoYRepetir; `reanudar` cancels the pending pause.
- **C, journal memory.** `ChainLog.Open` loaded the whole file into a `byte[]` (with `int` indexes, so it broke above 2 GB) plus a list with all records. Fix: 64 KB block reads with a callback and a 16 MB per-line limit. `DurableSimModel` validates everything in a first pass before applying anything.

## NRT-04: procedure (pending)

Preconditions, on the test machine and with its owner's authorization:

1. Install the alpha3 add-in on Revit 2024.
2. Open a second Revit 2024 instance with a local, disposable, non-workshared copy, for example DynamoSample_2024.rvt.
3. Check that the operator's working session is not touched.

Steps:

```
Bimbridge.AgentHost.exe revit instancias
Bimbridge.AgentHost.exe revit identidad --revit-pid <PID of the 2nd instance>
Bimbridge.AgentHost.exe revit nrt04 --revit-pid <PID> --salida %USERPROFILE%\Desktop\SAMPLE PROJECT\NRT_alpha3 --categoria <cat> --ids <3 to 5 ids> --parametro Comments --valor NRT04
```

What the harness does:

- Blocks the test if the document is workshared or if the PID does not match.
- Takes a snapshot and its SHA-256 fingerprint.
- Checks that the plan is rejected without the operator's authorization.
- Runs the IR plan (consultar + fijar_parametro) with `RealWrites=ensayo`.
- Takes another snapshot and compares them.

Checks: `estado_op_ensayada`, `grupo_rolledback_literal`, `verificacion_temporal_addin`, `restauracion_addin`, `huella_identica`, `rev_sin_cambios`, `modificado_sin_cambios`, `mismo_documento`, `sin_guardar_ni_sync` and `no_persistio_como_aplicado`.

Exit codes: 0 = PASSED, 2 = FAILED, 3 = BLOCKED.

Evidence: `nrt04_informe.json`, `nrt04.log`, `llamadas.jsonl` (op, ms, bytes, status, PID, pipe and mode of each call), `plan_ir.json` and `estado/` (journal with hash chain). With these, plan, op, key, attempt, PID, instance, document, transaction status, verification and journal state can be correlated.

## After the technical review (2026-10-28)

**Native NRT-04 history (no runs omitted):**

| Run | Result | Note |
|---|---|---|
| run2 | BLOCKED | workshared document |
| run3 | FAILED | `page_size` bug |
| run4 | FAILED, 10/12 | rev and IsModified; the rev criterion was changed afterwards |
| run5 | PASSED, 12/12 | with `--aceptar-workshared-local`; conditional evidence |

The original evidence is on the test machine: `alpha3\NRT04\NRT04_evidencia_run2-run5.zip` (SHA256 `CFCB5EE071FE7B01FE2C0E711FAD0F06C86F2DC18F4247E3BB383CB97C7AFAFA`) and the index `EVIDENCIA_run2-run5_SHA256.txt` (37 files).

**P0 tests (6 new, suite 359/359):**
- `TrialGuardTests`:
  - the pure policy;
  - the guard comes before `PlanOps.Trial` and inside `RunMain`;
  - the guard comes before `new TransactionGroup` in `exec`;
  - capture and task QC do not open a group if there are unsaved changes;
  - no code writes `IsModified`.
- `AgentHostRevitTests.P0_TrialOnModifiedDocument_IsRefusedBeforeAnyChange_AndNotUncertain`: up-front rejection, op `fallida`, no uncertainty, `IsModified` and values intact.

The add-in paths can only run inside Revit. **NRT-04N**, the native negative test, remains **pending** until the fixed add-in is installed.
