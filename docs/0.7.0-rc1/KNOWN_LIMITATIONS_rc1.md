# Limitaciones y riesgos rc1

1. **Nada de rc1 se ejecutó en Revit.** NRT-03, NRT-06 y la regresión de NRT-05 están `BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION`. Que pasen en el simulador o el FakeBim no es evidencia nativa.
2. **Reapertura:** sigue siendo una copia abierta como otro Document, no un cierre y apertura del documento activo (D17). La validación del cierre y reapertura desde la interfaz requiere un paso manual adicional en la campaña.
3. **Auditoría encadenada del add-in:** el commit persistente consta en el diario de operaciones del add-in y en el del AgentHost, pero no en `auditoria.jsonl` (que registra exec). Hallazgo de la revisión de beta1.1, no corregido en rc1: tocar la cadena del add-in exige validación nativa. Propuesto para la campaña.
4. **Lotes:** solo parámetros de texto. Los valores con unidades por lote siguen fuera de rc1.
5. **QC de luminarias y etiquetas:** reutiliza `check lighting_audit` (regla de un tag por luminaria). En modelos grandes, el coste es el de la auditoría completa del nivel.
6. **Recetas del motor de tareas** (etiquetar, exportar, qc_manifiesto): existen en el add-in, pero el AgentHost no las ejecuta en rc1 (ver README).
7. **Instalador sólo lectura:** probado con archivos sintéticos (`CopyOver`); en Windows real solo se ejerció indirectamente, porque las instalaciones de beta1 y beta1.1 dieron salida 0.
8. **Compuerta nativa:** la impone el AgentHost. El add-in, por su lado, sigue respondiendo a cualquier cliente MCP autorizado por su configuración de niveles (por eso operator puede trabajar con IA en su Revit). La compuerta protege los arneses, no reemplaza los niveles.
9. **Concurrencia en el mismo documento:** el lease del plan y la serialización del add-in (un ExternalEvent) están probados en el simulador y con procesos reales (AH-04). Dos AgentHost con planes distintos sobre el mismo documento real no se han probado en nativo.
