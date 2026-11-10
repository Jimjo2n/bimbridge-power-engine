# Informe de pruebas de 0.7.0-alpha3

## Resumen

| Conjunto | Aprobadas | Fallidas | Bloqueadas | Dónde |
|---|---|---|---|---|
| Suite completa (sin Revit) | 343 | 0 | 0 | `build.sh` en Linux, .NET 8; log en `logs/tests_full.log` y `logs/build.log` |
| Compilación del add-in 2022–2026, Bridge, Setup y AgentHost | 5/5 versiones | 0 | 0 | `logs/build.log` y `logs/publish_agenthost.log` |
| NRT-04 nativo (Revit 2024, copia desechable) | — | — | **PENDIENTE** | Necesita el equipo de prueba y su autorización para instalar alpha3 |
| NRT-03 nativo | — | — | **PENDIENTE (beta2)** | Preparado; la versión simulada pasa |

Antes había 326 pruebas, con una fallida (`Key_ConcurrentInitialisation_ProducesOneKey`, que ya fallaba en 7be12ad). Ahora son 343 y todas pasan.

## Pruebas nuevas (17)

| Prueba | Qué demuestra |
|---|---|
| `AgentHostRevitTests.Trial_FromAgentHost_IsEnsayada_ModelUnchanged_NothingPersisted` | La ruta completa AgentHost → tubería → plan/commit ensayo: estado `ensayada`, modelo idéntico y ningún commit persistente |
| `…WritesNeedOperatorAuthorization_AndMatchingMode` | Sin `RealWrites`, o con un modo distinto, el plan se rechaza antes de tocar nada |
| `…Identity_WrongPid_ReadOnlyLevels_Workshared_AndOtherDocument_AreRefused` | Rechazos por PID ajeno, nivel R, documento workshared y otra huella; nunca se autoselecciona |
| `…TrialThatDoesNotRestore_IsNeverReportedAsEnsayada` | Un ensayo que deja cambios queda en `requiere_reconciliar` |
| `…PlanOutsideScope_IsRejected_BeforeAnyCommit` | Si el add-in planifica ids fuera del alcance, se descarta sin commit |
| `…PersistentCommit_TimesOutAfterApplying_IsObserved_NotRepeated` | Timeout tras aplicar: efecto incierto, se observa, 1 commit y verificada |
| `…Semantics_ConsultaIsRead_EnsayoIsWrite` | Clasificación de `exec` |
| `…Nrt03_AgentHostCrash_AtEachPoint_ResumesWithoutDuplicateCommit` ×5 | Caída del AgentHost antes de enviar, tras un posible commit, tras ejecutar sin registrar, durante la verificación y tras verificar sin registrar. En los 5 casos: 1 commit, verificada y el elemento fuera del alcance intacto |
| `PlanQueueTests` ×3 | Un Enqueue durante la ejecución provoca una sola repetición y nunca ejecuciones concurrentes; 600 encolados en 10 planes con 6 trabajadores sin solapes; cola llena e idempotencia |
| `ChainLogStreamingTests` ×2 | Un diario de unos 40 MB y 40 000 registros se abre en streaming; la corrupción en medio sigue siendo error y la cola cortada se recorta |

Además, `IdentityJournalTests.ExecMode_Aliases_AndSpaces("PRUEBA")` ahora espera `ensayo` en lugar de `consulta`. Es un cambio intencionado y más restrictivo.

## P1: causas raíz

- **A, clave de concesiones.** En Unix, `File.Move(src, dst)` sin sobrescribir comprueba el destino y luego llama a `rename()`, que sí sobrescribe. Dos creadores podían "ganar" y quedar con claves distintas (2 de 16 almacenes en las reproducciones). Corrección: creación bajo un candado exclusivo (`FileShare.None`) y relectura del disco. Resultado: 10 de 10 ejecuciones verdes, incluida la prueba con 6 procesos.
- **B, cola del servicio.** `_queued` se borraba antes de ocupar `_slots`. Un `Enqueue` durante la ejecución lo volvía a encolar; otro trabajador lo sacaba, `TryAdd` fallaba y el plan se descartaba sin aviso. Además, `reanudar` durante una pausa pendiente se ignoraba. Corrección: `PlanQueue` con los estados EnCola, Ejecutando y EjecutandoYRepetir; `reanudar` anula la pausa pendiente.
- **C, memoria del diario.** `ChainLog.Open` cargaba el archivo entero en un `byte[]` (con índices `int`, así que se rompía por encima de 2 GB) y además una lista con todos los registros. Corrección: lectura por bloques de 64 KB con callback y un límite de 16 MB por línea. `DurableSimModel` valida todo en una primera pasada antes de aplicar nada.

