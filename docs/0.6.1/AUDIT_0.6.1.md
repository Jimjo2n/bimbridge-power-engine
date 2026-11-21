# Audit 0.6.1: findings and status

| # | Finding in v0.6.0 | Severity | 0.6.1 status | Evidence |
|---|---|---|---|---|
| A1 | Lease without cross-process atomicity | P0 | fixed | LeaseFencingTests (real processes) |
| A2 | No fencing; release deleted `lease.json` | P0 | fixed | LeaseFencingTests |
| A3 | Failed renewal ignored by the runner | P0 | fixed | LeaseStolen_MidRun |
| A4 | Unreadable `lease.json` treated as free | P0 | fixed | Lease_CorruptFile |
| A5 | `exec` audit could be lost silently | P0 | fixed (fail closed for writes) | AuditLogTests + compiles |
| A6 | Autonomous code without capability limits | P0 | mitigated (static policy; not a sandbox) | ScriptingTests |
| A7 | Client did not verify the pipe server | P1 | fixed | PipeServerCheck (rule tested; Win32 compiles) |
| A8 | Unsigned recipes (authenticity) | P1 | open → 0.7.0 | SECURITY_MODEL |
| A9 | Audit chain without an external anchor | P2 | open → 0.7.0 | SECURITY_MODEL |
| A10 | An already validated batch finishes even if the lease expires midway | P2 | accepted and documented | RELIABILITY_MODEL |
| A11 | MainThread/budgets: reviewed, no changes | — | no changes (per-visit budget already bounded since 0.6.0) | — |
| A12 | All of the above without native execution | P0 to approve | pending | NATIVE_ACCEPTANCE |
