# Reversible trial acceptance — 0.9.0

**NATIVE_ACCEPTANCE_PENDING · AWAITING_USER_AUTHORIZATION**

## Requirements

- Explicit authorization for the PID/instance, path of a disposable local copy, build version and one concrete reversible write. The copy must be synthetic or public, without worksharing and without pending changes.
- Lab profile, sufficient access levels and a document pinned by handle. Verify path, identity, read-only condition and absence of pending changes.
- A narrowly scoped initial operation. Change the comments parameter of a single test instance to a distinguishable value. Record the initial value and the SHA-256 of the closed RVT before the campaign.

## Trial

1. Get the original value with a targeted read. Create a typed plan pinned to the document and review the diff. The plan prepares the change without applying it.
2. Send a single `commit` write call with that `plan_id` and `modo=ensayo`. The Revit thread opens a group, applies the child transaction, verifies the temporary value, rolls back the group and re-reads the restored value in that same invocation.
3. Require `estado=ensayado`, `temporal_ok=true`, `grupo_estado=RolledBack`, `restauracion_ok=true`, at least one re-read and no persistent temporary element. Confirmation requires both boolean flags.
4. Re-read the parameter independently and compare with the original. Keep the receipts and the on-disk hash of the copy. Finish without saving or syncing.
5. If the response is lost, there is a `desconocido` or a mismatch, stop the campaign and reconcile by reading. A separate Undo call is outside this procedure.

## Scope of the offline evidence

The mocks exercise the verification → rollback → return sequence and exceptions. Compilation checks the adapter. The first campaign covers only modifying one parameter. Creation, deletion, secondary Revit changes, groups in Pending state and other operation types require specific evidence. The verification trial inside the group and the subsequent re-read are part of a single write call.
