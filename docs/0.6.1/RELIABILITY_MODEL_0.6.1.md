# Modelo de fiabilidad 0.6.1

## Lease y fencing
- Alcance: **una PC**. La exclusión usa un bloqueo exclusivo de archivo (`lease.lock`, `FileShare.None`) en la carpeta de la tarea. En Windows es un bloqueo de compartición del SO; en Linux .NET usa `flock`. Carpetas de tareas en red o sincronizadas (OneDrive) no están soportadas para exclusión.
- `lease.json`: `dueno, instancia, vence, fencing, liberada`. El fencing sube en cada adquisición que no es una renovación del dueño vigente. Liberar marca `liberada` (no borra), así el fencing nunca retrocede.
- Punto de autoridad: antes de cada lote y cada barrera el ejecutor llama a `ValidateOwnership(dueño, fencing)` o, cada TTL/3, `RenewLease(dueño, fencing)`. Cualquier fallo → `LeaseLostException` → tarea `interrumpida`, sin ejecutar más.
- Ventana residual: un lote ya validado que está corriendo en Revit termina aunque el lease venza durante él (no se puede abortar una transacción a medias desde fuera). El lote es acotado por el presupuesto por visita (ms) y el TTL es 60 s, 20× mayor que el presupuesto típico.
- Fallo seguro: archivo de lease ilegible o bloqueo no obtenido en 10 s → error explícito; nunca se trata como libre.

## Ventana commit → diario
Sin cambios de diseño respecto a 0.6.0 (ya cumple "clasificar, nunca re-ejecutar a ciegas"): un `inicio` sin `fin` se verifica (UniqueId creados → Verificar de la receta → inferencia segura por generación y guardado) y lo no demostrable queda `desconocida` hasta `tarea reconciliar`. Las pruebas de inyección de fallos de 0.6.0 siguen en verde.

## Auditoría vs telemetría
- Telemetría (`Journal`): asíncrona, puede perder líneas, ahora las cuenta (`Dropped`, `WriteErrors`).
- Auditoría (`AuditLog`): síncrona y durable; un fallo es visible y, en `exec` de escritura, bloquea la ejecución.

## Guardar / sync / exportar
Sólo como barreras del motor cuando todo lo anterior está confirmado (0.6.0). En 0.6.1 además una receta autónoma no puede llamar `Document.Save/SynchronizeWithCentral/Export/Close` directamente sin concesión explícita.
