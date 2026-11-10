# Informe de pruebas 0.6.1-rc3

Este informe sustituye a los anteriores (5d1f2a1 y rc2), que ya no describen el código actual.

| Campo | Valor |
|---|---|
| Commit | el etiquetado `v0.6.1-rc3` |
| Compilación | `bash build.sh` sin errores: add-ins 2022, 2023, 2024, 2025, 2026; puente; instalador (`evidencia/build_rc3.log`) |
| Pruebas | ver la línea `Passed!` de `evidencia/build_rc3.log` (284 esperadas: 241 de rc2 + 31 rc3 + 12 AgentHost) |
| Fallos | 0 |
| Estado nativo (Revit) | **NOT_EXECUTED** |

## Fallos encontrados y corregidos (reproducidos sobre rc2)

`evidencia/repro_rc2.log` — 9 pruebas que afirman el comportamiento correcto, todas fallan en 0222b6e:

| Prueba | Hallazgo |
|---|---|
| P0_Key_GrantSurvivesRebuild, P0_Key_TwoInstancesAgree | clave HMAC releída como texto: concesiones inválidas tras reabrir |
| P0_Recipe_EnumerationNotReachedWithoutGrant | el motor enumeraba (Preparar/MoveNext de la receta) antes de autorizar |
| P1_Audit_LastRecordEditedSameSize_Detected | edición del último registro indetectable |
| P1_Audit_MiddleEditSameSize_DetectedByLiveInstance | edición sin cambio de tamaño ignorada por la instancia viva |
| P1_Audit_QuarantineKeepsPendingIntents | la cuarentena ocultaba intenciones pendientes |
| P1_AuditGate_OutcomeFailure_NotReturnedAsNormal | resultado sin auditar devuelto como normal |
| P1_Print_MissingCurrentPrint_DoesNotWiden, P1_Print_GrantWithoutPrint_DoesNotCoverPrintedDoc | huella ausente ampliaba la concesión |

## Pruebas agregadas

- `Rc3Tests` (31): clave (8 casos, incluye corrupta ×4, ausente con concesiones, 16 hilos ×5 rondas, 6 procesos reales, formato rc2),
  huella (2), frontera de recetas (6 con el motor real y backend que reproduce el del add-in, 1 de la compuerta), analizador y metadatos (2),
  regresión de las 5 recetas instaladas en 2022 y 2026 (1), AuditLog (6), AuditGate (1).
- `AgentHostTests` (12): ids, validador, grafo y ciclos, ejecución, caída después y antes de aplicar, aplicación parcial, verificación fallida,
  carpeta de otro plan, cancelación, rechazo de escrituras con backend real.

## No ejecutado
- Nivel D (dentro de Revit): todo. En particular, `RecipeGate` en el add-in (contador de escrituras con el DocumentTracker real), cola y exec con
  auditoría incompleta, y el diálogo «Autorizar tareas» tras reiniciar Revit.
- `Lease_LockHeldByAnotherHandle_TimesOutExplicitly` sólo afirma en Windows.

## Compatibilidad comprobada
- Compila contra la API de Revit 2022–2026 (net48 y net8.0-windows).
- Clave de concesiones creada por rc2 (Base64 sin cabecera): se lee (prueba `Key_Rc2Format_IsStillAccepted`).
- Auditoría anterior a rc3 (sin cabecera): se acepta; la cabeza empieza en el siguiente registro.
- Tareas 0.6.0 sin lista `requiere`: se asume que escriben (sin cambio).

## Riesgos residuales
Clave en claro bajo el perfil del usuario; auditoría local sin ancla externa; el analizador no es un sandbox; exclusión de documento por PC;
mediciones en Linux. Detalle en RELEASE_GATE_0.6.1.md §B.
