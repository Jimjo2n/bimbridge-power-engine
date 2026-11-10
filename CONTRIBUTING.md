# Contributing

Work in a topic branch and open a pull request with the problem, resulting behavior, validation and limitations. Keep changes focused and preserve compatibility across supported Revit targets.

Run the relevant checks in [Development](docs/DEVELOPMENT.md). Revit acceptance is separate from offline tests. Include sanitized reproduction steps and exact commit identifiers; use synthetic fixtures instead of customer models.

Changes to transport, transactions, execution permissions, installation, signing or workflows require particular maintainer review. Document architectural decisions and user-visible behavior. Update the changelog without inventing test results or changing the product version for every commit.

By submitting original contributions you agree to license them under GPL-3.0-only. Preserve third-party notices. Do not submit credentials, private documents, Autodesk binaries or material you cannot contribute.

Maintainer: @Jimjo2n. Contributors must communicate respectfully, address technical evidence and avoid harassment or disclosure of private information.

## Branches and commits

Use `feat/*`, `fix/*`, `docs/*`, `perf/*`, `refactor/*` and `test/*`. New commits may use Conventional Commits (`feat:`, `fix:`, `docs:`); historical commits are not rewritten to adopt the convention. Explain behavior and validation in PRs rather than relying on a title prefix.

## Compatibility and diagnostics

Preserve public tool names, protocol fields, assembly identities, units and existing installation paths unless an explicit migration covers them. New errors must distinguish invalid input, busy/unavailable state, failure and uncertain write effects. Keep logs bounded and correlated; never print secrets or model payloads indiscriminately.

To support another Revit version, review target frameworks, reference packages, build loops, installer required paths and test references together. Compile against the intended API and record separate native evidence before advertising acceptance.

## Evidence and documentation

Update source-backed architecture records and the documentation index when introducing behavior. Add an ADR for meaningful boundary or policy changes. Use the benchmark template for performance claims. A full Windows build and applicable CI checks are required before integration; native behavior additionally needs fixed-commit acceptance evidence.

New development deliveries follow [the integration procedure](docs/operations/MAINTENANCE.md). Do not edit a developer's active checkout or auto-merge failed or unreviewed changes. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
