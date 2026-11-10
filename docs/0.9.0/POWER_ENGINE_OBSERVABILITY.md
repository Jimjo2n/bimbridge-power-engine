# Centro de control «BIMBRIDGE - Power Engine»

Botón **Power Engine | Estado** de la cinta BIMBRIDGE. Ventana no modal. No llama a la API de Revit ni guarda `Document` o `Element`. Se redibuja como mucho cada 1,5 s y sólo si algo cambió; la cola en disco se relee como mucho cada 5 s.

## Secciones

| Sección | Muestra |
|---|---|
| Resumen | Permisos R/P/W, estado del C# dinámico, versión exacta del add-in, Revit y pid, documento activo, agentes conectados y activos, solicitudes en ejecución, planes en cola, errores recientes, modo, salud del diario, tiempo activo |
| Agentes | Por sesión: alias o «sin nombre», presencia, cliente y versión, fuente de identidad, usuario verificado, sesión, conexión, última actividad, solicitudes, en ejecución, errores, tiempo acumulado, última operación y documento, conversación y modelo declarados. Debajo, las últimas desconectadas con su motivo |
| Seguridad y recuperación | Bloqueo de C#, bloqueo de escritura del documento activo (`CheckWritable`) y cómo resolverlo, últimas ejecuciones dinámicas |
| Rendimiento | Por operación sobre las últimas 200 solicitudes: n, mediana, p95 (sólo con 20 o más muestras) y errores |
| Actividad | Últimas 30 entradas con hora, agente, operación, resultado, duración y documento. Distingue consultas, planes, commits, ensayos, desconocidos y C# dinámico |

**Exportar:** guarda `actividad_*.jsonl`, `actividad_*.csv` y `sesiones_*.json` en `%LOCALAPPDATA%\PDI.RevitMcp\exportes`.

## Límites de esta revisión

| Requisito | Estado |
|---|---|
| Vista Agentes con filtros, orden y búsqueda | NO: lista fija ordenada por conexión |
| Desconectar, revocar o cancelar desde el panel | NO |
| Vista Power Engine por motor (Rust, Python) | NO: el estado sigue disponible con `power estado` y `AgentHost power-engine estado` |
| Leases y fencing en el panel | NO |
| Tiempos IPC y de serialización por motor | NO: el commit añade `espera_cola_ms` y `revit_ms` |
| MVVM y virtualización de tablas | NO: WPF por código como el resto del add-in, con firma de cambios y límites acotados |
| Tokens reales | NO: el cliente MCP no los informa; los estimados siguen en la herramienta `metricas` |
| Validación en Revit | PENDIENTE |
