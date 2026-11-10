# PDI.RevitMcp 0.7.0-alpha3

Esta versión conecta el AgentHost con un Revit real a través de la cadena tipada que ya existía, sin crear un segundo motor de transacciones:

`BIM Task IR → AgentHost (PlanRunner) → RevitBackend → RevitConnection (IPC tipado del Bridge) → Add-in (plan/commit) → Revit API → verificación → auditoría`

## Qué hay de nuevo

- **Lectura y escritura separadas en `exec`.**
  - `modo=consulta` se ejecuta sin transacción, así que Revit rechaza cualquier cambio.
  - El nuevo `modo=ensayo` envuelve el script en un grupo con transacción que siempre se revierte. Exige nivel W y se clasifica como escritura.
- **Ensayo tipado reversible (`commit modo=ensayo`).**
  - Usa el mismo PlanApplier: aplica, verifica dentro del grupo, hace `RollBack()` y guarda el estado literal de la transacción.
  - Después relee cada valor original y comprueba que no persiste ningún elemento creado.
  - El plan no se consume.
- **AgentHost con backend real (`RevitBackend`).**
  - Fija una instancia por PID y nunca elige una sesión por su cuenta.
  - Hace handshake, comprueba el PID del servidor de la tubería y la identidad del documento (`clave_doc`, `huella_doc`) y aplica timeouts.
  - Un timeout de escritura se trata como efecto incierto: se observa el modelo y nunca se repite a ciegas.
- **Catálogo limitado a `consultar` y `fijar_parametro`** (con `unidades=texto` y como máximo 30 objetivos). Las verificaciones QC no se ofrecen porque aún no se pueden demostrar en nativo.
- **Las escrituras reales necesitan la autorización del operador.** `--escritura ensayo|persistente` se fija en la línea de comandos y debe coincidir con el modo del backend; la petición del agente no basta.
- **Arnés NRT-04:** `PDI.RevitMcp.AgentHost.exe revit nrt04 ...`, descrito en `TEST_REPORT_alpha3.md`.
- **Correcciones P1:** la carrera de la clave de concesiones, la cola del servicio y la memoria al abrir diarios grandes.
- **NRT-03 preparado sin Revit:** caídas del AgentHost en 5 puntos críticos, nunca de Revit.

## Comandos

```
PDI.RevitMcp.AgentHost.exe revit instancias
PDI.RevitMcp.AgentHost.exe revit identidad --revit-pid 45440
PDI.RevitMcp.AgentHost.exe revit nrt04 --revit-pid 45440 --salida C:\...\NRT04 --categoria Doors --ids 1,2,3 --parametro Comments --valor NRT04
PDI.RevitMcp.AgentHost.exe ejecutar --plan p.json --dir estado --revit-pid 45440 --escritura ensayo --llamadas llamadas.jsonl
```

`persistente` sólo se acepta sobre un documento local, que no sea workshared, ni de sólo lectura, ni de la nube, y sin cola Retener. No se usa en alpha3.
