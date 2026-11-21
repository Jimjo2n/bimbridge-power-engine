# rc1 test report — NATIVE_ACCEPTANCE_PENDING

## Automated (without Revit)
- **Suite: 389 tests** (367 from beta1.1 + 22 from rc1). 6 full runs were made after the changes: 4 clean (389/389) and 2 with one intermittent failure each (details below).
- **New rc1 tests:**
  - fail-closed with 5 missing context flags;
  - batch with one value per element and 1 commit;
  - the batch validator (mismatched ids, non-numeric keys, more than 200, empty value);
  - a commit that says ok and does not apply is never verified nor retried;
  - paged read and a model change between pages detected before writing;
  - lighting fixture and tag QC (4 cases);
  - NRT-05 stops before saving on any negative pre-check;
  - native gate;
  - stable copy with concurrent modification;
  - batch with real process death at 2 points;
  - NRT-03 and NRT-06 harnesses against FakeBim, and their blocks;
  - batch bench.
- **Separating real failures from timing failures:**
  - **Real, fixed:** the NRT-03 harness disposed of the shared connection when closing each round's backend. One round saw `revit_no_disponible`. In the native campaign it would have produced a false FAILED.
  - **Timing, unchanged:** `Service_ProcessKilled_NewServiceProcessRecoversWithoutDuplicates` and `HeldCommit_WaitsForUser_ThenApplies_AndDetectsConflicts`. They are old tests and the code they exercise did not change. They failed once each, only with the full suite in parallel, and passed 6 of 6 isolated runs. They are timeouts caused by CPU contention in a 4-core container.

## NRT-03 scenarios in simulation (execution state contract)
| # | Scenario | Test |
|---|---|---|
| 1 | Interruption before sending | NRT-03 `backend.antes_enviar` (FakeBim and harness) |
| 2 | Interruption during the request | AH-01 `backend.mitad_tx`, including the batch (real processes) |
| 3 | Response lost after a possible commit | `PersistentCommit_TimesOutAfterApplying_IsObserved_NotRepeated` and `backend.tras_commit` |
| 4 | Crash before recording the result | `runner.tras_ejecutar` and AH-01 |
| 5 | Crash during observation or verification | `runner.en_verificacion` |
| 6 | Later AgentHost resume | the 5 rounds and AH-02 (real process death) |
| 7 | Repeated requests with the same key | add-in journal (existing tests) and resuming the same journal (0 commits) |
| 8 | Concurrent operations on one document | AH-04 (two processes, a single runner, fencing) |

Unknown results: `requiere_verificacion` or `requiere_reconciliar`; never blindly retried.

## Native
| Test | Status |
|---|---|
| Native NRT-03 | BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION |
| Native NRT-06 | BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION |
| NRT-05 regression with rc1 | BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION |
| New capabilities with the real Revit API (batch, QC, paging, stable verificar_disco) | BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION |
