# PDI Revit MCP 0.5.0: informe de entrega

**Commit:** `c87e162` (tag `v0.5.0`), sobre `9271259` (0.4.0). **Fecha:** 18/10/2026.
**Qué está probado:** sólo sin Revit. Compila para Revit 2022–2026, pasan 114 pruebas automáticas, y el script del lavamanos compila contra la API de cada año. **Nada nuevo de 0.5.0 se ha ejecutado todavía dentro de Revit.** Los pasos para hacerlo en el equipo de prueba están en `tools/aceptacion/PROCEDIMIENTO.md`.

## 1. Alcance corregido (F01–F06)

| Hallazgo | Qué cambió | Dónde |
|---|---|---|
| F01 exclusividad | Las instrucciones de los dos perfiles, las guías y la ventana de ayuda ya no dicen "nunca otra vía" ni "la IA nunca envía código". Con la tarea autorizada, la IA aplica sin pedir confirmación en cada lote; se mantienen la pausa del usuario y Retener. | `Bridge/CompactTools.cs`, `Bridge/Program.cs`, `Engine/Guides.cs`, `Addin/Ui/HelpWindow.cs`, `Addin/Revit/RequestHandler.cs` |
| F02 familias rechazadas | Los RFA se aceptan en `exec`, `fam`, `doc` y en 7 operaciones de `plan` (set, values, move, rotate, mirror, copy, delete). `geo` y `chk` responden `no_soportado` con la alternativa, en lugar de un rechazo genérico. | `RequestHandler.cs`, `Geo/GeoRunner.cs`, `LightingAuditRunner.cs` |
| F03 sin runner general | Nueva herramienta `exec`: C# sobre la API pública, compilado con Roslyn 4.8 dentro de Revit (carpeta `roslyn\`, en un contexto de carga propio en 2025–2026). Tiene tres formas de código y dos modos: `consulta` siempre se revierte, `ejecutar` usa `tx=auto`, `grupo` o `ninguna`. Incluye caché por hash, errores con la línea del script, resultado en JSON, cambios por documento, avisos clasificados y una copia auditada de cada script. | `Scripting/Compiler.cs`, `Addin/Scripting/*`, `Engine/ScriptSource.cs`, `Addin/Revit/RequestHandler.Exec.cs` |
| F04 sin flujo de familias | `act`/`fam`: `abrir` (EditFamily, reutiliza el RFA abierto) y `conector`, que crea, adopta o reconcilia por clave semántica y verifica cara, normal, radio, sistema y duplicados. También `param` (valor, fórmula, crear, asociar), `cargar` (IFamilyLoadOptions y relectura de conectores desde el proyecto) y `caras`/`info`. | `Addin/Revit/Families/FamilyOps.cs` |
| F05 documento cambiante | `DocRegistry` asigna handles `dN` por instancia. El documento queda fijado en todos los tramos de un trabajo, y si se cierra la respuesta es `destino_no_disponible`. Los conjuntos quedan atados a su documento. | `Addin/Revit/DocRegistry.cs`, `Engine/Sessions.cs`, todos los runners |
| F06 reconexión | El puente fija la instancia de Revit. Las escrituras no cambian solas de Revit. Las lecturas sólo pasan a un único Revit del mismo año, y la IA recibe un aviso cuando eso ocurre. | `Bridge/RevitConnection.cs` |

También se añadió:
- **Operaciones de documento** (`guardar`, `guardar_como`, `sync`, `pdf`, `cerrar`). Cada una corre como trabajo y se verifica con archivo, fecha, tamaño, SHA-256, páginas del PDF y el evento de sincronización.
- **Diario durable** `operaciones.jsonl` con `clave=` y `tarea=`: una misma clave no se aplica dos veces, una operación interrumpida exige reconciliar, y una clave con otro contenido se rechaza.
- **Respuestas largas por handle:** `res <id> desde=`.
- **Estado por orden** dentro de un lote.
- **Regla de tags por tarea:** `tags=uno|ninguno|ignorar`.
- **Límites:** 200 operaciones y 20 000 entradas por plan.
- **F16:** si la transformación de un vínculo falla, el vínculo se excluye y se avisa; ya no se usa la identidad en silencio.
- **F19:** el instalador no vuelve a encender un MCP apagado y conserva las claves de Codex; el build comprueba los códigos de salida.

## 2. Herramientas (esquema real)

Volcado de `tools/list` del puente compilado: `docs/ESQUEMA_HERRAMIENTAS_compacto.json` y `docs/ESQUEMA_HERRAMIENTAS_completo.json`.

| Perfil | 0.4.0 | 0.5.0 |
|---|---|---|
| Compacto (por defecto) | 3 herramientas, 2 356 caracteres (~590 tokens) | 5 herramientas (`revit`, `plan`, `commit`, `exec`, `act`), 5 810 caracteres (~1 450 tokens) |
| Completo | 14 herramientas, 15 647 caracteres | 19 herramientas, 20 264 caracteres |

El costo fijo del perfil compacto sube unos 860 tokens por conversación. Es el precio de tener `exec` y `act` siempre disponibles.

## 3. Matriz de capacidades y requisitos

- `docs/MATRIZ_CAPACIDADES.md`: se genera desde `Engine/Capabilities.cs`, y una prueba falla si no coincide. Para cada orden indica si aplica a proyecto o familia, el nivel de permiso y la evidencia real.
- `docs/ESTADO_REQUISITOS_0.5.0.csv`: estado de R001–R071 con evidencia y lo que falta.

