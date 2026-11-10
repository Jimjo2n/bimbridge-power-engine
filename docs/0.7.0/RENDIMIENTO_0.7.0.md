# Línea base de rendimiento — 0.7.0-alpha1 (simulado, contenedor Linux, sin Revit)

## Escenario 1 000 operaciones
- En proceso, sin caídas: 2 615 ms; diario 2 921 líneas.
- Con 7 SIGKILL (8 procesos, incluye arranque de .NET por proceso y espera de lease 2 s): 18 465 ms.

## Tamaño de mensajes (bytes UTF-8, medidos)
```
{"operaciones":1000,"ejecucion_ms_en_proceso":2615,"diario_lineas":2921,"plan_completo_bytes":373141,"estado_completo_bytes":308401,"resumen_bytes":64,"siguiente_bytes":7,"excepciones_bytes":1658,"fallos_verificacion_bytes":424,"cambios_ultima_operacion_bytes":81,"informe_referencia_bytes":401,"nota":"bytes UTF-8; no se midieron tokens (no hay tokenizador disponible sin red); no se afirma ahorro de tokens"}
```
Un agente puede seguir el plan con `resumen` (64 B) y `siguiente` (7 B) en vez de leer el estado completo (308 401 B). **No se midieron tokens** (no había tokenizador disponible); no se afirma ningún ahorro de tokens.

## Kernel candidato para Rust (P2): montaje vertical TriMesh.Extent
Rayo vertical punto→malla (lo que usa la verificación de montaje contra losas). Entrada binaria determinista, salida con sha256 para comparar implementaciones byte a byte.
```
{"kernel":"TriMesh.Extent (montaje vertical)","lenguaje":"C# 8.0.31","simd":true,"triangulos":20000,"puntos":20000,"aciertos":16027,"rondas":5,"entrada_bytes":1760008,"serializar_ms":8.148,"deserializar_ms":2.42,"indice_ms_mediana":36.023,"consulta_ms_mediana":25.214,"consulta_ns_por_punto":1260.7,"asignado_bytes_mediana":11328912,"memoria_proceso_mb":55,"digest":"742e47dabe872bd98b27addb01ec36c533b0c55d47dd8d48cc001490eb31654a","sha256_entrada":"cda519b430542b298bffba6ff21f3286ce5de5603c8d2075e049978805279943"}
```
Lectura: ~1,2 µs por punto en C# con SIMD; 20 000 luminarias en ~25 ms. **Con estos números Rust no está justificado todavía**: el coste real en Revit será extraer la geometría, no el cálculo. Se deja el banco y el formato para que cualquier implementación Rust se compare con el mismo digest.
