# Continuous integration

| Workflow | Purpose | Native Revit access |
|---|---|---|
| Repository checks | Source/license hygiene, wheel hashes, entrypoint links, Python syntax and declared dependency inventory | None |
| Worker tests | Python contract tests and Rust unit/IPC tests on Linux and Windows | None |
| Windows distribution | Existing build.ps1: Revit reference targets 2022–2026, offline C# suite, workers and installer packaging | None |
| Dependency review | Restore and known NuGet vulnerability report; audit errors or findings fail the check | None |
| Optional CodeQL | Manually enabled only when repository/account licensing supports it | None |
| Draft release | Manual accepted-source build and draft assets | None |

Actions are pinned to verified upstream commit SHAs. Default permissions are read-only, checkout credentials are not persisted, logs/inventories expire after seven days, and no untrusted PR receives a privileged Revit runner.

Reference packages are compilation inputs. Hosted runners do not establish native acceptance and must not be described as installed Revit test machines.

CodeQL for a private personal repository can be unavailable under GitHub's feature/licensing rules. Its workflow is opt-in and skipped unless ENABLE_CODEQL is deliberately set after eligibility verification. See [GitHub code scanning](https://docs.github.com/en/code-security/concepts/code-scanning/code-scanning).

Branch protection for private repositories also depends on account plan and configuration. A checked-in CODEOWNERS file is not enforcement. See [protected branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches).

## Scope limits

.editorconfig and .gitattributes define future style and line endings. Existing compiler/analyzer warnings remain visible; mass reformatting of runtime code is not part of repository foundation. Rust/Python transitive vulnerability tools and a complete resolved SBOM are additional release-review work; the declared inventory must not be presented as a complete audit.

Actual run links and conclusions belong in the acceptance report. A prepared workflow has not passed until GitHub reports a completed successful run. Failed, skipped and blocked checks retain those states.
