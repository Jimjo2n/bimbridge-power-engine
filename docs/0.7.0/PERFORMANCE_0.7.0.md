# Performance baseline — 0.7.0-alpha1 (simulated, Linux container, without Revit)

## 1,000-operation scenario
- In process, without crashes: 2,615 ms; journal 2,921 lines.
- With 7 SIGKILLs (8 processes, including .NET startup per process and a 2 s lease wait): 18,465 ms.

## Message size (UTF-8 bytes, measured)
```
{"operaciones":1000,"ejecucion_ms_en_proceso":2615,"diario_lineas":2921,"plan_completo_bytes":373141,"estado_completo_bytes":308401,"resumen_bytes":64,"siguiente_bytes":7,"excepciones_bytes":1658,"fallos_verificacion_bytes":424,"cambios_ultima_operacion_bytes":81,"informe_referencia_bytes":401,"nota":"UTF-8 bytes; tokens were not measured (no tokenizer available offline); no token savings are claimed"}
```
An agent can follow the plan with `resumen` (64 B) and `siguiente` (7 B) instead of reading the full state (308,401 B). **Tokens were not measured** (no tokenizer was available); no token savings are claimed.

## Candidate kernel for Rust (P2): vertical mounting TriMesh.Extent
Vertical point→mesh ray (what mounting verification against slabs uses). Deterministic binary input, output with sha256 to compare implementations byte for byte.
```
{"kernel":"TriMesh.Extent (montaje vertical)","lenguaje":"C# 8.0.31","simd":true,"triangulos":20000,"puntos":20000,"aciertos":16027,"rondas":5,"entrada_bytes":1760008,"serializar_ms":8.148,"deserializar_ms":2.42,"indice_ms_mediana":36.023,"consulta_ms_mediana":25.214,"consulta_ns_por_punto":1260.7,"asignado_bytes_mediana":11328912,"memoria_proceso_mb":55,"digest":"742e47dabe872bd98b27addb01ec36c533b0c55d47dd8d48cc001490eb31654a","sha256_entrada":"cda519b430542b298bffba6ff21f3286ce5de5603c8d2075e049978805279943"}
```
Reading: ~1.2 µs per point in C# with SIMD; 20,000 lighting fixtures in ~25 ms. **With these numbers Rust is not yet justified**: the real cost in Revit will be extracting the geometry, not the computation. The bench and the format are kept so that any Rust implementation can be compared against the same digest.
