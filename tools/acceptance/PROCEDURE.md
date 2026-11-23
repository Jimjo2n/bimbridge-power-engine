# Acceptance tests in Revit (0.5.0): not yet run

None of this has been run yet. It runs on the test machine, Revit 2024, **only with the practice model** and a copy of the washbasin RFA.
Each step says what to write to the AI (compact profile) and what counts as passed. If a step fails, note the exact text of the response.

## 0. Preparation
1. Install `Bimbridge.Setup.exe` 0.5.0 (zip `Bimbridge.Setup_0.5.0.zip`). Check the SHA-256 against `MANIFIESTO_0.5.0.txt`.
2. Open Revit 2024 with the practice model. **Live** mode (Hold rejects exec and families with `necesita_usuario`, on purpose).
3. `revit "ctx"`: passed if it shows `addin 0.5.0`, `protocolo 2`, `runner auto`, the `capacidades` list and the compiler path without error.

## 1. General runner (A02)
- `exec codigo="doc.Title"`: returns the title; `modo consulta`; cold compile time noted.
- Repeat the same code: `en_cache` true and `compilar_ms` 0.
- `exec codigo="return noExiste;"`: `error CS0103 l1`.
- `exec modo=ejecutar codigo="var w = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement(); w.LookupParameter(\"Comments\").Set(\"MCP\"); return w.Id;"` → `cambios` includes that id; Ctrl+Z in Revit undoes it in one step.

## 2. Washbasin (A03, A04, A05)
Fixed data: **radius** 6.5 mm (not diameter). Facing the fixture from the front: **cold on the right, hot on the left**. Flow **In** or **Bidirectional** is undecided: do not pass `flujo=` until the operator decides.

1. `revit "fam info familia=<washbasin>"` and `revit "fam caras familia=<washbasin>"`: note existing connectors and pick the two faces (normal and center).
2. `act "abrir familia=<washbasin>"` → handle `dN` of the RFA.
3. Typed path:
   `act "conector nombre=fria sistema=fria cara=<e:n> radio=6.5mm doc=dN clave=lav-fria"`
   `act "conector nombre=caliente sistema=caliente cara=<e:n> radio=6.5mm doc=dN clave=lav-cal"`
   Passed: radius exactly 6.5 mm, system DomesticColdWater / DomesticHotWater, correct face and normal, `pendientes` mentions the flow.
4. General path (same result with exec): `exec modo=ejecutar tx=auto doc=dN args={"cara_fria":"<e:n>","cara_caliente":"<e:n>","radio_mm":6.5}` with the code from `washbasin_exec.cs`. Passed: `n_fria=1`, `n_caliente=1`, `lado_correcto=true` (or `frente=` corrected if the family does not use -Y as its front).
5. `act "guardar doc=dN"` → file with a new hash. `act "cargar en=d1 doc=dN sobrescribir=valores"` → re-read from the project with both connectors.
6. **Repeat 3–5** (same keys, then new keys): no duplicate connector appears; repeated keys return the previous result.
7. Close Revit without saving the project and reopen: `fam info` still shows a single `fria` and a single `caliente`.

## 3. Long job (A10, A13–A15, A24)
Only after the operator confirms the scope: fixtures + groups + views in the practice model, in plans of ≤200 operations with `tarea=practica-larga` and `clave=` per batch; at the end `act "pdf conjunto=\"NON-UNIT LIGHTING\""`. Passed: new PDF (date, hash and pages verified), `chk lighting_audit tags=uno` with no differences in the requested scope.

## 4. Recovery (A09, A11)
1. Start a large commit with `clave=rec-1 tarea=practica-larga` and close the conversation before the response.
2. New conversation: `revit "job tarea=practica-larga"` shows the job; repeat the commit with `clave=rec-1` → `ya_aplicado` (it is not applied twice).
3. Close Revit in the middle of an exec with `clave=rec-2`; reopen and repeat → `interrumpido`; after review, `reconciliar=si`.

## 5. Comparison with pyRevit (R068/R069)
Same model, same operation (washbasin and a lighting audit), 1 cold run and 10 warm runs with each engine; note p50 and p95 and whether the final QC matches. Without this run there is no equivalent benchmark.
