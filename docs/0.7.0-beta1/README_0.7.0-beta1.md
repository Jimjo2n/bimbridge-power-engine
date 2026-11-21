# Bimbridge 0.7.0-beta1

Goal of beta1: demonstrate **NRT-05**, a real, authorized, reproducible and verifiable persistent write that originates in AgentHost:

`BIM Task IR → AgentHost → MCP Bridge → Add-in → Revit API → Commit → Observe → Verify → Save → Verify on disk → Audit`

## What is new
- `agenthost revit nrt05`: persistent write harness. Requires `--autorizo-persistente` and `--ruta-esperada` (the real path of the disposable .rvt), a local document, not workshared, without pending changes and without a held queue.
- `doc accion=verificar_disco` (add-in): copies the saved .rvt to %TEMP%, opens it in the background as ANOTHER document, re-reads the parameter of the ids and closes it without saving. This proves persistence without closing the active document, which the Revit API does not allow.
- `OpState.DirectiveName`: mapping of the existing states to the execution contract. No parallel enum was created.
- Reproducible build: `build.sh` and `build.ps1` refuse to assemble `dist/` with uncommitted changes and embed `SourceRevisionId = HEAD`.
- Installer: overwrites a previous copy of Setup.exe marked read-only (the alpha3 "Access denied"). If it still fails, the installation is not considered failed and a warning is shown.

## How to run NRT-05 (only on the authorized test instance)
```
agenthost revit instancias
agenthost revit identidad --revit-pid <test PID>
agenthost revit nrt05 --revit-pid <PID> --ruta-esperada "C:\...\NRT05_DynamoSample2024.rvt" --autorizo-persistente ^
  --salida "C:\...\NRT05\run1" --categoria Doors --ids a,b,c --parametro Comments
```
Evidence in `--salida`: `nrt05_informe.json`, `nrt05.log`, `llamadas.jsonl`, `plan_ir.json` and `estado\diario.jsonl`.
Exit codes: 0 = PASSED, 2 = FAILED, 3 = BLOCKED, 1 = plan error.
