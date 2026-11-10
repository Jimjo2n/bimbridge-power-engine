# Aceptación nativa 0.6.0: procedimiento (pendiente de ejecutar)

Nada de esta lista se ha corrido dentro de Revit. Se ejecuta en **Revit 2024 y Revit 2026**, cada uno con una **copia de práctica**
(nunca el modelo de producción ni el central). La IA puede ser Claude o Codex; los pasos 4 y 5 usan las dos.
Cada paso dice qué escribir (perfil compacto) y qué cuenta como **aprobado**. Si algo falla, se copia la respuesta exacta en
`evidencia_060\notas.md` y se sigue con el siguiente paso. Al final: `recoger_evidencia.ps1` (paso 9).

Notación: `<X>` = valor que se anota durante la prueba. Carpeta de trabajo sugerida: `%USERPROFILE%\Documents\PDI_practica`.
`<paquete>` = carpeta donde se descomprimió el zip de la entrega (contiene `aceptacion\` y `bench_060\`); `archivo=` necesita ruta completa (admite `%VARIABLES%`).

## 0. Instalación y diagnóstico (E)
1. Descomprimir `PDI.RevitMcp.Setup_0.6.0.zip`, comprobar el SHA-256 del .exe contra `MANIFIESTO_0.6.0.txt`, instalar marcando 2024 y 2026.
2. `PDI.RevitMcp.Setup.exe /diagnostico` (en una consola). **Aprobado:** `instalada 0.6.0`, `coinciden: true` en 2024 y 2026, `reinicio_pendiente: false`, `config.levels` y `runner` iguales a los que había antes (modo directo conservado).
3. Abrir Revit, `revit "ctx"`. **Aprobado:** `addin 0.6.0`, y el diagnóstico repetido muestra `revit_abiertos[].coincide_con_instalada: true`.
4. Repetir 2 con Revit abierto tras instalar encima (botón Versión): **aprobado** si dice `reinicio_pendiente: true` hasta reiniciar Revit, y `false` después.

## 1. Copia de práctica
1. Abrir el modelo de práctica. `act "guardar_como como=%USERPROFILE%\Documents\PDI_practica\practica_060_<año>.rvt sobrescribir=si"` (si es central: desvincular al abrir).
2. `revit "docs"`: anotar el handle `<dP>` de la copia. Todas las órdenes siguientes llevan `doc=<dP>`.

## 2. Tarea representativa: etiquetar luces + guardar + PDF (B, C, §10)
1. Elegir un RCP de la copia con luminarias **sin** etiquetas (o borrar las de prueba antes): anotar `<vista>`, número de una hoja que lo contenga `<hoja>`.
2. `act "tarea iniciar receta=etiquetar_luces id=acept-060 doc=<dP> args={\"vista\":<vista>,\"pdf_hojas\":[\"<hoja>\"],\"pdf_carpeta\":\"%USERPROFILE%\\Documents\\PDI_practica\",\"pdf_nombre\":\"acept_060\"}"`
3. **Mientras corre** (estado `ejecutando`), cerrar la conversación de la IA (no Revit). Abrir otra conversación **con el otro cliente** (Codex si empezó Claude).
4. Cliente 2: `revit "tarea resumen acept-060"`. **Aprobado:** ≤ 1 500 caracteres, dice receta, documento, progreso n/total, qué falta y siguiente acción; no hace falta reenviar código.
5. Antes de que termine, **cerrar Revit desde el Administrador de tareas** (simula caída). Reabrir la copia.
6. Cliente 2: `act "tarea reanudar acept-060"` y luego `revit "tarea esperar acept-060 max=120"` hasta estado final.
   **Aprobado:** estado `aplicada`; `qc_final.desviaciones` vacío (cada luminaria con **exactamente una** etiqueta en la vista); artefacto PDF con `sha256`, `bytes`, `paginas` (orientativo) y `nuevo: true`; evidencia de `guardado` con hash.
7. Verificación independiente: `revit "chk lighting_audit view=<vista> tags=uno"` → 0 sin etiqueta, 0 duplicadas.
8. Repetir 2 con el **mismo id**: **aprobado** si responde `ya_existia` sin crear nada. Con otro id: todas las unidades `omitida` ("ya tenía etiqueta").

## 3. Pausa, documento equivocado y conflicto (A)
1. Abrir un segundo modelo de práctica `<dQ>` y dejarlo **activo**. Iniciar `act "tarea iniciar receta=parametro_por_lote id=acept-doc doc=<dP> args={\"conjunto\":\"luces\",\"parametro\":\"Comments\",\"valor\":\"PDI-060\"}"` (antes: `exec codigo="ctx.SaveSet(\"luces\", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType().ToElementIds()); return 1;" doc=<dP>`).
   **Aprobado:** sólo cambia `<dP>`; `<dQ>` sigue `IsModified=false`.
2. Durante la tarea pulsar **Retener** en la cinta: estado `esperando_contexto`; al pulsar **En vivo** continúa sola. Sin unidades duplicadas.
3. Cerrar `<dP>` durante la tarea: `esperando_contexto` ("documento cerrado"); reabrirlo: continúa. **No** escribe en `<dQ>`.

## 4. Familia RFA + proyecto (escenario 1)
Pasos 2.1–2.7 de `PROCEDIMIENTO.md` (lavamanos) sobre la copia. Además: `act "abrir familia=<lavamanos> doc=<dP>"` dos veces devuelve el mismo `dN` con `procedencia`; abrir un RFA con el mismo nombre desde otro proyecto **no** se reutiliza (`no_reutilizados`).
**Aprobado:** guardado del RFA y guardado del RVT con hashes distintos de los anteriores; tras cerrar/reabrir el RVT, un solo `fria` y un solo `caliente`.

## 5. QC contra manifiesto con desviaciones conocidas (D)
1. `exec archivo=<paquete>\aceptacion\manifiesto_desde_modelo_060.cs doc=<dP> args={\"nivel\":\"<nivel>\",\"vista_etiquetas\":<vista>}` → ruta del manifiesto.
2. `act "tarea iniciar receta=qc_manifiesto id=acept-qc0 doc=<dP> args.manifiesto=<ruta> args.nivel=<nivel> args.vista_etiquetas=<vista>"` → **aprobado** sin desviaciones.
3. `exec modo=ejecutar archivo=<paquete>\aceptacion\perturbar_copia_060.cs doc=<dP> args={...}` con 5 luminarias distintas (mover, cambiar_tipo + tipo_nuevo, borrar, duplicar, quitar_etiqueta_de + vista).
4. `qc_manifiesto` con id nuevo. **Aprobado:** encuentra `posicion`, `tipo`, `faltante`, `casi_duplicado` (o `sobrante`) y `sin_etiqueta` en las referencias exactas; `aprobado: false`; CSV como artefacto.
5. Ctrl+Z (deshace el paso 3) y repetir 4 con id nuevo: **aprobado** sin desviaciones.
6. Grupos: en la copia, sobre un grupo espejado y su original: `exec codigo="var g = ctx.E<Group>(<id>); return Bim.Grupos.Inspeccionar(g);"` → anotar `miembros_espejados`; `Bim.Grupos.VerificarComposicion(doc, \"<tipo>\")` → `coherente`.
7. Montaje: `act "tarea iniciar receta=montaje_soportes id=acept-mont doc=<dP> args.nivel=<nivel>"`. Revisar en sección 3 luces de cada clase devuelta (al_ras, empotrado, separado). **Aprobado** si la clase coincide con lo visto.
8. Visibilidad: ocultar a mano una luminaria con un filtro y otra con el rango de vista; `exec codigo="return Bim.Vistas.PorQueNoSeVe(ctx.E<View>(<vista>), ctx.E(<id>));"` → **aprobado** si nombra `filtro` y `rango_de_vista` respectivamente.

## 6. Exportación independiente (§8)
`act "tarea iniciar receta=exportar_laminas id=acept-pdf doc=<dP> args={\"hojas\":[\"<hoja>\"],\"antes\":\"guardar\",\"carpeta\":\"%USERPROFILE%\\Documents\\PDI_practica\",\"nombre\":\"acept_pdf\"}"`
**Aprobado:** guardado antes del PDF (orden de eventos en `tarea unidades acept-pdf`), PDF nuevo con hash; cambiar el documento activo a `<dQ>` durante la tarea no cambia la hoja exportada.

## 7. Benchmark MCP vs pyRevit (§9.5)
Carpeta `<paquete>\bench_060` (en `archivo=` con ruta completa). Misma copia, mismos argumentos, Revit recién abierto para la corrida fría:
- Carga 1 familia: `exec modo=ejecutar tx=ninguna archivo=mcp_1_familia.cs` vs `pyrevit_1_familia.py`.
- Carga 2 montaje: `exec archivo=mcp_2_montaje.cs args={\"nivel\":\"<nivel>\"}` vs `pyrevit_2_montaje.py` (NIVEL igual).
- Carga 3 grupos/vistas/PDF: `exec modo=ejecutar tx=ninguna archivo=mcp_3_grupos_vistas_pdf.cs` vs `pyrevit_3_grupos_vistas_pdf.py`.
1 fría + 10 calientes por motor y carga. Anotar cada corrida en `corridas_plantilla.csv` (la IA puede llenarlo con lo que vio); `python resumir_bench.py corridas.csv bench_060.md`.
**Válido** sólo si `resultado_qc` coincide entre motores. `revit "metricas"` da los caracteres reales del lado MCP.

## 8. Prueba prolongada (§9.6)
Repetir el paso 2 con ids nuevos 20 veces (borrando las etiquetas de prueba entre corridas con Ctrl+Z). Anotar memoria de Revit (Administrador de tareas) al inicio y al final, y el tamaño de `%LOCALAPPDATA%\PDI.RevitMcp`. **Aprobado:** sin errores; memoria estable dentro de lo razonable (anotar cifra, no hay umbral fijado).

## 9. Evidencia
`powershell -ExecutionPolicy Bypass -File <paquete>\aceptacion\recoger_evidencia.ps1 -Tareas acept-060,acept-doc,acept-qc0,acept-pdf` → `evidencia_060_<fecha>.zip` en el Escritorio (sin config.json ni credenciales). Enviarlo al hilo del MCP.
