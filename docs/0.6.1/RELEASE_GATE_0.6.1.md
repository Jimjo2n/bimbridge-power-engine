# RELEASE_GATE_0.6.1 (rc3)

Sustituye la evaluación de rc2 (90/100), que **no era reproducible**: la revisión independiente encontró dos defectos P0 y tres P1
que esa matriz no reflejaba. Los nueve hallazgos se reprodujeron sobre el código de rc2 antes de corregirlos
(`evidencia/repro_rc2.log`: 9 de 9 pruebas fallan en 0222b6e; código en `evidencia/ReproRc2.cs.txt`).

## A. Identidad

| Campo | Valor |
|---|---|
| Versión | 0.6.1, candidata `v0.6.1-rc3` |
| Rama | `release/0.6.1` |
| Commit | el que lleva la etiqueta `v0.6.1-rc3` (ver `MANIFIESTO_rc3.txt` del paquete) |
| Base auditada | 0222b6e (`v0.6.1-rc2`), sustituida; nunca instalada |
| Fecha | 2026-10-22 |
| Árbol | limpio en el commit etiquetado |
| Entorno | contenedor Linux, .NET SDK 8; `bash build.sh` compila 2022–2026 contra ensamblados de referencia; log en `evidencia/build_rc3.log` |
| Aceptación nativa | **NOT_EXECUTED** (operator trabaja en Revit; no se tocó Revit ni se instaló nada) |

## B. Matriz (misma escala R01–R10; reevaluada desde cero)

| ID | Tema | Pos. | rc2 | rc3 | Estado |
|---|---|---|---|---|---|
| R01 | Auditoría previa a la modificación | 12 | 10 | 10 | PARTIAL |
| R02 | Integridad y concurrencia de AuditLog | 12 | 12 | 10 | PARTIAL |
| R03 | Autoridad de capacidades independiente | 14 | 12 | 11 | PARTIAL |
| R04 | Seguridad de Roslyn y DLL | 12 | 10 | 9 | PARTIAL |
| R05 | Reconciliación y máquina de estados | 12 | 12 | 12 | PASS |
| R06 | Leases, fencing y exclusión por documento | 12 | 11 | 11 | PARTIAL |
| R07 | Checkpoints | 8 | 8 | 8 | PASS |
| R08 | Compilaciones e integración | 8 | 6 | 6 | PARTIAL |
| R09 | Memoria y rendimiento | 5 | 4 | 4 | PARTIAL |
| R10 | Diseño C# + Rust | 5 | 5 | 5 | PASS |
| **Total** | | **100** | ~~90~~ | **86** | |

La columna rc2 es la que se declaró entonces; con los hallazgos, rc2 habría puntuado bastante menos (R03 en particular: tras reiniciar
Revit ninguna concesión validaba). No se recalcula rc2; se puntúa rc3 sobre lo demostrado.

### R01 — 10/12
- **Corregido (P1-4):** `AuditGate.Apply` ya no devuelve el resultado ordinario si falla el registro del resultado: devuelve el resultado
  BIM marcado `auditoria_incompleta` con el id de la intención, que queda en `PendingIntents`. Nunca afirma reversión. Cola: `ApplyResult.AuditIncomplete`
  + detalle del plan. exec: estado `aplicado_error_resultado` con `auditoria=auditoria_incompleta` y `reconciliar_intencion`.
- **Pruebas:** `Rc3Tests.AuditGate_OutcomeFailsAfterCommit_ResultIsMarkedIncomplete_IntentStaysPending`, `ReleaseGateTests.R01_*`.
- **−2:** el cableado en el add-in (cola, exec) sólo está comprobado compilando.

### R02 — 10/12
- **Corregido (P1-3):** cabeza `auditoria.jsonl.cabeza` (seq, hash y longitud del último registro) escrita bajo el bloqueo tras cada línea;
  `Verify` la compara con la cola (tolera ir un registro por detrás tras una caída). En cada Append se relee el último registro conocido
  (una línea) y se compara la fecha del archivo; si no cuadra, verificación completa. La cuarentena traslada las intenciones pendientes al
  primer registro de la cadena nueva y `PendingIntents` las sigue mostrando.
- **Pruebas:** `Audit_LastRecordEdited_SameSize_IsDetected`, `Audit_MiddleRecordEdited_SameSize_IsDetectedByLiveInstance`,
  `Audit_LastRecordRemoved_IsDetected`, `Audit_HeadOneBehind_AfterCrash_IsTolerated`, `Audit_Quarantine_KeepsPendingIntentsVisible` + R02 previas
  (8 procesos × 50).
- **Modelo de amenazas:** detecta corrupción, cortes y ediciones a mano. **No** resiste a un proceso del mismo usuario que reescriba la cadena y la
  cabeza a la vez (sin secreto fuera de su alcance no hay forma local). Una edición en medio que conserve tamaño **y** fecha sólo se detecta en la
  verificación completa al abrir.
- **−2:** esos dos límites.

