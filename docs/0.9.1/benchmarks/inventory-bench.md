# Benchmarks offline del índice de capacidades (0.9.1)

Generado por `tools/bench/CapabilityBench` en 4 CPU lógicas, .NET 8.0.33, Microsoft Windows 10.0.26200. Catálogos sintéticos (semilla fija) y el catálogo real.
No mide Revit. Tokens = bytes/4 (estimación). Tiempos: mediana de 5 construcciones; búsquedas: media por llamada.

| Catálogo | Construcción ms | Retenido KB | Asignado KB | Por id µs | Por dominio µs | Texto µs | Versión+contexto µs | Página 20 filas bytes | Tokens est. | Catálogo completo bytes | Tokens est. completo |
|---|---|---|---|---|---|---|---|---|---|---|---|
| real (123) | 1.69 | 87 | 777 | 0.050 | 0.9 | 2.0 | 8.3 | 646 | 161 | 113536 | 28384 |
| 500 | 3.25 | 239 | 2973 | 0.054 | 1.9 | 10.6 | 37.5 | 1161 | 290 | 313778 | 78444 |
| 1000 | 7.91 | 404 | 5839 | 0.039 | 2.9 | 40.2 | 143.6 | 1728 | 432 | 627810 | 156952 |
| 5000 | 27.81 | 1514 | 28868 | 0.049 | 7.1 | 71.7 | 115.8 | 1729 | 432 | 3157045 | 789261 |
| 10000 | 40.65 | 2226 | 57739 | 0.052 | 15.9 | 177.8 | 289.0 | 1729 | 432 | 6319230 | 1579807 |

Lectura: el descubrimiento devuelve una página compacta de tamaño casi constante; serializar el catálogo completo crece linealmente y por eso nunca se envía entero (contrato completo sólo con `check name=cobertura id=`).
