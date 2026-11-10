# Campaña nativa 0.9.0 — procedimiento

**Estado: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION.** La campaña requiere una instancia de prueba aislada y modelos desechables. No existe evidencia nativa de esta revisión. Los resultados simulados de la suite no son evidencia nativa.

## Antes de empezar (cuando haya autorización)

1. Una **copia desechable LOCAL**, no workshared, de un modelo público o sintético autorizado, con ejes rectos con nombre y una luminaria `OneLevelBased` cargada. Nunca SAMPLE PROJECT ni ACC/BIM 360.
2. Instalar 0.9.0 a mano (`PDI.RevitMcp.Setup.exe`); la instalación es una operación del usuario, separada de las pruebas automáticas.
3. `config.json`: niveles `RPW`, `perfil: laboratorio` con la carpeta de la copia en `laboratorio.carpetas`, `carpetas_documentos` con la carpeta del PDF de prueba.
4. PDF de prueba: uno sintético de `workers/python/tests/fixtures.py` (o un plano público), con ejes cuyos nombres existan en la copia.
5. `PDI.RevitMcp.AgentHost power-engine estado` → Python y Rust disponibles (o la degradación esperada, anotada).

## Pasos

| Ítem | Qué prueba | Cómo |
|---|---|---|
| N9-01 | Identidad, versión 0.9.0, documento fijado | `revit campana080 --revit-pid N --ruta-esperada <copia.rvt> --pasos N9-01.json --salida DIR` |
| N9-02 | Ejes del modelo (sólo lectura) | `--pasos N9-02.json --var codigo_ejes=@tools\aceptacion\0.9.0\ejes_modelo_090.cs`; el artefacto `ejes_modelo_090.json` es la entrada del flujo |
| — | Flujo PDF → ops (sin Revit) | `power-engine flujo-pdf --pdf plano.pdf --nivel "Level 1" --reglas reglas.json --ejes ejes_modelo_090.json --salida DIR\flujo`; revisar `plan.json` (registro `ok`, residuos, ambiguos) |
| N9-03 | Ensayo: aplica, verifica y revierte | `--pasos N9-03.json --var ops=@DIR\flujo\ops.json --autorizo-persistente`; el recuento final debe igualar el de N9-01 |
| N9-04 | Colocación persistente y recuento | `--pasos N9-04.json --var ops=@DIR\flujo\ops.json --var nivel="Level 1" --autorizo-persistente`; recuento = antes + colocados del plan |
| — | QA/QC independiente | Leer lo colocado (`revit q LF lvl=… g=type,x,y`) y verificar contra una fuente distinta del PDF (tabla de verdad del fixture); `no_independiente` si se usa el mismo PDF |
| N9-05 | Red de conduit en tres capas | `--pasos N9-05.json --var codigo_conductos=@tools\aceptacion\0.9.0\conductos_090.cs`; después `power red ruta=<conductos_090.json>` (copiado a una carpeta de documentos) |
| N9-06 | Etiquetas de lo colocado | `--pasos N9-06.json --var ids_colocados=… --var vista=<RCP> --autorizo-persistente` |
| B-PE | Benchmark en el PC | `power-engine bench --docx … --pdf … --salida bench_pc.json` y `power-engine calibrar --salida bin\power_engine\calibracion.json` |
| B1–B8 | BIMBRIDGE frente a pyRevit | **DEFERRED**: pyRevit está restringido en BIMBRIDGE; sólo con autorización expresa y en la copia |

## Criterios

- Ningún paso escribe fuera de la copia; el documento fijado no cambia entre pasos.
- N9-03 deja el modelo idéntico; N9-04 coloca exactamente los puntos del plan, sin duplicados.
- Los ambiguos del plan no se colocan.
- Recoger `DIR` completo con `recoger_evidencia.ps1` y adjuntar `bench_pc.json`.
