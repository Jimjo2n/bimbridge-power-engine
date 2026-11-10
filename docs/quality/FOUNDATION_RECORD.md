# Foundation validation record — 2026-11-10

## Source and privacy

The original a291cc6 delivery and all 54 checksum entries verify. Its 381-file source ZIP matches the Git bundle; 14 available commits pass object checks. Original history is retained locally and is excluded from the remote under the privacy exception. The hosted source is a documented sanitized import.

## Locally reproduced

Runtime/build source commit: `18b571bd3485d5ee0a617352762b66de07bdcf50` (initial sanitized foundation). Subsequent foundation changes add repository verification tooling and documentation; they do not include the desktop-notice prototype.

| Check | Result |
|---|---|
| Existing Windows build.ps1 | PASS |
| Revit reference compilation 2022–2026 | PASS |
| C# offline suite | 610 PASS, 0 FAIL |
| Rust | 6 unit + 7 IPC PASS |
| Python worker | 29 PASS, 2 SKIPPED by platform conditions |
| Local installer generation | PASS; not installed |
| Known NuGet vulnerability report | No findings in this executed report |
| New delivery-verifier archive tests | 3 PASS |
| Delivery verifier on real a291cc6 ZIP | PASS: 54 checksums, 381 source files, 14 available commits |
| Initial source/license/wheel/link checks | PASS |

Local build used Windows x64 GNU Rust and the available .NET 8 SDK/CPython. Hosted Windows CI selects MSVC Rust. This difference is recorded rather than described as an identical toolchain.

The locally generated installer SHA-256 is `7732fc1fad5aaf79097031e0ecee6d4b500dc09ca52be2403dfad92631742fb8`. It is a private build artifact, not a published or natively accepted release.

## Limits

No live Revit model, installation or UI acceptance was performed by repository preparation. Existing native reports are partial historical evidence. Compiler/analyzer warnings remain visible and are tracked for triage. Existing whitespace debt is not hidden by mass runtime-code reformatting.

At the time this record was prepared, hosted CI had not yet run. Consult actual [GitHub Actions](https://github.com/Jimjo2n/bimbridge-power-engine/actions) and the separately delivered acceptance report for completed run conclusions. This record does not turn an unexecuted workflow into PASS.

No stable tag, public release or visibility change is authorized by these checks.
