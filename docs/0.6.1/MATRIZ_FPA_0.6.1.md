# Matriz fallo → protección → aceptación (FPA) 0.6.1

| Fallo | Protección | Prueba |
|---|---|---|
| Dos Revit toman la misma tarea | bloqueo de archivo + fencing | 20 procesos reales |
| Revit A se cuelga, B toma la tarea, A despierta | A valida fencing antes del lote → interrumpida | LeaseStolen_MidRun |
| `lease.json` corrupto | error explícito, no ejecuta | Lease_CorruptFile |
| Disco lleno / carpeta de auditoría sin permiso | `exec` de escritura rechazado; tarea `parcial` si el diario de unidades falla (0.6.0) | AuditLogTests, DurabilityTests |
| Alguien borra o edita una línea de auditoría | `Verify` lo detecta | AuditLogTests |
| Caída entre commit y diario | verificar, nunca re-ejecutar a ciegas (0.6.0) | TaskEngineTests |
| Receta con `Process.Start`, red, reflexión, `doc.Close` | rechazada en perfil autónomo | HostileCode |
| Receta alterada en la carpeta de la tarea | SHA-256 no coincide → no se reanuda (0.6.0) | TaskEngineTests |
| Otro proceso ocupa el nombre del pipe | PID y ejecutable comprobados | PipeServerCheckTests |
