# Initial engineering backlog

All entries refer to the verified a291cc6 delivery. Incoming evidence is reported unless a record explicitly says independently reproduced. No assignees or delivery dates are implied.

| Priority | Work | Acceptance |
|---|---|---|
| P0 | Session timeout while a request is outstanding | An operation longer than the idle limit does not lose its session solely because it is in flight; abandoned sessions still expire; no duplicate write |
| P0 | Paginated-read regression | Equivalent complete reads avoid repeated full traversal; measured correctness, latency and bounded memory |
| P1 | Explicit agent registration policy | Missing/invalid aliases handled consistently without treating aliases as authentication |
| P1 | End-to-end correlation | A request can be followed across MCP, AgentHost and workers without exposing secrets |
| P1 | Status-panel controls | Filters, actions and per-engine views have verified behavior and visual acceptance |
| P1 | Native acceptance gaps | Fixed-commit matrix covers untested scenarios with sanitized evidence |
| P1 | Async/concurrency diagnostics | Warnings triaged; cancellation, disconnection and disposal have meaningful regression coverage |
| P1 | A/B evidence review | Equivalent workloads and disputed ratios reconciled |
| P1 | Dynamic-code trust documentation | Defaults, blocking, local privileges and rollback limits agree with source |
| P2 | BIM workflow expansion | Defined scope and end-to-end verification on authorized fixtures |
| P2 | Batch improvements | Correctness and recovery preserved under measured workload |
| P2 | Community examples | Reproducible public/synthetic inputs with documentation |

Detailed GitHub issues should link to evidence, scope, expected behavior and measurable acceptance criteria. An issue records work; it does not assert completion.
