# Hoja de ruta (corregida según la revisión técnica del 2026-10-28)

C#/.NET es el motor principal **de forma definitiva**: AgentHost, Revit API, transacciones, seguridad y diario. No se migrarán el AgentHost ni el diario a Rust, y no se creará un orquestador paralelo en Python.

| Versión | Contenido | Criterio de salida |
|---|---|---|
| **0.7.0-alpha3** (actual) | AgentHost conectado a Revit por la ruta tipada; ensayo reversible; P1 corregidos; P0 IsModified en el add-in | NRT-04 estándar (copia no workshared, sin excepciones) y NRT-04N (rechazo con IsModified=true) aprobadas en nativo |
| **0.7.0-beta1** | Estabilización y escritura persistente controlada en modelos desechables | NRT-04 persistente con reconciliación verificada en nativo |
| **0.7.0-beta2** | Recuperación nativa NRT-03 y escritura autónoma autorizada | NRT-03 nativo aprobado |
| **0.8.0** | Inteligencia documental: PDF/DOCX y extracción de requisitos. Python solo si aporta ventajas medidas | Benchmarks que lo justifiquen |
| **0.9.0** | Rendimiento geométrico y paralelismo. Rust frente a C# solo mediante benchmarks | Mejora medida frente a C# |
| **1.0.0** | Candidata a evaluación corporativa | Aceptación nativa y revisión de TI |

Sin versiones alpha intermedias: alpha3 se corrige en su rama y pasa directamente a beta1.
