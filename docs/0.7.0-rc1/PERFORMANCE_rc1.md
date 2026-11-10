# Rendimiento rc1 (sin Revit)

Medido en un contenedor Linux con 4 núcleos y .NET 8, contra el add-in falso por **tubería real** (IPC del Bridge). Son cifras relativas; las latencias de Revit salen de la campaña nativa.

## Lotes (`Rc1_Bench_BatchSizes_CallsBytesAndLatency`)
| Elementos | Modo | Llamadas IPC | Commits | Bytes IPC | ms |
|---|---|---|---|---|---|
| 1 | lote | 6 | 1 | 1 208 | 11* |
| 10 | lote | 6 | 1 | 2 716 | 11 |
| 10 | uno a uno | 51 | 10 | 9 614 | 88 |
| 50 | lote | 6 | 1 | 9 396 | 11 |
| 50 | uno a uno | 251 | 50 | 47 246 | 518 |
| 200 | lote | 6 | 1 | 34 451 | 16 |

\* La primera medición (114 ms) incluye el arranque en frío de la tubería.

**Conclusión:** un lote mantiene 6 llamadas por operación sea cual sea el tamaño (context, conteo, línea base, plan, commit y postcondición). Con 50 elementos ahorra el 98 % de las llamadas y el 80 % de los bytes frente a ir uno a uno. Con esta evidencia se subió el máximo por operación de 30 a **200** (una página del add-in). La relectura antes del commit se mantiene a propósito: es la protección contra cambios entre Prepare y Execute, y no se recorta para ahorrar.

## Heredado de beta1 (sin cambios de código en esa ruta)
- Plan de 1000 operaciones con el simulador durable: 2,75 s, 172 MB de RSS, diario de 1,5 MB.
- Recuperación del diario: 0,39 s, incluido el arranque de dotnet.
- NRT-05 nativo (beta1.1): commit 183 ms y guardar + verificar en disco 4,9 s.
