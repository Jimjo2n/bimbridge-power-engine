# BIMBRIDGE 0.9.0 Native Validation: integración y retorno

Estado: **NATIVE_ACCEPTANCE_PENDING**. Build de laboratorio. No usar en modelos de producción.
Instalar, abrir Revit o conectarse a él exige autorización específica de instalación, instancia y copia desechable.

## Requisitos del laboratorio

- Revit cerrado durante la instalación y el retorno.
- CPython real, fuera de WindowsApps. **No se empaqueta**: es un prerrequisito. Se indica en `%APPDATA%\PDI.RevitMcp\config.json`: `"power_engine": { "python": "C:\\ruta\\python.exe" }`. Sin él, las funciones de PDF quedan no disponibles con un diagnóstico.
- Copia local desechable, no workshared.

## Bloqueo por operaciones no reconciliadas

Con evidencia pendiente, un archivo de evidencia dañado o un error al leerla, las escrituras sobre ese documento se rechazan con `documento_bloqueado_reconciliacion`. El bloqueo está en disco y sobrevive a reinicios.

- `job op=pendientes doc=…` muestra el bloqueo.
- `job op=reconciliar plan_id=… doc=…` compara la evidencia con el modelo. Libera sólo con `revertido_verificado` o `aplicado_verificado`, y archiva la evidencia en `reconciliados\`. El plan queda `reconciliado` y no se repite.
- Si no se puede demostrar, una persona revisa el modelo y, con Revit cerrado: `AgentHost plan-store resolver --doc "clave" --plan P-x --autor nombre --motivo "qué revisé" --confirmo-revision-humana`. Para un archivo dañado, `--archivo P-x.json`: va a `cuarentena\`, no se borra.

## Códigos de salida de /quiet

0 bien, 1 error, 2 Revit abierto, 5 ya hay una instalación (repita con `/reemplazar` sólo en un perfil de laboratorio; se crea respaldo).

## Instalar

```
PDI.RevitMcp.Setup.exe /quiet /revit=2024 /niveles=RPW
```

Antes de copiar nada, el instalador verifica el manifiesto y guarda un respaldo en `%LOCALAPPDATA%\PDI.RevitMcp\respaldos\<id>\` si ya había una instalación. Conserva los 3 últimos.

## Volver atrás

```
PDI.RevitMcp.Setup.exe /restaurar
PDI.RevitMcp.Setup.exe /restaurar="<carpeta del respaldo>"
```

- Devuelve el código 2 si Revit está abierto, y no toca nada.
- Verifica los hashes del respaldo antes de tocar nada.
- Restaura el add-in y su `.addin` de cada versión, `bin\` (puente, AgentHost, Power Engine), `config.json`, `install.json`, la copia del instalador y `.codex\config.toml`. Lo que no existía se borra. Los add-ins ajenos, como pyRevit, no se tocan.
- Guarda antes el estado actual e imprime cómo deshacer el retorno. Si falla a medias, intenta dejar el estado anterior.
- `.codex\config.toml` se restaura completo: los cambios hechos en él después del respaldo se pierden.
- Fuera del alcance: las tareas y diarios de `%LOCALAPPDATA%\PDI.RevitMcp` y los modelos.

## Prueba offline

Con `BIMBRIDGE_SETUP_SANDBOX=<carpeta>`, todas las rutas caen dentro de esa carpeta, y no se toca el registro de Windows ni Claude. Sirve para probar instalación y retorno sin afectar a la cuenta.

## Reconciliación

Un plan cuyo resultado no está confirmado queda en estado `desconocido`, no se vuelve a aplicar y conserva su evidencia en `P-<id>.json`, dentro de la carpeta del documento. `PlanStore.LoadPersisted` lo recupera tras reiniciar y `job plan_id=` lo muestra. Revise el modelo antes de repetir.

## Comportamiento de un plan

| Resultado | `estado` | Evidencia |
|---|---|---|
| Ensayo correcto y restaurado | `ensayado` | No queda; el plan sigue listo |
| Fallo funcional, restauración confirmada | `revertido` | No queda |
| Reversión sin confirmar o restauración no comprobada | `desconocido` | Se conserva en disco |
| Interrumpido después de empezar a escribir | `desconocido` | Se conserva; no vuelve a listo |
