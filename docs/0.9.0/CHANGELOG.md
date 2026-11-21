# BIMBRIDGE 0.9.0 — engineering changelog

## Consolidated Power Engine revision (2026-11-09, base b944e6e)

- **Dynamic C# enabled by default**, with the single persistent control «Block C# code injection». «Trusted code» (4 h) and SHA authorization in `exec` are removed. See DYNAMIC_CODE_EXECUTION_POLICY.md.
- **Visible rebranding to «BIMBRIDGE - Power Engine»:** dialogs, windows, ribbon («Power Engine | Status»), add-in, MCP server, Windows Apps, installer and transaction history. The legacy prefix is still recognized.
- **Agent identity:** declared alias (`BIMBRIDGE_AGENT` or `ctx agente=`), verified principal (Windows user), per-session counters, history of disconnected sessions and attribution in the journal. See AGENT_IDENTITY_PROTOCOL.md.
- **New Status panel:** summary, agents, security and recovery, performance (median and p95) and attributed activity, with CSV and JSONL export. See POWER_ENGINE_OBSERVABILITY.md.
- **Performance:** `commit ops=…` (plan + commit in one round trip), fast count with `GetElementCount`, per-query name cache and compact `ctx`.
- **Tests:** the end-to-end test no longer reads the real `config.json`.

## Consolidated revision (2026-11-05, base 208cb2f)

Status: NATIVE_ACCEPTANCE_PENDING.

- Fail-closed writes: `PlanStore.CheckWritable` reads the disk and blocks commit, queue, exec, act, fam, doc and tarea iniciar/reanudar when there is unreconciled evidence, damaged files or read errors.
- Durable intent before opening the group (`BeginWrite`) and a checkpoint after each batch (`Checkpoint`). No disk, no write.
- `job op=pendientes` and `job op=reconciliar`: release only with the model as evidence and archive into `reconciliados\`. Human path with Revit closed: `AgentHost plan-store`.
- `PlanStore.Finish` returns and records disk failures. An exception while applying the queue is no longer declared rolled back.
- Trial: `clasificacion` with four outcomes. Commit reports `espera_cola_ms` and `revit_ms`.
- Installer: /quiet, /actualizar and the window require Revit closed and a manifest; /quiet over an existing installation gives exit code 5 without `/reemplazar`; rollback only restores the BIMBRIDGE section of `.codex\config.toml`, with a `.bak` copy before changing it. Behavior change: the Update button with Revit open no longer replaces files.

### After the native campaign (2026-11-05)

- Installation is atomic as seen from outside: if it fails midway, it rolls back to the backup on its own; if the bridge is in use, it stops before changing anything.
- A write with a lost connection answers `desconocido`, not `revit_no_disponible`.
- `job op=pendientes` only shows the block code when there is a block.

## Integration improvements

- **D32 · Windows worker.** A real executable is resolved on PATH or from the configured path. WindowsApps paths are discarded before startup. A handshake failure reports the phase, the executable by name, the observed exit code and up to 2048 characters of stderr. The Python protocol explicitly writes JSONL in UTF-8.
- **D33 · Queries.** `q … digest=true` returns the total and the SHA-256 of all filtered rows. The versioned format keeps multiplicity and full texts and sorts rows to compare runs. `timing=true` breaks the time down into bridge, dispatch, Revit thread and result processing. Counts and groupings skip reading unnecessary presentation fields.
- **D34 · PDF.** `pdf grids … sin_globo=true` enables an optional inference from labels at the ends of long lines. It requires intersections and activates on pages without recognized bubbles. Strict mode remains the default.
- **D35 · Reversible writes.** The typed plan trial is reused. Temporary verification uses rollback in `finally`. The returned status is checked and uncertain failures remain visible as `desconocido`.
- **D36 · Metrics.** The CSV summary reports characters, estimate, real tokens reported by the client and the difference for comparable pairs.
