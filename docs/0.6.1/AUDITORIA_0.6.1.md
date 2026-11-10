# Auditoría 0.6.1: hallazgos y estado

| # | Hallazgo en v0.6.0 | Severidad | Estado 0.6.1 | Evidencia |
|---|---|---|---|---|
| A1 | Lease sin atomicidad entre procesos | P0 | corregido | LeaseFencingTests (procesos reales) |
| A2 | Sin fencing; liberar borraba `lease.json` | P0 | corregido | LeaseFencingTests |
| A3 | Renovación fallida ignorada por el ejecutor | P0 | corregido | LeaseStolen_MidRun |
| A4 | `lease.json` ilegible tratado como libre | P0 | corregido | Lease_CorruptFile |
| A5 | Auditoría de `exec` podía perderse en silencio | P0 | corregido (fail closed para escritura) | AuditLogTests + compila |
| A6 | Código autónomo sin límites de capacidades | P0 | mitigado (política estática; no es sandbox) | ScriptingTests |
| A7 | Cliente no verificaba el servidor del pipe | P1 | corregido | PipeServerCheck (regla probada; Win32 compila) |
| A8 | Recetas sin firma (autenticidad) | P1 | abierto → 0.7.0 | SECURITY_MODEL |
| A9 | Cadena de auditoría sin ancla externa | P2 | abierto → 0.7.0 | SECURITY_MODEL |
| A10 | Lote ya validado termina aunque el lease venza en medio | P2 | aceptado y documentado | RELIABILITY_MODEL |
| A11 | MainThread/presupuestos: revisados, sin cambios | — | sin cambios (presupuesto por visita ya acotado desde 0.6.0) | — |
| A12 | Todo lo anterior sin ejecución nativa | P0 para aprobar | pendiente | NATIVE_ACCEPTANCE |
