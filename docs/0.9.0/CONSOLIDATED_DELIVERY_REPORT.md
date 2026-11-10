# Entrega consolidada 0.9.0+commit — informe de implementación

Rama: `feat/power-engine-consolidation-0.9.0`. Base: `main` en `efe37385dd8efdf2be1e675c1d765d4a15126761` (importación saneada).
Versión del producto: `0.9.0` + commit (sin nueva numeración). Decisiones: [D45–D50](ARCHITECTURE_DECISIONS.md).

Estados: `PASS`, `PARTIAL`, `NOT_TESTED`, `BLOCKED`. «Nativo» significa dentro de Revit. **No se ejecutó ninguna prueba nativa en esta entrega:** no se usó la instancia activa de Revit ni ningún modelo, y no se instaló nada sobre el add-in en uso.

## 1. Funcionalidad, commit, pruebas y estado

| Issue | Cambio | Commit | Pruebas de regresión | Offline | Nativo |
|---|---|---|---|---|---|
| #21 | Lectura de `clave.bin` tolerante a violaciones de compartición transitorias (ventana de 3 s, sólo HResult 32/33). Otros errores terminan en `GrantKeyException` sin el secreto. La creación de la clave tiene errores tipados. | `5bd2017` | `Rc3Tests`: inicialización concurrente sin abortar el host, violación transitoria (barrera), violación persistente (fail-closed, sin regenerar), acceso denegado sin reintento | PASS (15 pruebas, 10/10 corridas; suite completa en el worktree limpio) | NOT_TESTED |
| #1 | El umbral de inactividad sólo cierra sesiones sin solicitudes en curso. Una conexión rota se cierra siempre. Motivo `inactividad`. Sin subir el timeout global. | `d649162` | `SessionLifecycleTests`: solicitud de 4× el umbral sin latidos entrega su resultado; una sesión ociosa expira; un cliente que desaparece a mitad cierra la sesión y el host sigue aceptando conexiones | PASS (36 pruebas relacionadas, 3/3) | NOT_TESTED |
| #2 | `QuerySnapshotStore`: una lectura del modelo y páginas desde memoria con `cursor=`, ligadas a sesión, documento y revisión. TTL, presupuesto de memoria con LRU, cursores obsoletos rechazados. El puente y el AgentHost usan el cursor. | `42125ed` | `QuerySnapshotTests`: igualdad de filas contra la lectura clásica (5 100 filas, mm y where), vínculo con sesión, documento y revisión, fail-closed, TTL, LRU, límite por sesión | PASS | NOT_TESTED |
| #7 | `JobStore.Run` explícitamente supervisado (devuelve void). CS4014 es error de compilación. Auditadas las 4 llamadas sin await del add-in. | `d6a61c3` | `JobSupervisionTests`: excepción → `desconocido` con motivo; sin resultado → `desconocido`; cancelación; manejador final una sola vez | PASS | NOT_TESTED |
| #3 | Identidad nominal obligatoria para operar (lectura y registro libres). Alias duplicados rechazados. `ctx desregistrar=true`. Config `identidad_obligatoria`. El AgentHost se declara por configuración. | `1e1e8b8` | `AgentIdentityGateTests` (tubería real y registro); pruebas E2E con agente único por conexión | PASS | NOT_TESTED |
| #4 | Correlación agent_id, session_id, request_id, trace_id, job_id y transaction_id por AsyncLocal, en cada línea del diario. `_traza` del cliente y traza por ejecución del AgentHost. | `1e1e8b8` | `AgentIdentityGateTests`: correlación, traza oculta al manejador, job y transacción, huella estable | PASS | NOT_TESTED |
| #9 | Caché de compilación Roslyn con single-flight y contadores. C# dinámico sigue habilitado por defecto; el control «Bloquear inyección de código C#» no cambia. | `5fcf373` | `CompileCacheTests`: concurrencia con barreras (compila una vez), los fallos no se guardan | PASS | NOT_TESTED |
| #5 | Panel: sección «Motor C#» desde memoria; agentes con agent_id y «sólo lectura» con motivo. | `5fcf373` | Compilación del add-in 2022–2026 | PARTIAL (faltan tabla con filtros, historial, cancelación desde el panel y notificaciones de operaciones largas) | NOT_TESTED (prueba visual pendiente de autorización) |
| #6 | Aceptación nativa | — | — | — | BLOCKED: requiere autorización y un modelo desechable |
| #8 | Benchmarks | — | Medición offline de recorridos (sección 3) | PARTIAL | NOT_TESTED |
| Docs | ADR D45–D50, protocolo de identidad, CHANGELOG, este informe | `ff52c87` y el commit de este informe | — | — | — |

## 2. Evidencia de compilación y pruebas

Build reproducible con `build.ps1` en un worktree limpio (HEAD exacto, prioridad baja), commit `1e1e8b890d71941dc118aeef5be476fbd8d971b1`:

