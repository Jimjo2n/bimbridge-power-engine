# Integrating the next delivery

1. Receive an immutable source delivery with full commit ID, SHA-256, bundle, manifests, diff and sanitized test evidence. Do not edit the active developer checkout.
2. Verify archive hashes and paths. Inspect and verify the bundle in a private isolated repository. Confirm ancestry and run Git object checks. Keep original authors, dates and messages.
3. Compare the delivery source archive to its commit tree. Record absent prerequisites or mismatches rather than fabricating history.
4. Audit new history and assets for secrets, private project information and restricted binaries. Original archives containing excluded material stay local. Apply a documented sanitation mapping to the source snapshot; do not push the unsafe refs.
5. Compare the sanitized delivery against the current hosted source baseline. Port only reviewed changes into a `feat/*`, `fix/*`, `perf/*` or `docs/*` branch. Avoid independently editing the same runtime files while development is active.
6. Open a PR with behavior changes, compatibility, provenance, offline test results, native evidence and remaining uncertainty. QA reviews evidence independently where available.
7. Merge only after applicable review and passing checks. An automated agent does not grant itself independent approval.
8. Keep `0.9.0+commit` during consolidation. Use a release tag only after its defined acceptance gate. Prepare draft releases through [RELEASING.md](../RELEASING.md).

## Automated integrity preflight

The verifier executes Git integrity operations in a temporary private repository; it does not run delivery scripts, installers or Revit. This command shape is implemented by the checked-in tool:

```text
python tools/repository/verify_delivery.py <delivery.zip> --sha256 <expected-sha256> --commit <full-commit> --bundle <bundle-member-path> --source <source-zip-member-path>
```

The a291cc6 delivery was verified with this tool: 54 checksums, 381 matching source files and 14 available commits. The tool rejects duplicate/unsafe member names and mismatched hashes or source trees. Its PASS is an integrity result, not permission to upload private data or accept native behavior.

## Sanitation and history

The original source commit is an external provenance reference for a sanitized import. It cannot retain the same Git commit ID once the tree or parentage changes. The original a291cc6 history is therefore not made recoverable from this hosted repository while the local-only privacy decision remains in effect.

Never use `push --mirror` from the original private archive. Push only explicitly named reviewed branches from the sanitized repository. No process here changes visibility automatically.
