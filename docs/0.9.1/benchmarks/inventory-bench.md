# Benchmarks offline del índice de capacidades (0.9.1)

Generado por `tools/bench/CapabilityBench` en 4 CPU lógicas, .NET 8.0.33, Microsoft Windows 10.0.26200. Catálogos sintéticos (semilla fija) y el catálogo real.
No mide Revit. Tokens = bytes/4 (estimación). Tiempos: mediana de 5 construcciones; búsquedas: media por llamada.

| Catálogo | Construcción ms | Retenido KB | Asignado KB | Por id µs | Por dominio µs | Texto µs | Versión+contexto µs | Página 20 filas bytes | Tokens est. | Catálogo completo bytes | Tokens est. completo |
|---|---|---|---|---|---|---|---|---|---|---|---|
| real (222) | 5.19 | 166 | 1621 | 0.088 | 1.9 | 3.6 | 22.7 | 875 | 218 | 209919 | 52479 |
| 500 | 7.50 | 239 | 2911 | 0.062 | 3.4 | 14.0 | 77.8 | 1161 | 290 | 313778 | 78444 |
| 1000 | 14.26 | 404 | 5715 | 0.070 | 2.7 | 22.8 | 66.7 | 1728 | 432 | 627810 | 156952 |
| 5000 | 37.07 | 1514 | 28236 | 0.066 | 12.0 | 113.7 | 274.8 | 1729 | 432 | 3157045 | 789261 |
| 10000 | 72.66 | 2226 | 56473 | 0.094 | 23.4 | 323.7 | 663.6 | 1729 | 432 | 6319230 | 1579807 |

## Huella, serialización y memoria del proceso

| Catálogo | Huella canónica (construir + SHA-256) ms | Serializar página µs | Serializar un contrato µs | Contrato bytes | Tokens est. | Montón del proceso KB (pico observado tras la serie) |
|---|---|---|---|---|---|---|
| real (222) | 45.44 | 16.7 | 12.9 | 945 | 236 | 14551 |
| 500 | 30.34 | 41.7 | 18.7 | 654 | 163 | 16121 |
| 1000 | 22.54 | 9.3 | 3.7 | 649 | 162 | 18772 |
| 5000 | 90.50 | 8.1 | 4.3 | 665 | 166 | 36475 |
| 10000 | 184.31 | 7.3 | 2.3 | 659 | 164 | 59712 |

La fila «real» es el catálogo del producto en este commit. La huella es perezosa: no entra en la construcción ni en la carga del add-in; se calcula la primera vez que se pide `catalogo`.

Lectura: el descubrimiento devuelve una página compacta de tamaño casi constante; serializar el catálogo completo crece linealmente y por eso nunca se envía entero (contrato completo sólo con `check name=cobertura id=`).