| Paso | Resultado |
|---|---|
| Rust (`cargo test --locked --release`) | 13/13 |
| Python worker (`unittest`) | 31 OK, 2 omitidas |
| Add-ins Revit 2022, 2023, 2024 (net48) y 2025, 2026 (net8, ReadyToRun) | compilados contra los paquetes de referencia |
| Suite .NET | **635/635** |
| Instalador | `dist/PDI.RevitMcp.Setup.exe` generado |

El build del commit final se registra en el paquete de auditoría (`SHA256SUMS`, manifiesto y log). En un clon de desarrollo sin `build.ps1`, las pruebas de `ScriptingTests` y las de la librería Rust fallan porque falta compilar los add-ins de todos los años o la DLL nativa. Es un problema de entorno, y `build.ps1` lo resuelve.

## 3. Rendimiento (#2)

- **Antes:** cada página repetía la consulta completa en el hilo de Revit. La lectura completa de comentarios hacía 51 llamadas y 51 recorridos (19 727 ms, frente a 3 232 ms de pyRevit con 1 llamada; dato del issue).
- **Ahora:** 1 recorrido del modelo y 50 páginas desde memoria. Offline, 50 páginas de 100 filas se sirven en mucho menos de 2 s (umbral holgado de la prueba). El dato real depende de la máquina y no se publica como benchmark.
- **Pendiente (NOT_TESTED):** medición nativa equivalente frente a pyRevit (mismo modelo, mismos parámetros, orden alternado, frío y caliente, mediana, p95 con muestra suficiente). No se declara superioridad sin ella.

## 4. Informe al coordinador (sección 21)

