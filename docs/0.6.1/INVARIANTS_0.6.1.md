# Invariants and the test that checks each one

| Id | Invariant | Test |
|---|---|---|
| INV-001 | At most one current owner per task across processes | `LeaseFencingTests.Lease_TwentyRealProcesses_ExactlyOneWins` |
| INV-002 | Fencing never goes backwards (renew keeps it, acquire increments it, release does not delete) | `Lease_ExpiryTakeover_*`, `Lease_RenewalKeepsFencing_*` |
| INV-003 | An owner with an old fencing token can neither validate nor renew | `Lease_ExpiryTakeover_FencingStrictlyIncreases_OldOwnerRejected` |
| INV-004 | After losing the lease no further batch runs | `TaskEngineTests.LeaseStolen_MidRun_OldRunnerStopsBeforeNextBatch` |
| INV-005 | An unreliable lease store is never treated as free | `Lease_CorruptFile_FailsSafe_NotTreatedAsFree`, `Lease_LockHeldByAnotherHandle_*` (Windows only) |
| INV-006 | A unit that cannot be proven is never re-run on its own | 0.6.0 tests in `TaskEngineTests` (crash between commit and journal, reconcile) |
| INV-007 | The audit never loses lines silently and detects tampering | `AuditLogTests.*` |
| INV-008 | Code that writes does not run without an audit record | wiring in `RequestHandler.Exec` (compiles; no test without Revit) |
| INV-009 | An autonomous recipe does not use ungranted capabilities | `ScriptingTests.HostileCode_*`, `BuiltInRecipes_PassRestrictedAutonomousPolicy` |
| INV-010 | The bridge only talks to the announced Revit | `PipeServerCheckTests` (rule; the Win32 call only compiles here) |