### R03 — 11/14
- **Corregido (P0-1):** la clave se guardaba en Base64 y al reabrir se usaban los bytes del texto; ninguna concesión sobrevivía a un reinicio.
  Ahora: formato `PDI-GRANT-KEY v1 base64` (codificación, **no** cifrado), lectura del formato rc2, creación atómica (temporal + Move sin
  sobrescribir; otro proceso que gane la carrera se respeta), clave inválida/truncada → error sin regenerar, clave ausente con concesiones
  firmadas → error sin regenerar. El add-in trata el error como "sin autoridad" (deniega escrituras).
- **Corregido (P1-5):** la huella del documento debe ser idéntica; que falte en un lado ya no amplía el alcance.
- **Pruebas:** 7 de clave (incluye 16 hilos ×5 rondas y 6 procesos reales) + 2 de huella + R03 previas.
- **−3:** clave en claro bajo el perfil del usuario (DPAPI no implementado: requiere Windows y decidir compatibilidad entre equipos/usuarios,
  ver DECISIONES); la frontera del add-in sólo está probada con backend simulado.

### R04 — 9/12
- **Corregido (P0-2):** ningún código de receta corre antes de autorizar:
  1. `tarea iniciar` ya no instancia la receta: Version/Transaccion se leen del código con Roslyn (sólo constantes; si no, se asume que escribe);
  2. el motor comprueba concesiones antes de la reconciliación y la enumeración;
  3. `RecipeGate` envuelve constructor, Preparar, cada MoveNext, Verificar y Postcondicion: autoridad (leases + concesiones) antes de cada fase
     y, en las de lectura, un contador de escrituras del modelo (revisión del tracker + transacción abierta) que debe quedar igual;
  4. la política real (`Transaccion`) se contrasta al instanciar con lo autorizado; si escribe y se autorizó como lectura, no sigue;
  5. abrir transacciones propias, subtransacciones, grupos o `PostCommand` es capacidad `revit_transaccion`, siempre denegada al código autónomo;
     `ModuleInitializer` cuenta como reflexión.
- **Pruebas:** `Recipe_*` (6), `Gate_*`, `Analyzer_*`, `Metadata_*`, `InstalledRecipes_Regression_*` (las 5 recetas, 2022 y 2026).
- **−3:** sigue sin ser un sandbox: lo que no toca el modelo (archivos, CPU) lo limita el analizador, que es una lista de lo prohibido más el
  conjunto cerrado de recetas instaladas; no se implementó una lista de operaciones permitidas. Probado con backend simulado, no en Revit.

### R05 — 12/12, R07 — 8/8, R10 — 5/5
Sin hallazgos nuevos; pruebas previas en verde.

### R06 — 11/12
Sin cambios. −1: exclusión de documento sólo dentro de un PC; un lote validado termina aunque el lease caduque en él.

### R08 — 6/8
`bash build.sh`: add-ins 2022–2026, puente e instalador; suite completa (ver `evidencia/build_rc3.log`). −2: nada cargado en Revit real.

### R09 — 4/5
Coste nuevo medido (Linux): append de auditoría p50 0,30 → 0,88 ms (escritura atómica de la cabeza); intención+resultado p50 1,80 ms;
motor de 1 000 unidades 369,5 → 397,3 ms con protecciones (~7,5 %). −1: medido en Linux; retención de ensamblados Roslyn sin corregir.

## C. Resultado

**86/100 demostrados. Estado: `0.7_DEVELOPMENT_READY`.** Se asigna la categoría de forma conservadora tras una evaluación anterior
sobrevalorada. Supera el umbral de 70: queda autorizado continuar el desarrollo de 0.7.0 (AgentHost con backend simulado, sin permisos de
escritura reales), con la deuda aislada en la sección E.

## D. Producción

**`PRODUCTION_NOT_APPROVED`.** Falta la aceptación nativa (NATIVE_ACCEPTANCE_0.6.1.md), la protección de la clave (DPAPI o equivalente) y la
aprobación de TI para uso corporativo.

## E. Desactivado o restringido
1. Escrituras autónomas sobre modelos reales o compartidos (sólo copias de práctica, y sólo tras aceptación nativa).
2. `exec` de escritura fuera de una sesión «Código confiable» activada a mano.
3. Recetas no instaladas sin `receta_externa`; transacciones propias en código autónomo (`revit_transaccion`).
4. DLL de referencia fuera de la carpeta del add-in o de la lista sha256.
5. Reanudación automática de escrituras dudosas.
6. Guardar, sincronizar o exportar sin concesión para esa barrera.
7. AgentHost contra un backend real con escrituras (lo rechaza `PlanRunner.Load`).
8. Instalación en otros PCs sin TI; workers Rust.

## F. 0.7.0 — empezado en este commit
- Hecho (`src/PDI.RevitMcp.AgentHost`, 12 pruebas): proceso propio; IR esquema 1 versionado; validador (catálogo cerrado, campos no declarables,
  tipos); ids deterministas; grafo con orden determinista y ciclos; backend simulado; estado persistente (`plan.json`, `estado.json`,
  `diario.jsonl`); resumen de una línea; recuperación tras caída antes/después de aplicar, aplicación parcial bloqueada para una persona.
- Siguiente: protección de la clave; aceptación nativa en copia de práctica cuando operator lo indique; adaptador real del AgentHost que traduzca
  operaciones a `tarea iniciar` sólo con recetas instaladas; extracción de requisitos de documentos multipágina con trazabilidad.
