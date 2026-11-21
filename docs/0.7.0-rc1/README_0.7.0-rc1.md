# Bimbridge 0.7.0-rc1 — internal candidate · NATIVE_ACCEPTANCE_PENDING

Branch `feature/0.7.0-rc1`, branched from `v0.7.0-beta1.1` (c2aca78). **There is no `v0.7.0-rc1` tag**: it is only applied when the native campaign passes.
Every test against Revit is `BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION`.

## Feature matrix
| Capability | IR operation / command | Risk | Validation without Revit | Native validation |
|---|---|---|---|---|
| Typed query with count | `consultar` | Read | FakeBim | NRT-06 (pending) |
| Paged read of the scope (up to 50×200) with change detection between pages | internal to RevitBackend | Read | FakeBim (2 per page, changing total) | NRT-06 (pending) |
| Write of one parameter (text) | `fijar_parametro` | Persistent write | FakeBim + simulator | NRT-05 run2 PASSED (beta1.1); regression pending |
| **Batch write** (different value per id, 1 plan and 1 commit, ≤200) | `fijar_parametro_lote` | Persistent write | FakeBim, simulator, process death at 2 points | NRT-06 (pending) |
| **Lighting fixture and tag QC** (untagged + orphans + extra duplicates = 0) | `verificar_qc receta=qc_luminarias_tags` | Read (existing `check lighting_audit`) | FakeBim (4 cases) | NRT-06 (pending) |
| Multi-operation plan with dependencies and per-operation postconditions | PlanRunner (existing) | — | FakeBim + simulator | NRT-06 (pending) |
| Per-document and per-operation authorization | GrantStore + the operator's `RealWrites` + document fingerprint | — | FakeBim | NRT-06 (pending) |
| Cancellation, pause and resume | PlanRunner/service (existing) | — | existing tests | — |
| Reconciliation of uncertain results | Observe → `requiere_verificacion`/`requiere_reconciliar` | — | simulator + FakeBim | NRT-03 (pending) |
| Idempotency after restarting the AgentHost | journal + `opId#fencing` key + add-in journal | — | 5 crash points (FakeBim), real processes (simulator) | NRT-03 (pending) |
| Persistence on disk | `doc verificar_disco` with a stable copy | File read | synthetic files with concurrent modification | NRT-05 (regression pending) |

**They exist in the add-in but the AgentHost does not run them in rc1:** typed tagging, `parametro_por_lote` and `qc_manifiesto` (task engine recipes), and export (`doc pdf`). `RevitTaskAdapter` maps them deterministically and refuses to run them. Connecting them requires widening contracts with immediate native validation, so they move to 0.7.0 stable or 0.8.0 depending on the campaign. `fijar_parametro_lote` covers the `parametro_por_lote` case in IR for text parameters.

**Not included:** arbitrary C# execution as a substitute for typed operations, a second transaction engine, Python and Rust.

## New commands
- `agenthost revit nrt03 | nrt06`: campaign harnesses, see `CAMPANA_NATIVA_rc1.md`.
- Gate: every `agenthost revit …` and `ejecutar --revit-pid` require `BIMBRIDGE_REVIT_NATIVE=AUTORIZADO` **and** `--revit-disponible`. Without both, they do not connect and print the BLOCKED status.
