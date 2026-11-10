# Integration report — 0.9.0

This report describes engineering changes in the source snapshot prepared for repository import. It is an implementation record, not a native acceptance certificate.

## Runtime boundaries

C# owns Revit API access, transactions, scheduling and operational state. Rust handles geometry through an isolated library or worker. Python handles document extraction with explicit path authorization and a versioned JSONL contract. Neither worker directly controls Revit.

## Python startup and encoding

Worker selection rejects the Windows application alias and checks the configured interpreter using a bounded handshake. Diagnostics identify the startup phase and capture bounded stderr. JSONL is emitted as UTF-8. Configure a real CPython executable when the system PATH exposes only an application alias.

## Query verification and metrics

The query formatter can produce a canonical SHA-256 digest over complete filtered rows. Multiplicity and full text are retained; row order is normalized for comparison. The digest permits comparing complete results without returning every page to the client.

Timing separates bridge transport, scheduling, Revit-thread execution and result processing. A formatter benchmark is not an end-to-end Revit latency measurement. Token counts derived from character counts remain estimates unless supplied by the client.

## PDF grid inference

Optional grid inference recognizes labels at endpoints of sufficiently long lines when strict grid bubbles are absent. Candidate intersections require geometric review before registration or placement. Strict extraction remains the default.

## Write recovery

Typed trial operations verify a temporary result and roll back within the same invocation. Rollback is checked rather than assumed. Lost connections or incomplete evidence remain unknown and require reconciliation before retrying.

Durable intent and checkpoints precede model writes. A failed or unreadable journal blocks further writes. Recovery uses document evidence and does not infer success from a disconnected client.

## Reproduction and limitations

Use the [development guide](../DEVELOPMENT.md) for build order and offline tests. Compile the Revit add-in references before running script-analysis tests. Configure CPython explicitly for document-worker integration tests.

Use [native validation](NATIVE_VALIDATION.md) for the isolated Revit campaign. Native acceptance remains pending for this import. Historical measurements and test counts apply only to the source snapshots identified in their reports. No native performance improvement is claimed by this document.

