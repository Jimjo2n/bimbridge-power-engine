# Native acceptance 0.6.0: procedure (not yet run)

Nothing on this list has been run inside Revit. It runs on **Revit 2024 and Revit 2026**, each with a **practice copy**
(never the production model or the central model). The AI can be Claude or Codex; steps 4 and 5 use both.
Each step says what to write (compact profile) and what counts as **passed**. If something fails, copy the exact response into
`evidence_060\notes.md` and continue with the next step. At the end: `collect_evidence.ps1` (step 9).

Notation: `<X>` = value noted during the test. Suggested working folder: `%USERPROFILE%\Documents\BIMBRIDGE_practica`.
`<package>` = folder where the delivery zip was extracted (contains `acceptance\` and `bench_060\`); `archivo=` needs a full path (accepts `%VARIABLES%`).

## 0. Installation and diagnostics (E)
1. Extract `Bimbridge.Setup_0.6.0.zip`, check the SHA-256 of the .exe against `MANIFIESTO_0.6.0.txt`, install selecting 2024 and 2026.
2. `Bimbridge.Setup.exe /diagnostico` (in a console). **Passed:** `instalada 0.6.0`, `coinciden: true` on 2024 and 2026, `reinicio_pendiente: false`, `config.levels` and `runner` equal to what was there before (direct mode kept).
3. Open Revit, `revit "ctx"`. **Passed:** `addin 0.6.0`, and repeating the diagnostics shows `revit_abiertos[].coincide_con_instalada: true`.
4. Repeat 2 with Revit open after installing over it (Version button): **passed** if it says `reinicio_pendiente: true` until Revit restarts, and `false` afterwards.

## 1. Practice copy
1. Open the practice model. `act "guardar_como como=%USERPROFILE%\Documents\BIMBRIDGE_practica\practica_060_<year>.rvt sobrescribir=si"` (if it is a central model: detach when opening).
2. `revit "docs"`: note the handle `<dP>` of the copy. Every following command carries `doc=<dP>`.

## 2. Representative task: tag fixtures + save + PDF (B, C, §10)
1. Pick an RCP of the copy with fixtures **without** tags (or delete the test ones first): note `<view>` and the number of a sheet that contains it `<sheet>`.
2. `act "tarea iniciar receta=etiquetar_luces id=acept-060 doc=<dP> args={\"vista\":<view>,\"pdf_hojas\":[\"<sheet>\"],\"pdf_carpeta\":\"%USERPROFILE%\\Documents\\BIMBRIDGE_practica\",\"pdf_nombre\":\"acept_060\"}"`
3. **While it runs** (status `ejecutando`), close the AI conversation (not Revit). Open another conversation **with the other client** (Codex if Claude started).
4. Client 2: `revit "tarea resumen acept-060"`. **Passed:** ≤ 1,500 characters, states recipe, document, progress n/total, what is left and the next action; no need to resend code.
5. Before it finishes, **close Revit from Task Manager** (simulates a crash). Reopen the copy.
6. Client 2: `act "tarea reanudar acept-060"` and then `revit "tarea esperar acept-060 max=120"` until a final status.
   **Passed:** status `aplicada`; `qc_final.desviaciones` empty (each fixture with **exactly one** tag in the view); PDF artifact with `sha256`, `bytes`, `paginas` (indicative) and `nuevo: true`; `guardado` evidence with hash.
7. Independent verification: `revit "chk lighting_audit view=<view> tags=uno"` → 0 untagged, 0 duplicated.
8. Repeat 2 with the **same id**: **passed** if it answers `ya_existia` without creating anything. With another id: every unit `omitida` ("already tagged").

## 3. Pause, wrong document and conflict (A)
1. Open a second practice model `<dQ>` and leave it **active**. Start `act "tarea iniciar receta=parametro_por_lote id=acept-doc doc=<dP> args={\"conjunto\":\"luces\",\"parametro\":\"Comments\",\"valor\":\"BIMBRIDGE-060\"}"` (before that: `exec codigo="ctx.SaveSet(\"luces\", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType().ToElementIds()); return 1;" doc=<dP>`).
   **Passed:** only `<dP>` changes; `<dQ>` stays `IsModified=false`.
2. During the task press **Hold** on the ribbon: status `esperando_contexto`; pressing **Live** makes it continue by itself. No duplicated units.
3. Close `<dP>` during the task: `esperando_contexto` ("document closed"); reopen it: it continues. It does **not** write to `<dQ>`.

## 4. RFA family + project (scenario 1)
Steps 2.1–2.7 of `PROCEDURE.md` (washbasin) on the copy. Also: `act "abrir familia=<washbasin> doc=<dP>"` twice returns the same `dN` with `procedencia`; opening an RFA with the same name from another project is **not** reused (`no_reutilizados`).
**Passed:** RFA save and RVT save with hashes different from the previous ones; after closing/reopening the RVT, a single `fria` and a single `caliente`.

## 5. QC against a manifest with known deviations (D)
1. `exec archivo=<package>\acceptance\manifest_from_model_060.cs doc=<dP> args={\"nivel\":\"<level>\",\"vista_etiquetas\":<view>}` → path of the manifest.
2. `act "tarea iniciar receta=qc_manifiesto id=acept-qc0 doc=<dP> args.manifiesto=<path> args.nivel=<level> args.vista_etiquetas=<view>"` → **passed** with no deviations.
3. `exec modo=ejecutar archivo=<package>\acceptance\perturb_copy_060.cs doc=<dP> args={...}` with 5 different fixtures (mover, cambiar_tipo + tipo_nuevo, borrar, duplicar, quitar_etiqueta_de + vista).
4. `qc_manifiesto` with a new id. **Passed:** finds `posicion`, `tipo`, `faltante`, `casi_duplicado` (or `sobrante`) and `sin_etiqueta` at the exact references; `aprobado: false`; CSV as an artifact.
5. Ctrl+Z (undoes step 3) and repeat 4 with a new id: **passed** with no deviations.
6. Groups: in the copy, on a mirrored group and its original: `exec codigo="var g = ctx.E<Group>(<id>); return Bim.Grupos.Inspeccionar(g);"` → note `miembros_espejados`; `Bim.Grupos.VerificarComposicion(doc, \"<type>\")` → `coherente`.
7. Mounting: `act "tarea iniciar receta=montaje_soportes id=acept-mont doc=<dP> args.nivel=<level>"`. Check in section 3 fixtures of each returned class (al_ras, empotrado, separado). **Passed** if the class matches what is seen.
8. Visibility: hide one fixture by hand with a filter and another with the view range; `exec codigo="return Bim.Vistas.PorQueNoSeVe(ctx.E<View>(<view>), ctx.E(<id>));"` → **passed** if it names `filtro` and `rango_de_vista` respectively.

## 6. Independent export (§8)
`act "tarea iniciar receta=exportar_laminas id=acept-pdf doc=<dP> args={\"hojas\":[\"<sheet>\"],\"antes\":\"guardar\",\"carpeta\":\"%USERPROFILE%\\Documents\\BIMBRIDGE_practica\",\"nombre\":\"acept_pdf\"}"`
**Passed:** saved before the PDF (event order in `tarea unidades acept-pdf`), new PDF with hash; switching the active document to `<dQ>` during the task does not change the exported sheet.

## 7. MCP vs pyRevit benchmark (§9.5)
Folder `<package>\bench_060` (in `archivo=` with a full path). Same copy, same arguments, freshly opened Revit for the cold run:
- Workload 1 family: `exec modo=ejecutar tx=ninguna archivo=mcp_1_family.cs` vs `pyrevit_1_family.py`.
- Workload 2 mounting: `exec archivo=mcp_2_mounting.cs args={\"nivel\":\"<level>\"}` vs `pyrevit_2_mounting.py` (same NIVEL).
- Workload 3 groups/views/PDF: `exec modo=ejecutar tx=ninguna archivo=mcp_3_groups_views_pdf.cs` vs `pyrevit_3_groups_views_pdf.py`.
1 cold + 10 warm runs per engine and workload. Record each run in `runs_template.csv` (the AI can fill it in with what it saw); `python summarize_bench.py runs.csv bench_060.md`.
**Valid** only if `resultado_qc` matches between engines. `revit "metricas"` gives the real characters on the MCP side.

## 8. Soak test (§9.6)
Repeat step 2 with new ids 20 times (deleting the test tags between runs with Ctrl+Z). Note Revit memory (Task Manager) at the start and at the end, and the size of `%LOCALAPPDATA%\Bimbridge`. **Passed:** no errors; memory stable within reason (note the figure, no threshold is set).

## 9. Evidence
`powershell -ExecutionPolicy Bypass -File <package>\acceptance\collect_evidence.ps1 -Tareas acept-060,acept-doc,acept-qc0,acept-pdf` → `evidence_060_<date>.zip` on the Desktop (without config.json or credentials). Send it to the MCP thread.
