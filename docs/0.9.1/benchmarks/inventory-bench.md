# Benchmarks offline del índice de capacidades (0.9.1)

Generado por `tools/bench/CapabilityBench` en 4 CPU lógicas, .NET 8.0.33, Microsoft Windows 10.0.26200. Catálogos sintéticos (semilla fija) y el catálogo real.
No mide Revit. Tokens = bytes/4 (estimación). Tiempos: mediana de 5 construcciones; búsquedas: media por llamada.

| Catálogo | Construcción ms | Retenido KB | Asignado KB | Por id µs | Por dominio µs | Texto µs | Versión+contexto µs | Página 20 filas bytes | Tokens est. | Catálogo completo bytes | Tokens est. completo |
|---|---|---|---|---|---|---|---|---|---|---|---|
| real (223) | 2.79 | 166 | 1627 | 0.051 | 1.8 | 3.1 | 20.7 | 875 | 218 | 211355 | 52838 |
| 500 | 3.19 | 239 | 2911 | 0.093 | 2.6 | 12.3 | 40.5 | 1161 | 290 | 313778 | 78444 |
| 1000 | 5.98 | 404 | 5715 | 0.045 | 1.4 | 13.3 | 26.0 | 1728 | 432 | 627810 | 156952 |
| 5000 | 22.67 | 1514 | 28236 | 0.039 | 7.6 | 70.5 | 126.2 | 1729 | 432 | 3157045 | 789261 |
| 10000 | 40.76 | 2226 | 56473 | 0.045 | 15.3 | 182.3 | 468.7 | 1729 | 432 | 6319230 | 1579807 |

## Huella, serialización y memoria del proceso

| Catálogo | Huella canónica (construir + SHA-256) ms | Serializar página µs | Serializar un contrato µs | Contrato bytes | Tokens est. | Montón del proceso KB (pico observado tras la serie) |
|---|---|---|---|---|---|---|
| real (223) | 21.33 | 12.3 | 10.5 | 945 | 236 | 14557 |
| 500 | 8.24 | 16.2 | 8.3 | 654 | 163 | 16124 |
| 1000 | 14.34 | 24.5 | 8.3 | 649 | 162 | 18770 |
| 5000 | 78.44 | 5.0 | 2.0 | 665 | 166 | 36475 |
| 10000 | 126.58 | 4.9 | 2.0 | 659 | 164 | 56314 |

La fila «real» es el catálogo del producto en este commit. La huella es perezosa: no entra en la construcción ni en la carga del add-in; se calcula la primera vez que se pide `catalogo`.

Lectura: el descubrimiento devuelve una página compacta de tamaño casi constante; serializar el catálogo completo crece linealmente y por eso nunca se envía entero (contrato completo sólo con `check name=cobertura id=`).
