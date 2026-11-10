# Aceptación nativa 0.6.1 (nivel D)

**Estado: NOT_EXECUTED.** No se ha ejecutado nada de 0.6.1 dentro de Revit. El entorno de validación de esta revisión no incluía una instancia de Revit aislada.

Procedimiento (copias de práctica, nunca SAMPLE PROJECT de producción ni centrales), cuando operator lo autorice:

| Id | Paso | Criterio de aprobado | Estado |
|---|---|---|---|
| D-01 | Instalar 0.6.1 en Revit 2024 y 2026; `Setup.exe /diagnostico` | versión 0.6.1 cargada tras reiniciar; hashes OK | NOT_EXECUTED |
| D-02 | `exec` de lectura y de escritura en copia | ambas en `auditoria.jsonl`; `Verify` OK | NOT_EXECUTED |
| D-03 | Hacer la carpeta `auditoria` de sólo lectura y pedir un `exec` que escribe | rechazado con "auditoría no disponible"; uno de consulta sí corre | NOT_EXECUTED |
| D-04 | `tarea iniciar etiquetar_luces` (procedimiento de 0.6.0) | aplicada; política sin denegaciones | NOT_EXECUTED |
| D-05 | Receta de prueba con `System.Diagnostics.Process.Start` | `tarea iniciar` la rechaza con la lista de capacidades | NOT_EXECUTED |
| D-06 | Dos Revit con la misma copia, `tarea reanudar` en ambos a la vez | sólo uno ejecuta; el otro informa quién la tiene; fencing sube al tomarla el segundo | NOT_EXECUTED |
| D-07 | Conectar el puente con dos Revit abiertos | se conecta al PID anunciado; sin errores de identidad | NOT_EXECUTED |
| D-08 | Volver a 0.6.0 con el botón "Versión" | 0.6.0 carga; tareas con `lease.json` nuevo siguen legibles | NOT_EXECUTED |

Evidencia: `aceptacion\recoger_evidencia.ps1` de 0.6.0 + `auditoria\auditoria.jsonl`.
