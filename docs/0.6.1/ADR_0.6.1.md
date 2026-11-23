# Architecture decisions 0.6.1

**ADR-001 Lease by file lock, not a named mutex.** A named mutex does not exist on Linux and cannot be tested in CI with real processes; a file lock can, and it lives next to the task. Scope: one PC.

**ADR-002 Fencing in `lease.json` and release without deleting.** Deleting the file reset the counter; marking it `liberada` keeps it monotonic. An old `lease.json` = fencing 0 (compatible).

**ADR-003 Validation at the runner's point of authority.** The Revit backend does not receive the token: validation happens immediately before handing over each batch, on the runner's own thread. Residual window documented (one batch in progress).

**ADR-004 Audit separate from telemetry.** Telemetry may drop lines so as not to slow Revit down; the audit is synchronous and its failure blocks code that writes.

**ADR-005 Static capability policy instead of a sandbox.** .NET offers no in-process sandbox; a separate process without the Revit API is useless for recipes that write the model. Semantic analysis + profiles + audit is adopted and the limit is declared.

**ADR-006 No Rust in 0.6.1.** Design only (RUST_INTEROP_DESIGN.md); no measurement shows that computation is the bottleneck compared with the Revit thread.
