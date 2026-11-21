# BIMBRIDGE 0.8.0 native campaign

**Status of every item: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION.** Nothing runs until a new explicit authorization from the operator. Nothing in this campaign has been run in Revit.

## Rules

- Only **disposable, local, non-workshared** copies of a **public** model (for example, an Autodesk sample). Nothing corporate, nothing from ACC/BIM 360, no central model.
- Never on the operator's working Revit: a separate instance, opened by the operator, with the copy; the PID is taken from `revit instancias` and confirmed by the operator.
- Every command carries `BIMBRIDGE_REVIT_NATIVE=AUTORIZADO` and `--revit-disponible` (if missing, the harness prints the blocked status and exits with code 3).
- Write steps require `--autorizo-persistente` and `--ruta-esperada` = the real path of the copy; if the path does not match or the document is workshared, the item stays BLOCKED without sending any step.
- Revit is not saved, synced, closed or restarted except in the items that say so (N8-12 exports; N8-13 repeats NRT-05, which saves the copy).
- Installation: the operator installs `BIMBRIDGE-0.8.0-actualizacion.zip` by hand with **BIMBRIDGE → Developer Tools → Update from ZIP** (unsigned: it will ask for double confirmation) or with the installer. That installation is itself test N8-13c.

## Items

Base command (PowerShell, from the AgentHost folder):

```
$env:BIMBRIDGE_REVIT_NATIVE="AUTORIZADO"
.\Bimbridge.AgentHost.exe revit campana080 --revit-disponible --revit-pid <PID> --ruta-esperada "<C:\...\copy.rvt>" --autorizo-persistente --pasos <N8-xx.json> --salida <C:\...\evidence\N8-xx> [--var name=value ...]
```

| Item | What it demonstrates | Different initial state | Harness | Acceptance criterion |
|---|---|---|---|---|
| N8-01 | Correct document identity | — | `N8-01.json` | context with the authorized path; nonexistent target → `destino_no_disponible`; read only in the pinned document |
| N8-02 | Views and templates | — | `N8-02.json` (`--var nivel=...`) | RCP created, medium detail, Lighting Devices hidden; the view appears in the catalog |
| N8-03 | Element modification | — | `N8-03.json` (`--var ids=a,b,c`) | comment applied and re-read; move and move back without error |
| N8-04 | 2D drafting | — | `N8-04.json`, then `N8-04b.json` | drafting view with line, rectangle, polygon, arc, note and region; 0.001 ft segment rejected before Revit; detail component and dimension in N8-04b |
| N8-05 | Sheets and composition | after N8-04 | `N8-05.json` (`--var vista_lamina=<id>`) | sheet created with the view; set `BB-N8-05 SET` saved with its views verified; repeating it without `reemplazar` is rejected |
| N8-06 | External families | `carpetas_familias` = test folder with a public RFA | `N8-06.json` | search only in the folder; outside path rejected; load with sha-256; family in the catalog |
| N8-07 | Batch operations | — | `N8-07.json` (50–200 ids) + `revit nrt06` + native B0 (below) | one commit per batch, values re-read; NRT-06 11/11 |
| N8-08 | Authorization and rejection | `"permisos": ["leer","modificar_proyecto","interfaz"]`, Revit restarted | `N8-08.json` | dynamic code, sync, export and load file → `rechazado` with its class; modify project → ok |
| N8-09 | Recovery | — | `revit nrt03` and `revit nrt07` (a different copy from N8-13's) | NRT-03 15/15; NRT-07: lost response recorded as uncertain, a single commit, verified by observation |
| N8-10 | Independent validation | doors with seeded `Mark`: two expected, one omitted (D-999), one justified extra | `ejecutar N8-10_plan.json --revit-pid <PID> --escritura lectura` (key and fingerprint from N8-01) | status `diferencias` with D-999 omitted; the justified exception does not count; if `fuente_construccion` = `fuente_id` → `no_independiente`; zero commits |
| N8-11 | Visibility and representation | a view with fixtures | `N8-11.json` (`--var vista=<id>`) | per-view audit and PNG capture without error |
| N8-12 | Deliverables | after N8-05; export allowed | `N8-12.json` (`--var carpeta_pdf=...`) | PDF created; then `DeliveryTrack`: pages = sheets of the set, file sha = receipt sha |
| N8-13 | 0.7.0 core regression | new copy | `revit nrt05`, `revit nrt06`, `revit nrt03` (same parameters as rc1) + installation by ZIP (N8-13c) | same results as rc1 on the 0.8.0 build; auditoria.jsonl with intent and result of each commit (closes H2); no leftover temporary files in `%LOCALAPPDATA%\Bimbridge` |

Native B0 (optional, within N8-07): 586 elements from a copy with at least that many; one by one with the bridge tool versus a 3-batch plan; and the same job with a pyRevit script for the B1–B8 comparison. Until then B0 is MEDIDO_SIMULADO and B1–B8 NO_MEDIDO.

## Evidence returned

Per item: `<item>_informe.json` (steps, statuses, reasons, per-wire metrics), `llamadas.jsonl`. For N8-09/N8-13: the usual NRT reports. At the end, a zip with all the `evidence\N8-*` folders, without the .rvt.

## Exit gate

0.8.0 is not declared passed if a critical item fails (N8-01, N8-03, N8-08, N8-09, N8-10, N8-13), if there is doubt about the document, if a permission can be bypassed, if there are duplicated commits, if an uncertain result is committed without observation, or if any capability is declared native without its report.
