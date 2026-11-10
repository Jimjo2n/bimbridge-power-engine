# PDI.RevitMcp 0.7.0-beta1

Objetivo de beta1: demostrar **NRT-05**, una escritura persistente real, autorizada, reproducible y verificable que nace en AgentHost:

`BIM Task IR → AgentHost → MCP Bridge → Add-in → Revit API → Commit → Observe → Verify → Guardar → Verificar en disco → Auditoría`

## Qué hay de nuevo
- `agenthost revit nrt05`: arnés de la escritura persistente. Exige `--autorizo-persistente` y `--ruta-esperada` (la ruta real del .rvt desechable), un documento local, no workshared, sin cambios pendientes y sin cola retenida.
- `doc accion=verificar_disco` (add-in): copia el .rvt guardado a %TEMP%, lo abre en segundo plano como OTRO documento, relee el parámetro de los ids y lo cierra sin guardar. Así se prueba la persistencia sin cerrar el documento activo, algo que la API de Revit no permite.
- `OpState.DirectiveName`: correspondencia de los estados existentes con el contrato de ejecución. No se creó un enum paralelo.
- Build reproducible: `build.sh` y `build.ps1` se niegan a armar `dist/` con cambios sin confirmar y embeben `SourceRevisionId = HEAD`.
- Instalador: sobrescribe una copia anterior de Setup.exe marcada como sólo lectura (el "Acceso denegado" de alpha3). Si aun así falla, la instalación no se da por fallida y se muestra un aviso.

## Cómo correr NRT-05 (sólo en la instancia de prueba autorizada)
```
agenthost revit instancias
agenthost revit identidad --revit-pid <PID de prueba>
agenthost revit nrt05 --revit-pid <PID> --ruta-esperada "C:\...\NRT05_DynamoSample2024.rvt" --autorizo-persistente ^
  --salida "C:\...\NRT05\run1" --categoria Doors --ids a,b,c --parametro Comments
```
Evidencia en `--salida`: `nrt05_informe.json`, `nrt05.log`, `llamadas.jsonl`, `plan_ir.json` y `estado\diario.jsonl`.
Códigos de salida: 0 = APROBADA, 2 = FALLIDA, 3 = BLOQUEADA, 1 = error del plan.
