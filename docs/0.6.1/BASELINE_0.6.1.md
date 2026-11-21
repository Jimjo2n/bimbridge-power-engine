# Baseline 0.6.1

- Starting point: tag `v0.6.0` (commit 3163650), branch `release/0.6.1`. `master` untouched.
- Tests at the baseline: **193 pass, 0 fail**, ~1 min 13 s (net8, Linux, without Revit).
- P0 risk confirmed in the baseline code (`TaskStore.TryLease`): read-check-write protected only by an in-process `lock`; two Revit processes could take the same task at once. No fencing token; `ReleaseLease` deleted `lease.json` (no monotonic counter possible). `TaskRunner.RenewLease` ignored the renewal result.
- Audit: `Journal` on a background thread; with a full queue or a disk error the line was lost without warning, including the `exec` and `aplicar_cola` records.
- Roslyn: compiled code ran without any capability policy (processes, network, reflection, save/sync from a recipe).
- Bridge: the client did not check which process served the pipe.
