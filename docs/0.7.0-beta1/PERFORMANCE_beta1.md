# Rendimiento beta1

Contenedor Linux de CI, 4 núcleos, .NET 8, compilación Debug. No es el equipo de prueba: sirve como referencia relativa, no como cifra de producción.

| Medida | Resultado |
|---|---|
| Plan de 1000 operaciones (988 escrituras, 1030 dependencias) contra el backend simulado durable | 2,75 s en total, RSS máx. 172 MB, diario de 1,5 MB, seq 2921 |
| Recuperación del diario (`agenthost estado`, reabre y reconstruye 2921 eventos), 3 rondas | 0,39 / 0,41 / 0,39 s, incluido el arranque de dotnet |
| Suite completa (367 pruebas) | 1 m 38 s |
| NRT-05 contra el add-in falso por tubería (conectar, línea base, plan, commit, observar, guardar, verificar_disco, reanudar) | < 0,2 s |

**Pendiente de la corrida nativa** (sale de `nrt05_informe.json → metricas` y de `llamadas.jsonl`):
- Tiempo de conexión MCP.
- Latencias de query, plan (Prepare), commit (execute), Observe, guardar y verificar_disco.
- Bytes IPC por operación y llamadas al modelo por plan.

Diseño: un plan determinista agrupa las escrituras de una operación en un plan y un commit del add-in. Por eso el número de llamadas por plan no crece con el número de elementos (máx. 30 objetivos por operación, páginas de 200).
