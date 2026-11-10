# PDI.RevitMcp 0.7.0-alpha1 — índice

Solo backend simulado. Revit no fue tocado. Base v0.6.1-rc3 (7be12ad) sin cambios.

- ARQUITECTURA_0.7.0.md — capas, AH-01…05, servicio, documentos, adaptador
- IR_CONTRATO_0.7.0.md — contrato del plan y estados
- TEST_REPORT_0.7.0.md — resultados con logs literales (325/326; la falla ya existía en rc3)
- RENDIMIENTO_0.7.0.md — escenario 1 000, bytes de mensajes, kernel candidato
- LIMITACIONES_0.7.0.md, ROADMAP_0.7.0.md
- evidencia/ — logs de dotnet test, escenario, diario e informe completos, banco del kernel, pipeline de documento

Comandos (`dotnet PDI.RevitMcp.AgentHost.dll …`): validar, modelo crear|compactar, ejecutar, estado, pausar/reanudar/cancelar, servicio, cliente, documento, escenario, banco-kernel.
