# rc1 native campaign — prepared, NOT run

Status: `BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION`. It only starts when operator explicitly says that Revit is available.

## Environment requirements
- A Revit 2024 instance **dedicated** to the test, separate from the working one. The working instance is neither touched nor connected.
- **Disposable** model:
  - local copy of `DynamoSample_2024.rvt`, not workshared and without ACC or BIM 360;
  - one clean copy per test: `NRT05_*.rvt`, `NRT03_*.rvt` and `NRT06_*.rvt`;
  - IsModified=false on opening, and at least 5 doors with an editable Comments parameter.
- rc1 add-in installed with RPW levels; Hold disabled and queue empty.

## Preflight (each test)
1. `set BIMBRIDGE_REVIT_NATIVE=AUTORIZADO`.
2. `agenthost revit instancias --revit-disponible` shows a single test PID.
3. `agenthost revit identidad --revit-pid N --revit-disponible` must report:
   - the path equal to the disposable copy;
   - worksharing=false and modified=false;
   - the fingerprint present;
   - the add-in version equal to `0.7.0-rc1+<commit>`.

## Tests (one run each; if it fails, it is fixed and repeated without an artificial limit)
| Test | Command | PASSED criterion |
|---|---|---|
| NRT-05 regression | `revit nrt05 --revit-pid N --ruta-esperada <NRT05.rvt> --autorizo-persistente --salida …\nrt05 --categoria Doors --ids a,b,c,d,e --revit-disponible` | 13/13 checks |
| NRT-03 | `revit nrt03 … --ruta-esperada <NRT03.rvt> --autorizo-persistente --salida …\nrt03 --categoria Doors --ids a,b,c --revit-disponible` | In all 5 rounds (antes_enviar, tras_commit, runner.tras_ejecutar, en_verificacion, tras_verificar): it crashed, ended verified and there was exactly 1 commit. The AgentHost "crashes" through an injected exception; Revit is never killed. |
| NRT-06 | `revit nrt06 … --ruta-esperada <NRT06.rvt> --autorizo-persistente --salida …\nrt06 --categoria Doors --ids a,b,c,d,e --revit-disponible` | Must meet all of this: valid plan; rejection without authorization; out-of-scope rejection without a commit; query, batch and set verified; QC run with evidence; exactly 2 commits; correct independent re-read; resume without writing; no save and no sync |

For the NRT-06 QC it is required that it **runs and leaves evidence**, not that it gives 0 deviations: the sample model may have genuinely untagged lighting fixtures.

## Rollback
- NRT-03 and NRT-06 do not save. When finished, the operator closes the test instance **without saving**.
- NRT-05 only saves its disposable copy.
- If anything is left uncertain (`requiere_verificacion` or `requiere_reconciliar`), it is not repeated: it is reported with the journal.

## Evidence manifest (per test, in `--salida`)
- `<test>_informe.json`, `llamadas.jsonl` (ms and bytes per call), `plan_ir.json` and `estado*/diario.jsonl`.
- In NRT-03, one `rondaN` folder per crash point.
- At the end, `SHA256_campaña.txt` with the hash of each file.
