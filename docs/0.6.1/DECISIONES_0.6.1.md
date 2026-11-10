# Registro de decisiones 0.6.1

| Fecha | Decisión | Motivo |
|---|---|---|
| 2026-10-22 | No tocar Revit en esta fase | operator trabaja en él; aceptación nativa queda NOT_EXECUTED |
| 2026-10-22 | Auditoría en dos fases; sin intención no se aplica | R01 |
| 2026-10-22 | Cadena inválida ⇒ cuarentena, no continuar | R02: no ocultar corrupción |
| 2026-10-22 | Concesiones sólo desde el botón de Revit; argumentos MCP sólo «solicitadas» | R03 |
| 2026-10-22 | Clave HMAC local; DPAPI aplazado a 0.7.0 | Riesgo declarado, −2 en R03 |
| 2026-10-22 | Política de DLL antes de cargar; lista por sha256 | R04 |
| 2026-10-22 | Recetas autónomas = conjunto cerrado instalado + `receta_externa` | El análisis estático no es sandbox |
| 2026-10-22 | Reconciliar sólo estados dudosos, con nota | R05 |
| 2026-10-22 | Lease por documento además del de tarea | R06 |
| 2026-10-22 | Fallo de checkpoint detiene la tarea | R07 |
| 2026-10-22 | Rust sólo diseño | Decisión de diseño |
| 2026-10-22 | No mover la etiqueta v0.6.1; candidata nueva v0.6.1-rc2 | Trazabilidad |
| 2026-10-22 | Puntuación 90/100 con descuentos explícitos | Sin ajustar la escala |
| 2026-10-22 | rc3: clave con cabecera v1; Base64 declarado como codificación; nunca regenerar si hay concesiones o la clave es inválida | P0-1; regenerar invalidaría o enmascararía concesiones |
| 2026-10-22 | rc3: DPAPI aplazado | Exige Windows para probar, y elegir alcance (usuario/máquina) cambia la portabilidad de concesiones; antes se verifica el formato funcional |
| 2026-10-22 | rc3: autorizar antes de cualquier código de receta; metadatos leídos del código, no del objeto | P0-2 |
| 2026-10-22 | rc3: transacciones propias en código autónomo siempre denegadas | Sólo el motor abre transacciones; así una fase de lectura no puede escribir |
| 2026-10-22 | rc3: cabeza de auditoría en archivo aparte, sin firma | Mecanismo más simple que detecta último registro; firmar sin clave protegida sería ornamental |
| 2026-10-22 | rc3: resultado con auditoría incompleta = estado propio, nunca "ok" ni "revertido" | P1-4 |
| 2026-10-22 | rc3: huella estricta (igualdad, ausencia no amplía) | P1-5 |
| 2026-10-22 | rc3: puntuación 86, categoría conservadora 0.7_DEVELOPMENT_READY | rc2 sobrevaloró |
| 2026-10-22 | 0.7.0: AgentHost sólo con backend simulado; rechaza escrituras con backend real | Criterio de aceptación del backend |
