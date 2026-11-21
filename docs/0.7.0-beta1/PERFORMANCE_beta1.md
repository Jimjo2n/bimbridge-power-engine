# beta1 performance

Linux CI container, 4 cores, .NET 8, Debug build. This is not the test machine: it serves as a relative reference, not a production figure.

| Measure | Result |
|---|---|
| 1000-operation plan (988 writes, 1030 dependencies) against the durable simulated backend | 2.75 s in total, max RSS 172 MB, 1.5 MB journal, seq 2921 |
| Journal recovery (`agenthost estado`, reopens and rebuilds 2921 events), 3 rounds | 0.39 / 0.41 / 0.39 s, including dotnet startup |
| Full suite (367 tests) | 1 m 38 s |
| NRT-05 against the fake add-in over the pipe (connect, baseline, plan, commit, observe, save, verificar_disco, resume) | < 0.2 s |

**Pending the native run** (comes from `nrt05_informe.json → metricas` and from `llamadas.jsonl`):
- MCP connection time.
- Latencies of query, plan (Prepare), commit (execute), Observe, save and verificar_disco.
- IPC bytes per operation and model calls per plan.

Design: a deterministic plan groups the writes of one operation into one add-in plan and one commit. That is why the number of calls per plan does not grow with the number of elements (max. 30 targets per operation, pages of 200).
