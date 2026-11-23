# Known limitations — 0.7.0-alpha1

1. **Nothing ran in Revit.** Everything is simulated; no test is native validation. The Revit adapter refuses to write.
2. The simulated model models parameters by category/level, not geometry or real Revit transactions.
3. Documents: text only (fixtures with `=== PAGINA n ===` and `[REQ-x]` markers). There is no PDF/DOCX extraction or drawing reading.
4. Rule-based interpretation (regex) in English/Spanish for "set/fijar" and "verify"; everything else goes to human review.
5. After a crash, the next process waits for the lease to expire (30 s by default). This is intentional (AH-04), but noticeable.
6. Under a saturated CPU a very short lease may be lost; the system handles it correctly (fencing, code 4, no writes), but the process stops.
7. A single writer per plan and per model; no parallel execution within a plan.
8. `CurrentUserOnly` named pipe; no additional authentication or remote access.
9. Not a Windows service (out of scope for alpha1); runs as a process.
10. Preconditions: only `objetivos` min/max.
11. Pre-existing rc3 defect: `Key_ConcurrentInitialisation_ProducesOneKey` fails intermittently (race when creating the GrantStore key; fails closed). It already failed on 7be12ad; not fixed per the mandate.
12. rc3 E2E tests with tight timings may fail under load (`HeldCommit_WaitsForUser…` 1 time in 4).
13. Not tested on Windows in this round (paths, pipe ACL and SIGKILL→TerminateProcess).
