# Changelog

## Unreleased

- feat(ui): **Copy selection ID** ribbon button. Select elements in Revit, press it and paste into Claude or Codex: the text names the document, the categories and the ids, and gives a `$sel-xxxxxxxxxxxx` reference that every BIMBRIDGE command accepts (queries, plans, geo, audits, tools) from any conversation while Revit is open. An expired reference tells the AI to ask you to copy it again.
- feat(ui): brand palette red, black and white (black actions, red mark and destructive actions, neutral grays); the product is shown as just "BIMBRIDGE" in Revit, the installer and the MCP server.
- feat(ui): "Authorize tasks" shows how many requests are waiting; the duplicate "Block C# injection" ribbon button is removed (the control stays in Settings, with confirmation).
- chore(i18n)!: **everything is now English** — MCP commands and arguments, operation names and keys, enum values, statuses, response keys (`status`, `data`, `warning`), guides, errors, persisted keys and data folders (`tasks\`, `recipes\`, `operations\`…).
  - **Spanish input is still accepted until 0.9.5** (one version, `LegacyAliases`): old commands, argument names and enum values are read as their English equivalents. Outputs are English only. User values (parameter values, names, text) are never translated: `set value=Sí` writes `Sí`.
  - Data written by 0.9.3/0.9.4 stays readable in place (journals, tasks, receipts, Copy/Monitor baselines, grants, manifests, config.json); the installer moves the old folders to the English names. Hash-chained audit records are never rewritten and retries of keys recorded before the update are recognized as the same operation.
  - Recipes renamed: `etiquetar_luces` → `tag_lights`, `parametro_por_lote` → `batch_parameter`, `qc_manifiesto` → `qc_manifest`, `montaje_soportes` → `mount_supports`, `exportar_laminas` → `export_sheets`. The old names and the old `args.*` input names of these installed recipes (`vista`, `parametro`, `valor`, `guardar=si`…) are accepted until 0.9.5.
  - Scripting API renamed: `Receta` → `Recipe`, `PoliticaTx.PorUnidad/Ninguna/Lectura` → `TxPolicy.PerUnit/None/ReadOnly`, `Unidad` → `Unit`, `Verificacion` → `Verification`, `ctx.Omitir` → `ctx.Skip`, `ctx.Artefacto` → `ctx.Artifact`, `ctx.Excepcion` → `ctx.Exception`, `OmitirUnidad` → `SkipUnit`, overrides `Ejecutar/Preparar/Unidades/Verificar/Postcondicion` → `Run/Prepare/Units/Verify/Postcondition`, `Bim.Montaje` → `Bim.Mounting`, `Bim.Grupos` → `Bim.Groups`, `Bim.Vistas` → `Bim.Views`, `Bim.Manifiesto` → `Bim.Manifest`. Setup rewrites saved scripts and recipes in the migrated `recipes\` and `scripts\` folders.
  - Installer switches are English (`/update`, `/uninstall`, `/replace`, `/removedata`, `/restore[=folder]`, `/levels=`, `/diagnostics`, `/allow-older`, `/apply-pending`); the old `/actualizar`, `/desinstalar`, `/reemplazar`, `/borrardatos`, `/restaurar`, `/niveles=`, `/diagnostico`, `/permitir-anterior`, `/aplicar-pendiente` still work until 0.9.5.
  - Delivery confirmations take `origin=user:…` (`usuario:…` still accepted). Geo `against=techos` keeps meaning roofs; use `roofs` or `ceilings`.
  - **What still breaks:** C# code sent inline by an agent (`code=`, `exec`) or loaded with `file=` from a folder the installer did not migrate must use the English scripting API. **Finish or cancel paused tasks before updating**: their saved recipe copy is bound to a hash and is not rewritten. **Finish in-flight AgentHost plans before updating** (alpha): `plan.json`/`model.json` written by 0.9.4 are not migrated.

## 0.9.4 experimental

- chore(rename)!: the product, projects, namespaces, MCP server (`bimbridge`), environment variables (`BIMBRIDGE_*`) and data folders (`%LOCALAPPDATA%\Bimbridge`, `%APPDATA%\Bimbridge`) now use the BIMBRIDGE name. Environment variables also have English names: `BIMBRIDGE_PROFILE`, `BIMBRIDGE_AGENT`, `BIMBRIDGE_CONVERSATION`, `BIMBRIDGE_MODEL`, `BIMBRIDGE_REVIT_NATIVE`, `BIMBRIDGE_UPDATE_KEY` and the AgentHost test hooks `BIMBRIDGE_AH_FAULT`, `_FAULT_MODE`, `_FAULT_MARK`, `_DELAY`, `_LEASE_MS`, `_EVIDENCE`.
  - **Updating a 0.9.x install is a one-time manual step:** close Revit and double-click `Bimbridge.Setup.exe`. The ribbon Update button and update ZIPs of 0.9.x cannot find the renamed installer.
  - **Before updating, finish or cancel paused tasks** (`tarea`): their saved recipe copy is bound to a hash and is not rewritten.
  - Setup moves data, configuration, the grant key, the Codex section, user-level environment variables and the Claude Code registration (with its env block) to the new names, and updates namespaces in saved scripts and recipes (`recetas\`, `scripts\`). The old add-in and bridge are removed only after the new install succeeds. If the old bridge is still in use, restart open Claude/Codex conversations.
  - Claude Code permission rules written for the previous MCP tool prefix must be re-created for `mcp__bimbridge__*`. Revit keyboard shortcuts and Quick Access Toolbar buttons for the add-in must be assigned again.
  - The protocol compatibility tool (`tools/aceptacion/compat-protocolo2`) was removed: bridges before and after the rename use different pipe names.
- feat(ui): redesigned installer and add-in windows on a shared design system (`Theme.cs`): calm surfaces, white header with brand mark, selectable option cards, consistent buttons with press feedback and keyboard focus rings, recolorable notices, stats, segmented filters and disclosures. Status and Queue windows answer the main question at a glance and keep details one click away (auto-opened only when something needs attention). Ribbon buttons animate briefly only when their state changes (5 frames / 180 ms, never looping). New Settings window (pause, apply mode, dynamic C# with a single confirmation shared with the ribbon, motion: automatic/reduced). All motion respects Windows "reduce animations" and the new setting. No engine or permission semantics changed; on the uninstall screen Enter now cancels.
- perf(geometry): merge duplicate points as the Rust grid finds them, avoiding temporary neighbor lists.
- feat(diagnostics): `plan timing=true` and `commit timing=true` report the Revit dispatch breakdown (monotonic queue wait, Revit thread time, `ExternalEvent.Raise` result, visits before start) and the commit phases (load, write intent, apply/verify, finish), plus bridge IPC time. `revit_ocupado` responses from the add-in always include the dispatch data (the bridge's own read timeout does not). `revit_ms` keeps its previous boundary. Execution policy is unchanged.
- fix(tasks): `tarea cancelar/anotar/reconciliar/olvidar` refuse with `documento_ocupado` when another Revit holds the task lease instead of reporting success while that Revit keeps writing; save-as and persist evidence no longer rewrite tasks held elsewhere.
- fix(tasks): a runner that lost its lease no longer overwrites the new owner's `tarea.json`; the final checkpoint now happens before the lease is released.
- fix(power-engine): the worker memory limit is enforced while the job runs (Windows has no RLIMIT_AS), and a lifetime peak over the limit always restarts the worker so the next job is not discarded.

## 0.9.3 experimental

- Added Revit 2027 build and installer support with .NET 10.
- Closed the 39 parity checks under the offline validation criteria.
- Improved lighting placement, linked geometry, annotations, view and connector checks, print sets, and PDF and workbook handling.
- Added the illustrated bilingual repository landing and documented the Windows installer and update ZIP.

## Unreleased — 0.9.1 functional coverage candidate (0.9.0+commit)
Branch `feat/revit-functional-coverage-0.9.1`. Status and decisions D54–D66 in [DELIVERY_STATUS](docs/0.9.1/DELIVERY_STATUS.md). No native campaign yet: nothing is NATIVE_VERIFIED.
- fix(audit) AUD-01/01B: batch-aware head recovery, durable claims, tail repair into fragments; crash-point tests for batches of 1–64.
- fix(query) AUD-02/03: snapshot revalidation after LiveRev; incremental projection within budget.
- fix(agenthost) AUD-04 #28: deterministic reproduction; crash injection separated from lease expiry.
- fix(scripting) AUD-05: ref-counted compile gates, exact entry limit, per-agent accounting.
- feat(coverage): offline Revit API inventory 2022–2026 and capability catalog F01–F37 with registry.
- feat(write): typed level/grid/wall/floor/ceiling/room/duct/pipe/circuit/schedule creation with offline validation and `verificacion_creacion` (44 ops).
- feat(workflows) #23–#25: evidence-driven receipts, idempotent attempt keys, reconciliation, native capability descriptors, `check copy_monitor` (read-only preflight, manual handoff, verification), stable multi-Revit targeting, connection diagnostics.
- feat(export): `doc ifc` (transaction always rolled back) and `doc tabla_csv`.
- fix AUD-06…AUD-11: non-finite verification values, cross-process receipts with privileged evidence, Copy/Monitor dimensions and manual-handoff continuity, commit vs postconditions, durable tail-repair fragment, full link transforms.
- feat(inventory): every public RevitAPI/RevitAPIUI symbol with full signatures per version, deterministic gzip listings with SHA-256 traceability and version diffs.
- feat(coverage): immutable capability index, `check name=cobertura` paged discovery, offline benchmarks up to 10,000 entries; 223 capabilities; all 596 PostableCommands linked (189 UNKNOWN with recorded pending research, 48 NOT_APPLICABLE).
- feat(qa): `check name=model_health` (warnings, duplicates, required parameters, free MEP connectors, documentation).
- fix AUD-18…AUD-21: final job state decided before notification (`con_diferencias` survives to history); PDF-sourced batches carry an origin witness and the commit applies the content-derived idempotency key on the operation journal; Copy/Monitor preflight freezes the full objective and verification conflicts on change; delivery documents regenerated from source.
- perf: per-query type/target/data-class caches (`ElementFields.NameCache`), per-probe type and level caches in `model_health` and lighting audit, O(1) membership in duplicate detection (9x on a 20,000-element coincident cluster, offline); `WorkflowMetrics` with declared provenance; offline workflow benchmark harness. Measured offline only; no Revit run.
- ci: push-triggered runs only on `main`, per-PR concurrency cancellation, path-filtered worker suites (the Windows build still runs them).
- feat(ops): persistent operation history with filters, cancellation and safe-retry advice (`check name=historial`).

## Unreleased — consolidated engineering delivery (0.9.0+commit)
Branch `feat/power-engine-consolidation-0.9.0`. Decisions D45–D53 in [ARCHITECTURE_DECISIONS](docs/0.9.0/ARCHITECTURE_DECISIONS.md).
- fix(engine) #21: grant-key reads tolerate transient Windows sharing violations within a bounded window; other I/O errors fail closed without the secret; key creation errors are typed.
- fix(engine) #1: the idle threshold no longer closes sessions with in-flight requests; idle and broken connections are still cleaned up (reason `inactividad`).
- perf(query) #2: full paginated reads materialize once; later pages are served from a revision-bound snapshot via `cursor=` (session- and document-bound, TTL, stale cursors rejected).
- fix(engine) #7: background jobs are explicitly supervised (`JobStore.Run` returns void); CS4014 is a build error.
- feat(agents) #3/#4: with `identidad_obligatoria=true` an unnamed session may only send hello, ping and the registration (`ctx agente=…`); reads and writes require a named agent; duplicate aliases rejected; `ctx desregistrar=true`; legacy mode marks anonymous operations; correlation ids (agent_id, session_id, request_id, trace_id, job_id, transaction_id) on every journal line.
- fix(engine): session ids are unique per session instance (64-bit random suffix, never reissued), so request ids cannot repeat after a reconnect with the same pid.
- fix(query): snapshot memory budget in estimated bytes over a compact projection (64 MB global, 32 MB per document, 4 per session, 5 min TTL); oversized results and evicted cursors are reported to the agent, never dropped silently. The figure is an estimate, not a heap measurement.
- fix(audit) #27: cross-process group commit for the audit journal. Root cause confirmed from the failing Windows run: one of eight writers exhausted the 10 s lock wait. Pending entries are written in one append, one fsync and one head update, and each writer is confirmed only after persistence. The 10 s limit is unchanged. Local measurement (same harness): 8×50 appends 21.7 s → 9.3 s, lock-wait p95 1.3–1.8 s → 0.14–0.26 s; 8×1500 438.8 s → 274.9 s.
- test(concurrency) #27: the eight-process audit test supervises children with a single deadline, bounded stream reads, tree kill and disposal, verifies the chain even when a child fails, and checks exactly 50 lines per process; deterministic group-commit fault tests added.

## Unreleased — repository foundation
- Adopt GPL-3.0-only for original code.
- Document the existing architecture, contribution process and repository provenance.
- Add offline CI, dependency updates and manually requested draft releases.
- Keep product version 0.9.0 and native acceptance pending.

Earlier development: [0.9.0 changelog](docs/0.9.0/CHANGELOG.md) and versioned documents under `docs/`.
