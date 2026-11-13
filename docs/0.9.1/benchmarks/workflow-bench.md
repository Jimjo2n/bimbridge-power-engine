# Benchmarks offline de workflows (0.9.1)

Generado por `tools/bench/WorkflowBench` en 4 CPU lógicas, .NET 8.0.33. Mediana y p95 de 7 repeticiones tras calentamiento; semilla fija.
**Mide sólo lo que BIMBRIDGE hace fuera del hilo de Revit.** No es rendimiento de Revit ni de pyRevit; las cifras «antes/después» comparan dos implementaciones del mismo cálculo en este equipo.

| Escenario | Variante | N | Mediana ms | p95 ms | Throughput /s | Asignado KB | Nota |
|---|---|---|---|---|---|---|---|
| BM-03 Model Health: informe (duplicados, parámetros, conectores) | informe | 1000 | 1.44 | 1.75 | 692425 | 796 |  |
| BM-03 Model Health: informe (duplicados, parámetros, conectores) | informe | 10000 | 15.50 | 21.39 | 645066 | 6463 |  |
| BM-03 Model Health: informe (duplicados, parámetros, conectores) | informe | 50000 | 57.84 | 69.13 | 864404 | 31620 |  |
| BM-03 Duplicados en modelo denso | antes (List.Contains) | 10000 | 7.90 | 8.56 | 1266272 | 4624 |  |
| BM-03 Duplicados en modelo denso | después (HashSet) | 10000 | 8.49 | 10.13 | 1177593 | 6343 |  |
| BM-03 Duplicados en modelo denso | antes (List.Contains) | 50000 | 45.50 | 46.80 | 1098961 | 22506 |  |
| BM-03 Duplicados en modelo denso | después (HashSet) | 50000 | 45.00 | 50.26 | 1110993 | 31100 |  |
| BM-03 Duplicados: un único grupo de 20.000 coincidentes | antes (List.Contains) | 20000 | 42.60 | 63.73 | 469511 | 5639 |  |
| BM-03 Duplicados: un único grupo de 20.000 coincidentes | después (HashSet) | 20000 | 4.71 | 5.32 | 4245383 | 7554 |  |
| BM-06 Validación offline de N colocaciones | N planes de 1 op | 10 | 0.07 | 0.13 | 151745 | 28 | mide sólo la validación offline; cada plan adicional en Revit añade una visita al hilo y una transacción (no medido aquí) |
| BM-06 Validación offline de N colocaciones | 1 plan de N ops | 10 | 0.07 | 0.12 | 148148 | 25 |  |
| BM-06 Validación offline de N colocaciones | N planes de 1 op | 50 | 0.20 | 0.22 | 247158 | 142 | mide sólo la validación offline; cada plan adicional en Revit añade una visita al hilo y una transacción (no medido aquí) |
| BM-06 Validación offline de N colocaciones | 1 plan de N ops | 50 | 0.17 | 0.19 | 302115 | 125 |  |
| BM-06 Validación offline de N colocaciones | N planes de 1 op | 200 | 0.69 | 0.77 | 290951 | 568 | mide sólo la validación offline; cada plan adicional en Revit añade una visita al hilo y una transacción (no medido aquí) |
| BM-06 Validación offline de N colocaciones | 1 plan de N ops | 200 | 0.96 | 1.20 | 209183 | 500 |  |
| BM-07 Copy/Monitor: verificación de la copia (sin la parte manual) | verificar | 88 | 0.21 | 0.29 | 413146 | 125 |  |
| BM-07 Copy/Monitor: congelar y comparar expectativas | huella + diff | 88 | 0.32 | 0.45 | 277778 | 124 |  |
| BM-07 Copy/Monitor: verificación de la copia (sin la parte manual) | verificar | 10000 | 218.94 | 296.15 | 45674 | 12953 |  |
| BM-07 Copy/Monitor: congelar y comparar expectativas | huella + diff | 10000 | 39.13 | 58.18 | 255585 | 13637 |  |
| BM-09 Planificador: 64 operaciones repartidas entre agentes | 1 agente(s) | 64 | 498.75 | 516.14 | 128 | 18 | espera sintética de 5 ms por operación: mide el coste de coordinación y el reparto, no trabajo en Revit |
| BM-09 Planificador: 64 operaciones repartidas entre agentes | 2 agente(s) | 64 | 341.39 | 344.11 | 187 | 18 | espera sintética de 5 ms por operación: mide el coste de coordinación y el reparto, no trabajo en Revit |
| BM-09 Planificador: 64 operaciones repartidas entre agentes | 4 agente(s) | 64 | 341.72 | 344.80 | 187 | 18 | espera sintética de 5 ms por operación: mide el coste de coordinación y el reparto, no trabajo en Revit |
| BM-09 Planificador: 64 operaciones repartidas entre agentes | 8 agente(s) | 64 | 341.43 | 343.59 | 187 | 18 | espera sintética de 5 ms por operación: mide el coste de coordinación y el reparto, no trabajo en Revit |
| INS Armar y serializar WorkflowMetrics | 1 workflow | 1 | 0.04 | 0.06 | 23810 | 5 |  |

## Antes → después (misma máquina, mismo dato)
- Duplicados en modelo denso (N=10000): 7.90 ms → 8.49 ms (0.9×)
- Duplicados en modelo denso (N=50000): 45.50 ms → 45.00 ms (1.0×)
- Duplicados: un único grupo de 20.000 coincidentes (N=20000): 42.60 ms → 4.71 ms (9.0×)
