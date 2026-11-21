# «BIMBRIDGE - Power Engine» control center

**Power Engine | Status** button on the BIMBRIDGE ribbon. Modeless window. It does not call the Revit API or hold `Document` or `Element`. It redraws at most every 1.5 s and only if something changed; the on-disk queue is re-read at most every 5 s.

## Sections

| Section | Shows |
|---|---|
| Summary | R/P/W permissions, dynamic C# status, exact add-in version, Revit and pid, active document, connected and active agents, requests in progress, queued plans, recent errors, mode, journal health, uptime |
| Agents | Per session: alias or «unnamed», presence, client and version, identity source, verified user, session, connection, last activity, requests, in progress, errors, accumulated time, last operation and document, declared conversation and model. Below, the most recently disconnected ones with their reason |
| Security and recovery | C# block, write lock of the active document (`CheckWritable`) and how to resolve it, latest dynamic runs |
| Performance | Per operation over the last 200 requests: n, median, p95 (only with 20 or more samples) and errors |
| Activity | Last 30 entries with time, agent, operation, result, duration and document. Distinguishes queries, plans, commits, trials, unknowns and dynamic C# |

**Export:** saves `actividad_*.jsonl`, `actividad_*.csv` and `sesiones_*.json` in `%LOCALAPPDATA%\Bimbridge\exportes`.

## Limits of this revision

| Requirement | Status |
|---|---|
| Agents view with filters, sorting and search | NO: fixed list ordered by connection |
| Disconnect, revoke or cancel from the panel | NO |
| Power Engine view per engine (Rust, Python) | NO: the status is still available with `power estado` and `AgentHost power-engine estado` |
| Leases and fencing in the panel | NO |
| IPC and serialization timings per engine | NO: the commit adds `espera_cola_ms` and `revit_ms` |
| MVVM and table virtualization | NO: code-built WPF like the rest of the add-in, with change signatures and bounded limits |
| Real tokens | NO: the MCP client does not report them; the estimates remain in the `metricas` tool |
| Validation in Revit | PENDING |
