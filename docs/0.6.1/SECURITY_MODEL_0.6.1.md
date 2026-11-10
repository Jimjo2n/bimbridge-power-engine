# Modelo de seguridad 0.6.1

## Límite honesto
El código C# que compila el MCP corre **dentro del proceso de Revit con los permisos del usuario**. .NET (Framework 4.8 y 8) no tiene sandbox en proceso. Lo que 0.6.1 añade es una **puerta de política** antes de ejecutar, más auditoría durable; no es aislamiento. Un aislamiento real exigiría un proceso aparte sin acceso a la API de Revit (ver ROADMAP_0.7.0).

## Perfiles
| Perfil | Cuándo | Qué permite |
|---|---|---|
| TrustedInteractive | `exec` en una sesión con una persona (cola Retener/Aplicar disponible) | todo, auditado de forma durable antes de correr; si la auditoría falla, sólo consultas |
| RestrictedAutonomous | `tarea iniciar` / `tarea reanudar` (recetas que corren solas) | API de Revit dentro de la transacción del motor, `ctx.Artefacto`, barreras del motor |

En autónomo **siempre se deniega**: proceso, red, reflexión (incl. `dynamic`, `Activator`, `Type.GetType`, `Invoke`), código nativo (`extern`, `unsafe`, `Marshal`), registro, salida del proceso, variables de entorno, hilos, cerrar/abrir documentos.
**Concedible** con `capacidades=[...]` (queda en la huella de la tarea): `archivo_escritura`, `revit_guardar`, `revit_sync`, `revit_exportar`. Sin concesión, guardar/sync/PDF sólo por `Unidad.Guardar/Sync/Pdf`, que el motor ejecuta como barreras verificadas.

La política se aplica dos veces: al iniciar y al reanudar (la receta se recompila desde la copia de la tarea y su SHA-256 se comprueba antes).

## Integridad vs autenticidad de recetas
- Integridad: la tarea guarda la copia exacta y su SHA-256; reanudar con una copia alterada se rechaza (desde 0.6.0).
- Autenticidad: **no hay firma**. Cualquiera con escritura en `%LOCALAPPDATA%\PDI.RevitMcp\recetas` puede poner una receta. Mitigación 0.6.1: la política de capacidades limita qué puede hacer. Firma de recetas: ROADMAP_0.7.0.

## Puente / pipe
- Pipe sólo para el usuario actual (ACL en 2022–2024, `CurrentUserOnly` en 2025–2026), sin red; trama máx. 16 MB (desde antes).
- Nuevo: el cliente comprueba con `GetNamedPipeServerProcessId` que el servidor es el PID anunciado y que el ejecutable es Revit (evita que otro proceso ocupe el nombre).

## Auditoría
`%LOCALAPPDATA%\PDI.RevitMcp\auditoria\auditoria.jsonl`: una línea por `exec_codigo` (con sha256 del código y ruta de la copia completa) y por `aplicar_cola`. Cadena `prev` = sha256 de la línea anterior. `AuditLog.Verify` detecta borrado, edición o línea cortada. No protege contra quien reescriba todo el archivo con una cadena nueva (no hay ancla externa).

## Qué NO cambia
Sin instalaciones silenciosas, sin ocultar nada a IT, sin red saliente desde el add-in.
