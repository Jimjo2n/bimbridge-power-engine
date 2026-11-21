# Decision log 0.6.1

| Date | Decision | Reason |
|---|---|---|
| 2026-10-22 | Do not touch Revit in this phase | operator is working in it; native acceptance stays NOT_EXECUTED |
| 2026-10-22 | Two-phase audit; nothing is applied without an intent | R01 |
| 2026-10-22 | Invalid chain ⇒ quarantine, do not continue | R02: do not hide corruption |
| 2026-10-22 | Grants only from the Revit button; MCP arguments are only «requested» | R03 |
| 2026-10-22 | Local HMAC key; DPAPI deferred to 0.7.0 | Declared risk, −2 on R03 |
| 2026-10-22 | DLL policy before loading; allow list by sha256 | R04 |
| 2026-10-22 | Autonomous recipes = closed installed set + `receta_externa` | Static analysis is not a sandbox |
| 2026-10-22 | Reconcile only uncertain states, with a note | R05 |
| 2026-10-22 | Per-document lease in addition to the task lease | R06 |
| 2026-10-22 | Checkpoint failure stops the task | R07 |
| 2026-10-22 | Rust design only | Design decision |
| 2026-10-22 | Do not move the v0.6.1 tag; new candidate v0.6.1-rc2 | Traceability |
| 2026-10-22 | Score 90/100 with explicit deductions | Without adjusting the scale |
| 2026-10-22 | rc3: key with a v1 header; Base64 declared as the encoding; never regenerate if there are grants or the key is invalid | P0-1; regenerating would invalidate or mask grants |
| 2026-10-22 | rc3: DPAPI deferred | Requires Windows to test, and choosing the scope (user/machine) changes grant portability; the functional format is verified first |
| 2026-10-22 | rc3: authorize before any recipe code; metadata read from the code, not from the object | P0-2 |
| 2026-10-22 | rc3: own transactions in autonomous code always denied | Only the engine opens transactions; this way a read phase cannot write |
| 2026-10-22 | rc3: audit head in a separate file, unsigned | Simplest mechanism that detects the last record; signing without a protected key would be ornamental |
| 2026-10-22 | rc3: result with incomplete audit = its own status, never "ok" or "revertido" | P1-4 |
| 2026-10-22 | rc3: strict fingerprint (equality, absence does not widen) | P1-5 |
| 2026-10-22 | rc3: score 86, conservative category 0.7_DEVELOPMENT_READY | rc2 overrated |
| 2026-10-22 | 0.7.0: AgentHost only with a simulated backend; rejects writes with the real backend | Backend acceptance criterion |
