# Consolidated delivery 0.9.0+commit — implementation report

Branch: `feat/power-engine-consolidation-0.9.0`. Base: `main` at `efe37385dd8efdf2be1e675c1d765d4a15126761` (sanitized import).
Product version: `0.9.0` + commit (no new numbering). Decisions: [D45–D50](ARCHITECTURE_DECISIONS.md).

Statuses: `PASS`, `PARTIAL`, `NOT_TESTED`, `BLOCKED`. «Native» means inside Revit. **No native test was run in this delivery:** the active Revit instance and models were not used, and nothing was installed over the add-in in use.

## 1. Feature, commit, tests and status

| Issue | Change | Commit | Regression tests | Offline | Native |
|---|---|---|---|---|---|
| #21 | Reading `clave.bin` tolerates transient sharing violations (3 s window, only HResult 32/33). Other errors end in `GrantKeyException` without the secret. Key creation has typed errors. | `5bd2017` | `Rc3Tests`: concurrent initialization without aborting the host, transient violation (barrier), persistent violation (fail-closed, no regeneration), access denied without retry | PASS (15 tests, 10/10 runs; full suite in the clean worktree) | NOT_TESTED |
| #1 | The idle threshold only closes sessions without requests in progress. A broken connection is always closed. Reason `inactividad`. Without raising the global timeout. | `d649162` | `SessionLifecycleTests`: a request of 4× the threshold without heartbeats delivers its result; an idle session expires; a client that disappears midway closes the session and the host keeps accepting connections | PASS (36 related tests, 3/3) | NOT_TESTED |
| #2 | `QuerySnapshotStore`: one model read and pages served from memory with `cursor=`, bound to session, document and revision. TTL, memory budget with LRU, stale cursors rejected. The bridge and the AgentHost use the cursor. | `42125ed` | `QuerySnapshotTests`: row equality against the classic read (5,100 rows, mm and where), binding to session, document and revision, fail-closed, TTL, LRU, per-session limit | PASS | NOT_TESTED |
| #7 | `JobStore.Run` explicitly supervised (returns void). CS4014 is a compile error. The add-in's 4 un-awaited calls were audited. | `d6a61c3` | `JobSupervisionTests`: exception → `desconocido` with reason; no result → `desconocido`; cancellation; final handler only once | PASS | NOT_TESTED |
| #3 | Mandatory named identity to operate (hello and registration free). Duplicate aliases rejected. `ctx desregistrar=true`. Config `identidad_obligatoria`. The AgentHost declares itself via configuration. | `1e1e8b8` | `AgentIdentityGateTests` (real pipe and registration); E2E tests with a unique agent per connection | PASS | NOT_TESTED |
| #4 | Correlation of agent_id, session_id, request_id, trace_id, job_id and transaction_id via AsyncLocal, on every journal line. Client `_traza` and a per-run trace from the AgentHost. | `1e1e8b8` | `AgentIdentityGateTests`: correlation, trace hidden from the handler, job and transaction, stable fingerprint | PASS | NOT_TESTED |
| #9 | Roslyn compilation cache with single-flight and counters. Dynamic C# is still enabled by default; the «Block C# code injection» control does not change. | `5fcf373` | `CompileCacheTests`: concurrency with barriers (compiles once), failures are not cached | PASS | NOT_TESTED |
| #5 | Panel: «C# engine» section from memory; agents with agent_id and «read-only» with a reason. | `5fcf373` | Add-in build 2022–2026 | PARTIAL (missing table with filters, history, cancellation from the panel and long-operation notifications) | NOT_TESTED (visual test pending authorization) |
| #6 | Native acceptance | — | — | — | BLOCKED: requires authorization and a disposable model |
| #8 | Benchmarks | — | Offline measurement of traversals (section 3) | PARTIAL | NOT_TESTED |
| Docs | ADR D45–D50, identity protocol, CHANGELOG, this report | `ff52c87` and the commit of this report | — | — | — |

## 2. Build and test evidence

Reproducible build with `build.ps1` in a clean worktree (exact HEAD, low priority), commit `1e1e8b890d71941dc118aeef5be476fbd8d971b1`:

| Step | Result |
|---|---|
| Rust (`cargo test --locked --release`) | 13/13 |
| Python worker (`unittest`) | 31 OK, 2 skipped |
| Revit add-ins 2022, 2023, 2024 (net48) and 2025, 2026 (net8, ReadyToRun) | compiled against the reference packages |
| .NET suite | **635/635** |
| Installer | `dist/Bimbridge.Setup.exe` generated |

The build of the final commit is recorded in the audit package (`SHA256SUMS`, manifest and log). In a development clone without `build.ps1`, the `ScriptingTests` and Rust library tests fail because the add-ins for all years or the native DLL have not been built. It is an environment problem, and `build.ps1` resolves it.

## 3. Performance (#2)

