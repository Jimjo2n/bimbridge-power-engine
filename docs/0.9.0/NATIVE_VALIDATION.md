# BIMBRIDGE 0.9.0 Native Validation: integration and rollback

Status: **NATIVE_ACCEPTANCE_PENDING**. Lab build. Do not use on production models.
Installing, opening Revit or connecting to it requires specific authorization for the installation, the instance and the disposable copy.

## Lab requirements

- Revit closed during installation and rollback.
- A real CPython, outside WindowsApps. **It is not packaged**: it is a prerequisite. It is set in `%APPDATA%\Bimbridge\config.json`: `"power_engine": { "python": "C:\\path\\python.exe" }`. Without it, PDF features are unavailable with a diagnostic.
- Disposable local copy, not workshared.

## Block due to unreconciled operations

With pending evidence, a damaged evidence file or an error reading it, writes to that document are rejected with `documento_bloqueado_reconciliacion`. The block is on disk and survives restarts.

- `job op=pendientes doc=…` shows the block.
- `job op=reconciliar plan_id=… doc=…` compares the evidence with the model. It releases only with `revertido_verificado` or `aplicado_verificado`, and archives the evidence into `reconciliados\`. The plan becomes `reconciliado` and is not repeated.
- If it cannot be proven, a person reviews the model and, with Revit closed: `AgentHost plan-store resolver --doc "key" --plan P-x --autor name --motivo "what I reviewed" --confirmo-revision-humana`. For a damaged file, `--archivo P-x.json`: it goes to `cuarentena\`, it is not deleted.

## /quiet exit codes

0 success, 1 error, 2 Revit open, 5 an installation already exists (repeat with `/reemplazar` only in a lab profile; a backup is created).

## Install

```
Bimbridge.Setup.exe /quiet /revit=2024 /niveles=RPW
```

Before copying anything, the installer verifies the manifest and saves a backup in `%LOCALAPPDATA%\Bimbridge\respaldos\<id>\` if there was already an installation. It keeps the last 3.

## Roll back

```
Bimbridge.Setup.exe /restaurar
Bimbridge.Setup.exe /restaurar="<backup folder>"
```

- Returns code 2 if Revit is open, and touches nothing.
- Verifies the backup hashes before touching anything.
- Restores the add-in and its `.addin` for each version, `bin\` (bridge, AgentHost, Power Engine), `config.json`, `install.json`, the installer copy and `.codex\config.toml`. Whatever did not exist is deleted. Third-party add-ins, such as pyRevit, are not touched.
- Saves the current state first and prints how to undo the rollback. If it fails midway, it tries to leave the previous state.
- `.codex\config.toml` is restored in full: changes made to it after the backup are lost.
- Out of scope: the tasks and journals in `%LOCALAPPDATA%\Bimbridge` and the models.

## Offline test

With `BIMBRIDGE_SETUP_SANDBOX=<folder>`, all paths fall inside that folder, and neither the Windows registry nor Claude is touched. It is used to test installation and rollback without affecting the account.

## Reconciliation

A plan whose result is not confirmed stays in the `desconocido` state, is not applied again and keeps its evidence in `P-<id>.json`, inside the document folder. `PlanStore.LoadPersisted` recovers it after a restart and `job plan_id=` shows it. Review the model before repeating.

## Plan behavior

| Result | `estado` | Evidence |
|---|---|---|
| Successful and restored trial | `ensayado` | None left; the plan stays ready |
| Functional failure, restoration confirmed | `revertido` | None left |
| Unconfirmed rollback or unchecked restoration | `desconocido` | Kept on disk |
| Interrupted after starting to write | `desconocido` | Kept; does not return to ready |
