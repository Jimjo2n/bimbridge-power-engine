# rc1 performance (without Revit)

Measured in a Linux container with 4 cores and .NET 8, against the fake add-in over a **real pipe** (the Bridge IPC). These are relative figures; Revit latencies come from the native campaign.

## Batches (`Rc1_Bench_BatchSizes_CallsBytesAndLatency`)
| Elements | Mode | IPC calls | Commits | IPC bytes | ms |
|---|---|---|---|---|---|
| 1 | batch | 6 | 1 | 1,208 | 11* |
| 10 | batch | 6 | 1 | 2,716 | 11 |
| 10 | one by one | 51 | 10 | 9,614 | 88 |
| 50 | batch | 6 | 1 | 9,396 | 11 |
| 50 | one by one | 251 | 50 | 47,246 | 518 |
| 200 | batch | 6 | 1 | 34,451 | 16 |

\* The first measurement (114 ms) includes the pipe's cold start.

**Conclusion:** a batch keeps 6 calls per operation whatever its size (context, count, baseline, plan, commit and postcondition). With 50 elements it saves 98 % of the calls and 80 % of the bytes compared with going one by one. With this evidence the maximum per operation was raised from 30 to **200** (one add-in page). The re-read before the commit is kept on purpose: it is the protection against changes between Prepare and Execute, and it is not trimmed to save cost.

## Inherited from beta1 (no code changes on that path)
- 1000-operation plan with the durable simulator: 2.75 s, 172 MB RSS, 1.5 MB journal.
- Journal recovery: 0.39 s, including dotnet startup.
- Native NRT-05 (beta1.1): commit 183 ms and save + verify on disk 4.9 s.
