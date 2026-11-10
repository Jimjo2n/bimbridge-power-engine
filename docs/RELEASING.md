# Reviewed changes and releases

1. Develop in a topic branch, update documentation, and submit a PR.
2. Review the diff, security-sensitive behavior and current CI results.
3. Merge reviewed changes using GitHub branch rules; no automatic approval or merge is configured.
4. For a release, complete native acceptance on the exact integrated commit and inspect third-party notices, binaries and source correspondence. Record sanitized evidence under `docs/releases/<tag>.md`.
5. Commit the acceptance record separately after testing. Include `Acceptance: PASS` and `Commit: <full reviewed source SHA>` in the record. From the default branch, run **Draft release** with that source commit, reviewed tag and evidence path. The source must be an ancestor of the default branch. It builds that exact source commit, computes SHA-256, and creates a **draft** release. It never changes repository visibility.
6. Inspect draft assets and hashes, installer behavior, licensing and evidence, then publish the draft explicitly in GitHub. Unsigned binaries must not be presented as signed.

Use consolidated product versions; do not invent alpha/beta releases or increment for every commit. Current 0.9.0 remains native acceptance pending. Tags identify a commit; SHA-256 identifies an asset. Neither proves functional correctness.

The workflow creates a source archive from its exact commit and attaches only ZIP/EXE outputs produced by the build plus LICENSE and checksums. Build failures stop draft creation. CI availability and plan-dependent branch rules must be verified after upload. This is an event-driven process, not an unattended observer of local folders.
