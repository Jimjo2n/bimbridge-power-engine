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

## First hosted execution and clean-environment correction

The first hosted commit (`7f20fd8`) passed repository and NuGet checks and both Rust jobs, but Python jobs and Windows distribution failed because fresh interpreters had no pdfminer installed. The local interpreter's installed packages had masked that setup dependency.

CI now calls `prepare_python_tests.py`, which reuses the existing hash-verifying packaging routine and prepares the ignored vendor directory without downloads or changes to the Revit engine. A clean local virtual environment reproduced 29 Python passes with 2 platform skips using these packaged dependencies. The original failed runs remain visible in Actions; subsequent hosted results are separate evidence.

The corrected Python/Rust jobs passed on Linux and Windows. The first complete hosted build then exposed CS1503 at a test expression involving `Reverse()`, despite the same source passing under local SDK 8.0.427. The repository previously did not constrain SDK selection on a multi-SDK runner. `global.json` now selects the locally verified SDK feature band, and setup-dotnet reads that selection. Product/test logic was not rewritten to suppress the failure.