## NRT-04: procedimiento (pendiente de ejecutar)

Condiciones previas, en el equipo de prueba y con su autorización:

1. Instalar el add-in de alpha3 en Revit 2024.
2. Abrir una segunda instancia de Revit 2024 con una copia local, desechable y no workshared, por ejemplo DynamoSample_2024.rvt.
3. Comprobar que la sesión de trabajo del operador no se toca.

Pasos:

```
PDI.RevitMcp.AgentHost.exe revit instancias
PDI.RevitMcp.AgentHost.exe revit identidad --revit-pid <PID de la 2.ª instancia>
PDI.RevitMcp.AgentHost.exe revit nrt04 --revit-pid <PID> --salida %USERPROFILE%\Desktop\SAMPLE PROJECT\NRT_alpha3 --categoria <cat> --ids <3 a 5 ids> --parametro Comments --valor NRT04
```

Qué hace el arnés:

- Bloquea la prueba si el documento es workshared o si el PID no coincide.
- Toma una instantánea y su huella SHA-256.
- Comprueba que el plan se rechaza sin la autorización del operador.
- Ejecuta el plan IR (consultar + fijar_parametro) con `RealWrites=ensayo`.
- Toma otra instantánea y la compara.

Verificaciones: `estado_op_ensayada`, `grupo_rolledback_literal`, `verificacion_temporal_addin`, `restauracion_addin`, `huella_identica`, `rev_sin_cambios`, `modificado_sin_cambios`, `mismo_documento`, `sin_guardar_ni_sync` y `no_persistio_como_aplicado`.

Códigos de salida: 0 = APROBADA, 2 = FALLIDA, 3 = BLOQUEADA.

Evidencia: `nrt04_informe.json`, `nrt04.log`, `llamadas.jsonl` (op, ms, bytes, estado, PID, tubería y modo de cada llamada), `plan_ir.json` y `estado/` (diario con cadena de hash). Con eso se correlacionan plan, op, clave, intento, PID, instancia, documento, estado de transacción, verificación y estado del diario.

## Tras la revisión técnica (2026-10-28)

**Historial nativo de NRT-04 (sin omitir runs):**

| Run | Resultado | Nota |
|---|---|---|
| run2 | BLOQUEADA | documento workshared |
| run3 | FALLIDA | bug de `page_size` |
| run4 | FALLIDA, 10/12 | rev e IsModified; el criterio de rev se cambió después |
| run5 | APROBADA, 12/12 | con `--aceptar-workshared-local`; evidencia condicionada |

La evidencia original está en el equipo de prueba: `alpha3\NRT04\NRT04_evidencia_run2-run5.zip` (SHA256 `CFCB5EE071FE7B01FE2C0E711FAD0F06C86F2DC18F4247E3BB383CB97C7AFAFA`) y el índice `EVIDENCIA_run2-run5_SHA256.txt` (37 archivos).

**Pruebas del P0 (6 nuevas, suite 359/359):**
- `TrialGuardTests`:
  - la política pura;
  - la guardia va antes de `PlanOps.Trial` y dentro de `RunMain`;
  - la guardia va antes de `new TransactionGroup` en `exec`;
  - la captura y el QC de tarea no abren grupo si hay cambios sin guardar;
  - ningún código escribe `IsModified`.
- `AgentHostRevitTests.P0_TrialOnModifiedDocument_IsRefusedBeforeAnyChange_AndNotUncertain`: rechazo previo, op `fallida`, sin incertidumbre, `IsModified` y valores intactos.

Las rutas del add-in solo se pueden ejecutar dentro de Revit. **NRT-04N**, la prueba negativa nativa, queda **pendiente** de instalar el add-in corregido.
