# Validation and evidence

Baseline: verified delivery `a291cc6c72f8c47efde64db3d29fef6bb2a156ec`.

## Evidence categories

- **Delivery-reported:** results stated in incoming reports, associated with their source commit.
- **Independently inspected:** source, hashes, logs or benchmark arithmetic inspected during repository preparation.
- **Locally reproduced:** checks executed in the isolated sanitized import.
- **Hosted CI:** results of actual GitHub Actions runs.
- **Native acceptance:** tests within a specifically identified Revit installation and disposable model.

Never substitute one category for another.

## Incoming delivery

The outer SHA-256 matches the expected value. All 54 listed delivery hashes verify. The bundle has 14 available commits, contains b944e6e and a291cc6, passes Git object checks and matches the 381-file source archive.

The report states C# 610/610, Rust 13/13 and Python 29 passed with 2 skipped, plus installer sandbox 31/31. These are historical reported results until independently reproduced.

Native evidence is partial: the report lists passes for PE-01–08 and PE-11, failure for PE-12 (session timeout during an outstanding request), and visual/status controls not tested. Earlier NV-14/15/16 results from another build are not acceptance of a291cc6. Raw reports include private context and remain in the local audit archive.

## Current import

Sanitation and repository preparation produce a different commit from a291cc6. Current checks and hosted runs must name that import's exact SHA. Full native acceptance remains pending. No new Revit session is opened or connected during repository preparation.

## Release gates

Review full build/test results, dependency notices, package contents, correspondence of source and binaries, signing status, checksums and native acceptance for a fixed source commit. A passing offline build alone does not authorize a stable release.

The long-operation desktop-notice prototype is excluded from this baseline and requires a separate proposal, review and native acceptance.