1. **Commit inicial real de GitHub:** `efe37385dd8efdf2be1e675c1d765d4a15126761` (`main`). No avanzó durante la entrega.
2. **Cambios locales previos:** el historial original (14 commits hasta `a291cc6`) en una carpeta local aparte, y trabajo de Codex sin confirmar en `bimbridge-codex\src` (rama `chore/github-foundation`: README, LICENSE, CI, docs). Ambos quedaron intactos.
3. **Reconciliación:** clon nuevo del repositorio saneado y rama derivada de `efe3738`. Sólo se transportaron cambios de producto nuevos, escritos y probados sobre el clon. Sin `--allow-unrelated-histories`, sin force-push, sin bundle histórico ni archivos excluidos.
4. **URL de la rama:** https://github.com/Jimjo2n/bimbridge-power-engine/tree/feat/power-engine-consolidation-0.9.0
5. **URL del pull request:** pendiente. Esta sesión no tiene acceso a la API (no hay `gh` y el conector no está autorizado). Se puede abrir desde https://github.com/Jimjo2n/bimbridge-power-engine/compare/main...feat/power-engine-consolidation-0.9.0
6. **Commit final:** el del paquete de auditoría (rama HEAD al cerrar la entrega).
7. **¿Se resolvió la inicialización de claves?** Sí, offline: suite completa en un build limpio, sin abortos. Falta confirmar el CI de Windows en GitHub, que no se pudo consultar.
8. **Causa raíz:** una violación de compartición transitoria de Windows sobre un `clave.bin` recién publicado (otro handle, el indexador o el antivirus), tratada como error fatal. Además, la prueba multihilo lanzaba la excepción en un `Thread` sin capturar, lo que abortaba el host de pruebas.
9. **¿Se resolvió el timeout?** Sí: una sesión con solicitudes en curso no expira por inactividad, sin subir el timeout global.
10. **¿Se eliminó el recorrido repetido?** Sí: con cursor, 1 recorrido por lectura completa.
11. **Mejora de rendimiento:** de 51 a 1 recorridos del modelo. Los ms nativos quedan NOT_TESTED.
12. **Identidad y observabilidad:** alias obligatorio para operar, rechazo de duplicados, desregistro y seis ids de correlación en el diario. En el panel: agent_id, sólo lectura con motivo y la sección Motor C#.
13. **Nuevas funciones BIM completas:** ninguna nueva en esta entrega. Se priorizaron las puertas de calidad P0 y la multiagencia.
14. **Workflows end-to-end que funcionan:** los existentes (plan/commit, consulta, tareas) siguen pasando la suite. PDF→Revit no se amplió.
15. **Pruebas nativas ejecutadas:** ninguna.
16. **Pruebas que no pudieron ejecutarse:** aceptación nativa (#6) en Revit 2022–2026, prueba visual del panel, benchmark nativo frente a pyRevit, CI de GitHub (sin acceso a la API).
17. **CI Windows:** no consultado. El build local equivalente (build.ps1, Windows) pasa.
18. **Regresiones abiertas:** ninguna conocida offline. #5 está incompleto (ver sección 1).
19. **Objetivos del roadmap alcanzados:** las puertas P0 (#21, #1, #2), concurrencia (#7), identidad y correlación (#3, #4) y la caché de compilación (#9).
20. **Para acercarse a 1.0.0:** aceptación nativa autorizada (#6) con modelo desechable; benchmark nativo equivalente (#8); completar el panel (#5: tabla de agentes con filtros, historial, cancelación segura y notificación de operaciones largas); propagar la traza a los workers de Python y Rust; ampliar los workflows BIM y PDF→Revit con escenarios sintéticos verificables; CI de GitHub en verde con el PR.

## 5. Recomendación sobre los issues

- Cerrar tras el CI del PR: #21, #1, #2, #7, #3, #4.
- Mantener abiertos: #5 (PARTIAL), #6 (BLOCKED), #8 (PARTIAL), #9 (mejora entregada; falta validación multiversión nativa).

## 6. Cierre de la consolidación y pendientes para la siguiente versión

Autor y mantenedor: Jose Jimenez ([Jimjo2n](https://github.com/Jimjo2n)). Los commits posteriores a `521f7cf` usan la identidad Git configurada en esta máquina.

### 6.1 Correcciones tras los controles de GitHub en `521f7cf`

- **Repository checks:** faltaba el encabezado `SPDX-License-Identifier: GPL-3.0-only` en cinco archivos de prueba nuevos. Añadido siguiendo la convención existente. Los controles del repositorio (`check_repository.py`, `inspect_history.py`, pruebas del repositorio) pasan en local.
- **Windows distribution, `WriteSafetyTests.IsAlive_ProtectedProcess_DoesNotThrow`:** la prueba suponía que Windows siempre niega abrir el pid 4 (System). Eso depende del entorno: un usuario normal recibe acceso denegado, y `IsAlive` lo trata como «no es nuestro Revit». Un runner elevado puede abrirlo, y entonces el proceso existe y está vivo. El comportamiento de `IsAlive` es correcto; se corrigió el supuesto de la prueba, que ahora exige que nunca lance y que coincida con lo que el sistema permite en ese entorno. **Hipótesis:** se dedujo del código y de la diferencia de entorno, sin poder leer el registro de la ejecución (sin acceso a la API). Se confirma con el resultado del control en GitHub.

### 6.2 Pendientes

| Issue | Estado real | Prioridad | Depende de | Criterio de aceptación |
|---|---|---|---|---|
| #5 panel | PARTIAL. Hecho: resumen, agentes con agent_id y «sólo lectura», sección «Motor C#» desde memoria, rendimiento (mediana, p95 con ≥20 muestras), recuperación y actividad. Falta: tabla de agentes con búsqueda, filtros y ordenamiento; vista detallada e historial por agente; cancelación segura de trabajos desde el panel; control de nuevas autorizaciones; estadísticas de Rust y Python; **notificación de operación larga** (propuesta no implementada: debe indicar agente, operación, documento, tiempo transcurrido y estado comprobado, y explicar que Revit está procesando y conviene dejarlo trabajar, sin afirmar salud ni fin de escritura sin evidencia). | Alta | Estados de sesión y trabajo (hechos en #1/#7) | Prueba funcional del modelo de vista sin Revit, más prueba visual nativa autorizada |
| #6 aceptación nativa | BLOCKED. No se ejecutó nada dentro de Revit. | Alta | Autorización explícita para una instancia y un modelo desechable de prueba | Campaña con commit, versión de Revit, modelo, fecha, resultado y evidencia saneada, Revit 2022–2026 según disponibilidad |
| #8 rendimiento | PARTIAL. Verificado offline: la lectura completa pasa de 51 recorridos del modelo a 1, y las 50 páginas siguientes salen de memoria. Falta medir dentro de Revit. | Alta | #6 | Mismo modelo, parámetros y unidades; orden alternado, frío y caliente; mediana, p95 con muestra suficiente, llamadas, tiempo de API, serialización, memoria; comparación con pyRevit sin digest |
| #9 C# dinámico | Mejora entregada (single-flight). Falta validación multiversión dentro de Revit y telemetría de entrada al dispatcher. | Media | #6 | Compilación fría y caliente medida en Revit 2022–2026 |
| Traza en workers | La traza llega al AgentHost; Python y Rust no la registran. | Media | — | `trace_id` en los diarios de los workers |
| Workflows BIM y PDF→Revit | Sin ampliar en esta entrega. | Media | #6 para ejecución | Escenario sintético reproducible con incertidumbre explícita |
| Scheduler (equidad entre agentes, colas por documento) | No iniciado. | Baja | Perfiles nativos de #8 | Medición que lo justifique |

### 6.3 Cierres propuestos (tras el CI del PR)

#21 (`5bd2017`), #1 (`d649162`), #2 (`42125ed`, con medición nativa pendiente en #8), #7 (`d6a61c3`), #3 y #4 (`1e1e8b8`). Ninguno se cierra sólo por una prueba aislada: se enlaza el commit, la prueba de regresión y el resultado del control en GitHub.

### 6.4 Regresiones

Ninguna descubierta. Los dos fallos de GitHub eran de metadatos y de un supuesto de entorno de una prueba.

### 6.5 Protecciones de la rama

El repositorio es privado. Si GitHub no ofrece reglas de protección de rama en el plan vigente, el procedimiento de revisión mientras tanto es: todo cambio por pull request hacia `main`, controles de GitHub Actions en verde sobre el commit exacto, revisión del mantenedor (CODEOWNERS) y sin push directo a `main`.