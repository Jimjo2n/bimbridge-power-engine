# Test report 0.6.1-rc3

This report replaces the earlier ones (5d1f2a1 and rc2), which no longer describe the current code.

| Field | Value |
|---|---|
| Commit | the one tagged `v0.6.1-rc3` |
| Build | `bash build.sh` without errors: add-ins 2022, 2023, 2024, 2025, 2026; bridge; installer (`evidencia/build_rc3.log`) |
| Tests | see the `Passed!` line of `evidencia/build_rc3.log` (284 expected: 241 from rc2 + 31 rc3 + 12 AgentHost) |
| Failures | 0 |
| Native status (Revit) | **NOT_EXECUTED** |

## Bugs found and fixed (reproduced on rc2)

`evidencia/repro_rc2.log` — 9 tests that assert the correct behavior, all failing on 0222b6e:

| Test | Finding |
|---|---|
| P0_Key_GrantSurvivesRebuild, P0_Key_TwoInstancesAgree | HMAC key re-read as text: grants invalid after reopening |
| P0_Recipe_EnumerationNotReachedWithoutGrant | the engine enumerated (the recipe's Preparar/MoveNext) before authorizing |
| P1_Audit_LastRecordEditedSameSize_Detected | editing the last record went undetected |
| P1_Audit_MiddleEditSameSize_DetectedByLiveInstance | an edit without a size change was ignored by the live instance |
| P1_Audit_QuarantineKeepsPendingIntents | quarantine hid pending intents |
| P1_AuditGate_OutcomeFailure_NotReturnedAsNormal | unaudited result returned as normal |
| P1_Print_MissingCurrentPrint_DoesNotWiden, P1_Print_GrantWithoutPrint_DoesNotCoverPrintedDoc | a missing fingerprint widened the grant |

## Tests added

- `Rc3Tests` (31): key (8 cases, including corrupt ×4, missing with grants, 16 threads ×5 rounds, 6 real processes, rc2 format),
  fingerprint (2), recipe boundary (6 with the real engine and a backend that reproduces the add-in's, 1 for the gate), analyzer and metadata (2),
  regression of the 5 installed recipes on 2022 and 2026 (1), AuditLog (6), AuditGate (1).
- `AgentHostTests` (12): ids, validator, graph and cycles, execution, crash after and before applying, partial application, failed verification,
  another plan's folder, cancellation, rejection of writes with the real backend.

## Not run
- Level D (inside Revit): everything. In particular, `RecipeGate` in the add-in (write counter with the real DocumentTracker), queue and exec with
  incomplete audit, and the «Authorize tasks» dialog after restarting Revit.
- `Lease_LockHeldByAnotherHandle_TimesOutExplicitly` only asserts on Windows.

## Compatibility verified
- Compiles against the Revit 2022–2026 API (net48 and net8.0-windows).
- Grant key created by rc2 (Base64 without a header): it is read (test `Key_Rc2Format_IsStillAccepted`).
- Audit from before rc3 (without a header): accepted; the head starts at the next record.
- 0.6.0 tasks without a `requiere` list: assumed to write (no change).

## Residual risks
Plain-text key under the user profile; local audit without an external anchor; the analyzer is not a sandbox; document exclusion per PC;
measurements on Linux. Details in RELEASE_GATE_0.6.1.md §B.