| Estado | Requisitos |
|---|---|
| Hechos | 33, la mayoría marcados "sin Revit" |
| Parciales | 10 |
| Sin cambios en esta versión | 16 |
| Pendientes | 12 |

Los diez P0 (R001–R010) tienen código. Faltan R006 (al conjunto le falta generación/revisión) y la prueba automatizada de R007. Ninguno está demostrado en Revit.

## 4. Pruebas (evidencia)

`dotnet test`: **114 pasan, 0 fallan** (antes 92). Las nuevas son:

| Prueba | Qué demuestra |
|---|---|
| `ScriptingTests.Lavamanos_CompilesAgainstEachYearsApi` ×5 | `tools/aceptacion/lavamanos_exec.cs` compila contra RevitAPI/RevitAPIUI 2022, 2023, 2024, 2025 y 2026 y contra el add-in de cada año |
| `ScriptingTests.ExpressionAndBody_Compile` ×5, `Errors_PointToTheUsersLine`, `Wrap_*` | las tres formas de código; el error señala la línea del usuario |
| `DurabilityTests` (7) | diario: no repite tras reinicio, interrumpido → reconciliar, otra huella → rechazo; resultados por handle; conjuntos por documento; matriz = código; regla de tags |
| `EndToEnd...ExecAndAct_PassArgumentsAndStopAfterFailure` | el puente entrega `codigo/modo/tx/doc/args/clave/tarea` tal cual; `act` se detiene tras un fallo y lo marca; `revit fam` no escribe |
| `SetupTests.Update_KeepsMcpOff_AndCodexExtraKeys` | F19/A31 |

Estas pruebas encontraron y corrigieron dos fallos reales:
- `ctx.ToMm()` era estático y no compilaba como lo escribiría cualquiera.
- Un DLL sin manifiesto entre las referencias rompía toda compilación (CS1509).

Una tercera corrección salió de revisar el código sin prueba posible aquí. En Revit 2022–2024 el script compilado no habría encontrado el add-in, que Revit carga con LoadFrom; ahora se resuelve contra lo ya cargado. Esto queda sin probar hasta correrlo en Revit.

## 5. Mediciones (no son un benchmark equivalente)

Compilación del script del lavamanos medida en este contenedor (Xeon de 2,8 GHz, 4 núcleos), sin Revit:

| Año | Primera | Siguientes (mediana de 7) |
|---|---|---|
| 2022 | 2 697 ms (incluye cargar Roslyn) | 128 ms |
| 2023 | 231 ms | 140 ms |
| 2024 | 290 ms | 124 ms |
| 2025 | 524 ms | 89 ms |
| 2026 | 227 ms | 107 ms |

Dentro de Revit, la caché por hash hace que repetir un script no lo vuelva a compilar.

**No hay comparación equivalente con pyRevit (R068/R069).** Hace falta correr el mismo modelo y la misma operación con los dos motores y contrastar el QC; está en el punto 5 del procedimiento. Las cifras de 0.2.x frente a pyRevit eran de consultas, no de familias.

## 6. Entrega

En `/mnt/project-files/mcp-revit/0.5.0/`:
- `PDI.RevitMcp.Setup_0.5.0.zip` contiene el instalador `PDI.RevitMcp.Setup.exe`, con add-in 2022–2026 + `roslyn\` + puente. El entorno histórico utilizaba 0.4.0 en Revit 2024; Revit 2026 estaba fuera del alcance de esa validación.
- `PDI.RevitMcp_codigo_0.5.0_c87e162.zip` es el código fuente exacto del commit. El binario lleva `0.5.0+c87e162…`.
- `MANIFIESTO_0.5.0.txt` trae el SHA-256 de los zips, del instalador y de cada archivo instalado.

## 7. Límites que quedan

1. **Nada de 0.5.0 se ha ejecutado en Revit.** Faltan el lavamanos, el trabajo largo, la recuperación y el benchmark equivalente.
2. `exec` corre en el hilo de Revit: un script largo bloquea Revit mientras dura. No hay cesión del hilo entre unidades (R019) ni planificador con barreras por documento (R020).
3. No hay adaptador Python ni helpers de pyRevit (R013): `exec` es sólo C#.
4. Los conjuntos siguen siendo por conversación y se pierden al reconectar. Los jobs y planes con `tarea=` sí se recuperan (R023 parcial).
5. No hay checkpoints dentro de un trabajo largo (R017/R018). La recuperación es por operación con `clave=`.
6. Sin cambios en esta versión: grupos (R031–R034), alojamientos y orientación (R035–R040), vistas, plantillas y cotas (R046–R051) y fuentes PDF (R064–R066). `exec` puede hacerlo caso por caso, pero no hay operaciones tipadas ni QC específico.
7. **Lavamanos:**
   - el frente se toma como −Y de la familia (vista Front); si no lo es, se pasa `frente=`;
   - el flujo In o Bidirectional sigue sin decidir y no se cambia hasta que el operador lo diga.
8. Se compila con los paquetes de referencia de Nice3point, no con las DLL instaladas. `build.ps1 -RevitRoot` permite compilar contra las reales en la PC.
9. En Revit 2022–2024 conviven en el mismo dominio Roslyn y sus dependencias (System.Memory, Immutable…). Si otro add-in carga versiones distintas, puede haber choque; es un riesgo sin probar.
