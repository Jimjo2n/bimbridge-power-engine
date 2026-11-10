# Decisiones de arquitectura (revisión consolidada Power Engine)

Los antecedentes técnicos se conservan en los registros versionados de arquitectura y fiabilidad. Véanse [ADR 0.6.1](../0.6.1/ADR_0.6.1.md) y [fiabilidad](../0.6.1/RELIABILITY_MODEL_0.6.1.md).

- **D37 — C# dinámico por defecto con un solo control.** `DynamicCodePolicy` y config `bloquear_inyeccion_csharp`. Sustituye «Código confiable» (4 h) y la autorización por SHA en `exec`. La frontera de confianza es la tubería del usuario más los niveles; no se abre ninguna escucha.
- **D38 — Rebranding sólo de superficies visibles.** `Protocol.Brand` concentra los textos. Se conservan namespaces `PDI.RevitMcp`, GUID del add-in, nombres de tubería, carpetas `%APPDATA%`/`%LOCALAPPDATA%\PDI.RevitMcp`, la clave MCP `pdi-revit` (prefijo de herramientas `mcp__pdi-revit__*` en los clientes) y la clave del registro de Windows. Las transacciones pasan a `BIMBRIDGE …`, y `PDI MCP …` se sigue reconociendo como propia (Deshacer de versiones anteriores).
- **D39 — Identidad declarada, principal verificado.** El alias nunca se infiere ni se hereda. El principal es el usuario de Windows que fija la ACL. Registro con `ctx agente=`, sin herramientas nuevas.
- **D40 — Atribución en el diario existente.** `Journal` (JSONL, hilo de fondo, cola acotada) añade agente, cliente, traza y documento. No se añadió SQLite: el panel usa el anillo en memoria (200) y la exportación usa el JSONL.
- **D41 — Un viaje para escribir.** `commit ops=…` sin `plan_id` planifica y aplica con el mismo motor. Los planes con geometría (job) no se fusionan.
- **D42 — Optimizaciones medibles de consulta.** Conteo puro con `GetElementCount` (`QuerySpec.CanFastCount`) y caché de nombres de tipo, familia y nivel por consulta (`ElementFields.NameCache`). Vive sólo una consulta, en el hilo de Revit: no necesita invalidación.
- **D43 — `ctx` compacto.** La tabla de 27 capacidades sólo aparece con `detalle=true`.
- **D44 — Pruebas aisladas del perfil real.** Las pruebas de extremo a extremo usan `PDI_REVITMCP_CONFIG` temporal.

## Descartado en esta revisión

Caché de consultas entre llamadas, con invalidación por cambio de documento; DAG de tareas; scheduler con fairness; SIMD en Rust. No hay perfiles nativos que los justifiquen y el riesgo de servir datos de otra revisión es alto.
