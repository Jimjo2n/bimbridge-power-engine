# RELEASE_GATE_0.6.1 (rc3)

Replaces the rc2 assessment (90/100), which **was not reproducible**: the independent review found two P0 and three P1 defects
that the matrix did not reflect. All nine findings were reproduced on the rc2 code before fixing them
(`evidencia/repro_rc2.log`: 9 of 9 tests fail on 0222b6e; code in `evidencia/ReproRc2.cs.txt`).

## A. Identity

| Field | Value |
|---|---|
| Version | 0.6.1, candidate `v0.6.1-rc3` |
| Branch | `release/0.6.1` |
| Commit | the one carrying the `v0.6.1-rc3` tag (see the package's `MANIFIESTO_rc3.txt`) |
| Audited base | 0222b6e (`v0.6.1-rc2`), superseded; never installed |
| Date | 2026-10-22 |
| Tree | clean at the tagged commit |
| Environment | Linux container, .NET SDK 8; `bash build.sh` compiles 2022–2026 against reference assemblies; log in `evidencia/build_rc3.log` |
| Native acceptance | **NOT_EXECUTED** (operator is working in Revit; Revit was not touched and nothing was installed) |

## B. Matrix (same R01–R10 scale; reassessed from scratch)

| ID | Topic | Max | rc2 | rc3 | Status |
|---|---|---|---|---|---|
| R01 | Audit before modification | 12 | 10 | 10 | PARTIAL |
| R02 | AuditLog integrity and concurrency | 12 | 12 | 10 | PARTIAL |
| R03 | Independent capability authority | 14 | 12 | 11 | PARTIAL |
| R04 | Roslyn and DLL security | 12 | 10 | 9 | PARTIAL |
| R05 | Reconciliation and state machine | 12 | 12 | 12 | PASS |
| R06 | Leases, fencing and per-document exclusion | 12 | 11 | 11 | PARTIAL |
| R07 | Checkpoints | 8 | 8 | 8 | PASS |
| R08 | Builds and integration | 8 | 6 | 6 | PARTIAL |
| R09 | Memory and performance | 5 | 4 | 4 | PARTIAL |
| R10 | C# + Rust design | 5 | 5 | 5 | PASS |
| **Total** | | **100** | ~~90~~ | **86** | |

The rc2 column is what was declared at the time; with the findings, rc2 would have scored considerably lower (R03 in particular: after restarting
Revit no grant validated). rc2 is not recalculated; rc3 is scored on what has been demonstrated.

### R01 — 10/12
- **Fixed (P1-4):** `AuditGate.Apply` no longer returns the ordinary result if recording the result fails: it returns the BIM result
  marked `auditoria_incompleta` with the intent id, which stays in `PendingIntents`. It never claims a rollback. Queue: `ApplyResult.AuditIncomplete`
  + plan detail. exec: status `aplicado_error_resultado` with `auditoria=auditoria_incompleta` and `reconciliar_intencion`.
- **Tests:** `Rc3Tests.AuditGate_OutcomeFailsAfterCommit_ResultIsMarkedIncomplete_IntentStaysPending`, `ReleaseGateTests.R01_*`.
- **−2:** the add-in wiring (queue, exec) is only verified by compiling.

### R02 — 10/12
- **Fixed (P1-3):** head `auditoria.jsonl.cabeza` (seq, hash and length of the last record) written under the lock after each line;
  `Verify` compares it with the tail (tolerates being one record behind after a crash). On every Append the last known record is re-read
  (one line) and the file date is compared; if it does not match, full verification. Quarantine carries the pending intents over to the
  first record of the new chain and `PendingIntents` keeps showing them.
- **Tests:** `Audit_LastRecordEdited_SameSize_IsDetected`, `Audit_MiddleRecordEdited_SameSize_IsDetectedByLiveInstance`,
  `Audit_LastRecordRemoved_IsDetected`, `Audit_HeadOneBehind_AfterCrash_IsTolerated`, `Audit_Quarantine_KeepsPendingIntentsVisible` + earlier R02 tests
  (8 processes × 50).
- **Threat model:** detects corruption, truncation and manual edits. It does **not** withstand a process of the same user rewriting the chain and the
  head at the same time (without a secret outside its reach there is no local way). An edit in the middle that keeps both size **and** date is only detected by the
  full verification on open.
- **−2:** those two limits.

### R03 — 11/14
- **Fixed (P0-1):** the key was stored in Base64 and on reopening the bytes of the text were used; no grant survived a restart.
  Now: format `BIMBRIDGE-GRANT-KEY v1 base64` (encoding, **not** encryption), reading of the rc2 format, atomic creation (temp file + Move without
  overwriting; another process that wins the race is respected), invalid/truncated key → error without regenerating, missing key with signed grants
  → error without regenerating. The add-in treats the error as "no authority" (denies writes).
- **Fixed (P1-5):** the document fingerprint must be identical; its absence on one side no longer widens the scope.
- **Tests:** 7 key tests (including 16 threads ×5 rounds and 6 real processes) + 2 fingerprint tests + earlier R03 tests.
- **−3:** key in plain text under the user profile (DPAPI not implemented: requires Windows and deciding compatibility across machines/users,
  see DECISIONES); the add-in boundary is only tested with a simulated backend.

### R04 — 9/12
- **Fixed (P0-2):** no recipe code runs before authorization:
  1. `tarea iniciar` no longer instantiates the recipe: Version/Transaccion are read from the code with Roslyn (constants only; otherwise it is assumed to write);
  2. the engine checks grants before reconciliation and enumeration;
  3. `RecipeGate` wraps the constructor, Preparar, each MoveNext, Verificar and Postcondicion: authority (leases + grants) before each phase
     and, in the read phases, a model write counter (tracker revision + open transaction) that must stay the same;
  4. the real policy (`Transaccion`) is checked against what was authorized at instantiation; if it writes and was authorized as read, it does not continue;
  5. opening its own transactions, subtransactions, groups or `PostCommand` is the `revit_transaccion` capability, always denied to autonomous code;
     `ModuleInitializer` counts as reflection.
- **Tests:** `Recipe_*` (6), `Gate_*`, `Analyzer_*`, `Metadata_*`, `InstalledRecipes_Regression_*` (the 5 recipes, 2022 and 2026).
- **−3:** it is still not a sandbox: whatever does not touch the model (files, CPU) is limited by the analyzer, which is a deny list plus the
  closed set of installed recipes; an allow list of operations was not implemented. Tested with a simulated backend, not in Revit.

### R05 — 12/12, R07 — 8/8, R10 — 5/5
No new findings; earlier tests green.

### R06 — 11/12
No changes. −1: document exclusion only within one PC; a validated batch finishes even if the lease expires during it.

### R08 — 6/8
`bash build.sh`: add-ins 2022–2026, bridge and installer; full suite (see `evidencia/build_rc3.log`). −2: nothing loaded in real Revit.

### R09 — 4/5
New cost measured (Linux): audit append p50 0.30 → 0.88 ms (atomic head write); intent+result p50 1.80 ms;
1,000-unit engine 369.5 → 397.3 ms with protections (~7.5 %). −1: measured on Linux; Roslyn assembly retention not fixed.

## C. Result

**86/100 demonstrated. Status: `0.7_DEVELOPMENT_READY`.** The category is assigned conservatively after an earlier, overrated
assessment. It exceeds the threshold of 70: continuing development of 0.7.0 is authorized (AgentHost with a simulated backend, without real
write permissions), with the debt isolated in section E.

## D. Production

**`PRODUCTION_NOT_APPROVED`.** Still missing: native acceptance (NATIVE_ACCEPTANCE_0.6.1.md), key protection (DPAPI or equivalent) and
IT approval for corporate use.

## E. Disabled or restricted
1. Autonomous writes on real or shared models (practice copies only, and only after native acceptance).
2. Writing `exec` outside a manually activated «Trusted code» session.
3. Non-installed recipes without `receta_externa`; own transactions in autonomous code (`revit_transaccion`).
4. Reference DLLs outside the add-in folder or the sha256 list.
5. Automatic resumption of uncertain writes.
6. Save, sync or export without a grant for that barrier.
7. AgentHost against a real backend with writes (`PlanRunner.Load` rejects it).
8. Installation on other PCs without IT; Rust workers.

## F. 0.7.0 — started in this commit
- Done (`src/Bimbridge.AgentHost`, 12 tests): its own process; versioned IR schema 1; validator (closed catalog, non-declarable fields,
  types); deterministic ids; graph with deterministic order and cycles; simulated backend; persistent state (`plan.json`, `estado.json`,
  `diario.jsonl`); one-line summary; recovery after a crash before/after applying, partial application blocked for a person.
- Next: key protection; native acceptance on a practice copy when operator says so; a real AgentHost adapter that translates
  operations into `tarea iniciar` only with installed recipes; requirement extraction from multi-page documents with traceability.
