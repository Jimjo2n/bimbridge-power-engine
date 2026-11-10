# Línea base 0.6.1

- Punto de partida: tag `v0.6.0` (commit 3163650), rama `release/0.6.1`. `master` sin tocar.
- Pruebas en la base: **193 pasan, 0 fallan**, ~1 min 13 s (net8, Linux, sin Revit).
- Riesgo P0 confirmado en el código de la base (`TaskStore.TryLease`): leer-comprobar-escribir protegido sólo por un `lock` dentro del proceso; dos procesos de Revit podían tomar la misma tarea a la vez. Sin fencing token; `ReleaseLease` borraba `lease.json` (ningún contador monotónico posible). `TaskRunner.RenewLease` ignoraba el resultado de la renovación.
- Auditoría: `Journal` en hilo de fondo; con cola llena o error de disco la línea se perdía sin aviso, incluido el registro de `exec` y `aplicar_cola`.
- Roslyn: el código compilado corría sin ninguna política de capacidades (procesos, red, reflexión, guardar/sync desde una receta).
- Puente: el cliente no comprobaba qué proceso servía el pipe.