- **Before:** each page repeated the full query on the Revit thread. Reading all comments took 51 calls and 51 traversals (19,727 ms, versus 3,232 ms for pyRevit with 1 call; figure from the issue).
- **Now:** 1 model traversal and 50 pages from memory. Offline, 50 pages of 100 rows are served in well under 2 s (the test's generous threshold). The real figure depends on the machine and is not published as a benchmark.
- **Pending (NOT_TESTED):** equivalent native measurement against pyRevit (same model, same parameters, alternating order, cold and warm, median, p95 with a sufficient sample). No superiority is claimed without it.

## 4. Report to the coordinator (section 21)

1. **Real initial GitHub commit:** `efe37385dd8efdf2be1e675c1d765d4a15126761` (`main`). It did not move during the delivery.
2. **Earlier local changes:** the original history (14 commits up to `a291cc6`) in a separate local folder, and uncommitted Codex work in `bimbridge-codex\src` (branch `chore/github-foundation`: README, LICENSE, CI, docs). Both were left intact.
3. **Reconciliation:** fresh clone of the sanitized repository and a branch derived from `efe3738`. Only new product changes, written and tested on the clone, were carried over. No `--allow-unrelated-histories`, no force-push, no historical bundle and no excluded files.
4. **Branch URL:** https://github.com/Jimjo2n/bimbridge-power-engine/tree/feat/power-engine-consolidation-0.9.0
5. **Pull request URL:** pending. This session has no API access (no `gh` and the connector is not authorized). It can be opened from https://github.com/Jimjo2n/bimbridge-power-engine/compare/main...feat/power-engine-consolidation-0.9.0
6. **Final commit:** the one in the audit package (branch HEAD when closing the delivery).
7. **Was key initialization resolved?** Yes, offline: full suite on a clean build, without aborts. The Windows CI on GitHub, which could not be queried, is still to be confirmed.
8. **Root cause:** a transient Windows sharing violation on a just-published `clave.bin` (another handle, the indexer or the antivirus), treated as a fatal error. In addition, the multithreaded test threw the exception on an uncaught `Thread`, which aborted the test host.
9. **Was the timeout resolved?** Yes: a session with requests in progress does not expire for inactivity, without raising the global timeout.
10. **Was the repeated traversal removed?** Yes: with a cursor, 1 traversal per full read.
11. **Performance improvement:** from 51 to 1 model traversals. Native ms remain NOT_TESTED.
12. **Identity and observability:** mandatory alias to operate, duplicate rejection, deregistration and six correlation ids in the journal. In the panel: agent_id, read-only with a reason and the C# engine section.
13. **New complete BIM features:** none new in this delivery. The P0 quality gates and multi-agent support were prioritized.
14. **End-to-end workflows that work:** the existing ones (plan/commit, query, tasks) still pass the suite. PDF→Revit was not extended.
15. **Native tests run:** none.
16. **Tests that could not be run:** native acceptance (#6) on Revit 2022–2026, the panel visual test, the native benchmark against pyRevit, GitHub CI (no API access).
17. **Windows CI:** not queried. The equivalent local build (build.ps1, Windows) passes.
18. **Open regressions:** none known offline. #5 is incomplete (see section 1).
19. **Roadmap goals reached:** the P0 gates (#21, #1, #2), concurrency (#7), identity and correlation (#3, #4) and the compilation cache (#9).
20. **To get closer to 1.0.0:** authorized native acceptance (#6) with a disposable model; equivalent native benchmark (#8); complete the panel (#5: agents table with filters, history, safe cancellation and long-operation notification); propagate the trace to the Python and Rust workers; extend the BIM and PDF→Revit workflows with verifiable synthetic scenarios; green GitHub CI on the PR.

## 5. Recommendation on the issues

- Close after the PR CI: #21, #1, #2, #7, #3, #4.
- Keep open: #5 (PARTIAL), #6 (BLOCKED), #8 (PARTIAL), #9 (improvement delivered; native multi-version validation missing).

## 6. Consolidation close-out and items pending for the next version

Author and maintainer: Jose Jimenez ([Jimjo2n](https://github.com/Jimjo2n)). Commits after `521f7cf` use the Git identity configured on this machine.

### 6.1 Fixes after the GitHub checks on `521f7cf`

- **Repository checks:** the `SPDX-License-Identifier: GPL-3.0-only` header was missing from five new test files. Added following the existing convention. The repository checks (`check_repository.py`, `inspect_history.py`, repository tests) pass locally.
- **Windows distribution, `WriteSafetyTests.IsAlive_ProtectedProcess_DoesNotThrow`:** the test assumed that Windows always refuses to open pid 4 (System). That depends on the environment: a normal user gets access denied, and `IsAlive` treats it as «not our Revit». An elevated runner can open it, and then the process exists and is alive. The behavior of `IsAlive` is correct; the test's assumption was fixed, and it now requires that it never throws and that it matches what the system allows in that environment. **Hypothesis:** deduced from the code and the environment difference, without being able to read the run log (no API access). It is confirmed by the result of the GitHub check.

### 6.2 Pending

| Issue | Actual status | Priority | Depends on | Acceptance criterion |
|---|---|---|---|---|
| #5 panel | PARTIAL. Done: summary, agents with agent_id and «read-only», «C# engine» section from memory, performance (median, p95 with ≥20 samples), recovery and activity. Missing: agents table with search, filters and sorting; detailed view and per-agent history; safe job cancellation from the panel; control of new authorizations; Rust and Python statistics; **long-operation notification** (proposal not implemented: it must state agent, operation, document, elapsed time and verified status, and explain that Revit is processing and it is best to let it work, without claiming health or the end of a write without evidence). | High | Session and job states (done in #1/#7) | Functional test of the view model without Revit, plus an authorized native visual test |
| #6 native acceptance | BLOCKED. Nothing was run inside Revit. | High | Explicit authorization for a test instance and a disposable test model | Campaign with commit, Revit version, model, date, result and sanitized evidence, Revit 2022–2026 as available |
| #8 performance | PARTIAL. Verified offline: the full read goes from 51 model traversals to 1, and the following 50 pages come from memory. Measuring inside Revit is missing. | High | #6 | Same model, parameters and units; alternating order, cold and warm; median, p95 with a sufficient sample, calls, API time, serialization, memory; comparison with pyRevit without digest |
| #9 dynamic C# | Improvement delivered (single-flight). Multi-version validation inside Revit and dispatcher-entry telemetry are missing. | Medium | #6 | Cold and warm compilation measured on Revit 2022–2026 |
| Trace in workers | The trace reaches the AgentHost; Python and Rust do not record it. | Medium | — | `trace_id` in the workers' journals |
| BIM and PDF→Revit workflows | Not extended in this delivery. | Medium | #6 for execution | Reproducible synthetic scenario with explicit uncertainty |
| Scheduler (fairness between agents, per-document queues) | Not started. | Low | Native profiles from #8 | A measurement that justifies it |

### 6.3 Proposed closures (after the PR CI)

#21 (`5bd2017`), #1 (`d649162`), #2 (`42125ed`, with native measurement pending in #8), #7 (`d6a61c3`), #3 and #4 (`1e1e8b8`). None is closed on an isolated test alone: the commit, the regression test and the result of the GitHub check are linked.

### 6.4 Regressions

None discovered. The two GitHub failures were about metadata and an environment assumption in a test.

### 6.5 Branch protections

The repository is private. If GitHub does not offer branch protection rules on the current plan, the review procedure in the meantime is: every change via pull request to `main`, green GitHub Actions checks on the exact commit, maintainer review (CODEOWNERS) and no direct push to `main`.

## 7. Review of PR #22 and issue #27

Blockers raised by the coordination on `3e09581`, resolved in this branch. The SHAs of each change are in the PR history; the final SHA and its checks are published on the PR itself.

| Blocker | Change | Offline evidence | Status |
|---|---|---|---|
| #27, intermittent failure of the 8-process test on Windows CI | Cause confirmed in the log of run 42882010373: a writer exhausted the 10 s wait for the audit lock. Cross-process group commit (D53), supervised harness (single deadline, termination, release, chain verified even if a child fails) | 8 deterministic group commit tests; R02 (8×50, 401 lines, 50 per process, unique cids); 8×50 and 8×1500 measurements without failures | Resolved offline; pending several green Windows CI runs on the final SHA |
| Anonymous reads with `identidad_obligatoria=true` | Single policy: without a name only `hello`, `ping` and registration (D49) | Rejection of the 9 reads and of `ctx` without `agente=`; registration and attributed read in the journal; duplicate alias; concurrent sessions; legacy mode marked | Resolved offline |
| Reuse of `session_id` and `request_id` on reconnect | Unique id per instance with 64 random bits (D51) | 200 reconnections with the same pid without repeating `session_id` or `request_id` | Resolved offline |
| «Memory budget» measured in cells | Estimated bytes over a compact projection, with global and per-document limits and reported evictions (D52) | Long texts, per-document and global LRU, 64 concurrent writes from 16 agents over 5 documents, expiry and close | Resolved offline (estimate, not a heap measurement) |

Local audit measurement, with the same harness before and after:

| Scenario | Before | After |
|---|---|---|
| 8×50: write time | ≈21.7 s | ≈9.3 s |
| 8×50: lock wait p95 / max | 1.3–1.8 s / 4.7 s | 0.14–0.26 s / 0.28 s |
| 8×1500: total time | 438.8 s | 274.9 s |
| 8×1500: throughput | ≈27.6 writes/s | ≈44 writes/s |

Profiling showed that the line fsync costs ≈1 ms. The expensive part was the atomic rewrite of the head (≈19 ms), re-reading the tail and opening files with the antivirus active, and that is what the batch amortizes. The figures come from a single development machine and are not a native benchmark.

Tests not run: none inside Revit. The single 0.9.1 native campaign is still pending authorization.