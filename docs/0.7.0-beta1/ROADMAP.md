# Hoja de ruta (beta1)

C#/.NET es el motor definitivo. Python y Rust sólo entran si un benchmark lo justifica (0.8.0 / 0.9.0).

| Versión | Contenido | Criterio de salida |
|---|---|---|
| 0.7.0-alpha3 | Ensayo reversible desde AgentHost; P0 IsModified | **Cumplido:** NRT-04 run6 y NRT-04N aprobadas en nativo |
| **0.7.0-beta1** (actual) | Escritura persistente controlada sobre un .rvt desechable; build reproducible; arreglo del instalador | NRT-05 APROBADA en nativo: commit verificado, SHA cambiado, valores en disco y sin duplicado |
| 0.7.0-beta2 | NRT-03 nativo: caída del AgentHost en cada punto, sin matar Revit; escritura autónoma autorizada | NRT-03 nativo aprobado |
| 0.8.0 | Inteligencia documental (PDF/DOCX, requisitos) | Benchmarks |
| 0.9.0 | Rendimiento geométrico y paralelismo | Mejora medida frente a C# |
| 1.0.0 | Candidata a evaluación corporativa | Aceptación nativa y revisión de TI |
