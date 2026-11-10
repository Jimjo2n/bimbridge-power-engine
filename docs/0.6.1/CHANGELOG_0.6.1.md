# Cambios 0.6.1 (desde v0.6.0)

Comprobado: `prueba` = prueba automática sin Revit; `procesos` = prueba con procesos del SO reales; `compila` = compila contra la API 2022–2026; `nativo` = dentro de Revit. **Nada de 0.6.1 es `nativo`.**

| Cambio | Comprobado |
|---|---|
| Lease entre procesos: `lease.lock` abierto con `FileShare.None` alrededor de leer-comprobar-escribir (misma PC) | procesos (20 concurrentes → 1 ganador) |
| Fencing token monotónico en `lease.json`; renovación lo conserva; otra adquisición lo sube; liberar no borra el archivo | procesos + prueba |
| `RenewLease`, `ValidateOwnership`, `GetCurrentLease`; `lease.json` antiguo sin campo = fencing 0 | prueba |
| Almacén de leases corrupto o bloqueo no obtenido → error explícito, nunca "libre" | prueba |
| El ejecutor valida dueño+fencing antes de cada lote y barrera; renovación fallida → `interrumpida` ("lease perdido") sin ejecutar más | prueba (robo de lease a mitad: 0 lotes después) |
| `AuditLog`: auditoría síncrona (WriteThrough+Flush), encadenada por sha256, `Verify` detecta líneas borradas/alteradas/cortadas | prueba |
| `exec`: se audita ANTES de ejecutar; si la auditoría falla, el código que escribe se rechaza (fail closed). `aplicar_cola` también va a auditoría | compila |
| Telemetría (`Journal`) cuenta líneas perdidas y errores en vez de callarlos | prueba |
| Análisis semántico de capacidades en Roslyn (`CompileWithCapabilities`) | prueba (7 patrones hostiles) |
| Perfiles `TrustedInteractive` / `RestrictedAutonomous`; tareas y recetas rechazan capacidades no concedidas al iniciar y al reanudar; concesiones (`capacidades=`) entran en la huella | prueba (política) / compila (cableado) |
| Puente: el servidor del pipe debe ser el PID de Revit anunciado y el ejecutable Revit | prueba (regla) / compila (Win32) |
| Versión 0.6.1 | — |

## rc3 (revisión crítica de rc2)

| Cambio | Comprobado |
|---|---|
| Clave de concesiones: formato v1, lectura rc2, creación atómica, inválida/ausente sin regenerar | prueba + procesos |
| Huella de documento estricta en concesiones | prueba |
| Recetas: sin instanciar en `tarea iniciar`; `RecipeGate` en constructor/Preparar/MoveNext/Verificar/Postcondicion; autorización previa en el motor | prueba (backend simulado) + compila |
| `revit_transaccion` (Transaction/SubTransaction/TransactionGroup/PostCommand) siempre denegada al código autónomo | prueba |
| AuditLog: cabeza, relectura del último registro, fecha, cuarentena con intenciones pendientes | prueba |
| AuditGate: resultado `auditoria_incompleta`; exec `aplicado_error_resultado` | prueba + compila |
| AgentHost 0.7.0 (inicio) con backend simulado | prueba |
