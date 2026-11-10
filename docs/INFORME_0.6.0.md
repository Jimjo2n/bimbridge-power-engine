# PDI Revit MCP 0.6.0: informe de entrega (candidata)

## 1. Qué puede terminar 0.6.0 que 0.5.0 no podía

- **Una tarea larga con una sola orden.** 0.5.0 obligaba a la IA a partir el trabajo en planes de ≤200 operaciones y a llevar ella la cuenta. En 0.6.0 la IA dice `tarea iniciar receta=… id=…` y el add-in enumera las unidades, las ejecuta por lotes con una transacción por unidad, cede Revit entre visitas, guarda un checkpoint por lote y termina con guardar/sync/PDF sólo cuando todo lo anterior está confirmado.
- **Sobrevivir a la pérdida del chat, del puente o de Revit sin duplicar.** Una unidad que quedó entre el commit y el diario se verifica (UniqueId de lo creado, `Verificar` de la receta o inferencia segura por apertura y guardado) en vez de repetirse. Lo que no se puede probar queda `desconocida` y espera una decisión explícita (`tarea reconciliar`).
- **Que otro cliente retome el trabajo.** Codex o Claude leen `tarea resumen <id>` desde el disco: ~1 000 caracteres para una tarea de 1 000 unidades (estado, progreso, desviaciones, artefactos con hash y siguiente paso), sin reenviar código ni contexto.
- **Recetas reutilizables por archivo**, con su versión y SHA-256 congelados en la tarea. Cinco incluidas: `etiquetar_luces` (exactamente una etiqueta por luminaria, reanudable), `parametro_por_lote`, `qc_manifiesto`, `montaje_soportes` y `exportar_laminas`.
- **QC que no aprueba por totales.** Hay un QC uno a uno contra un manifiesto de referencia que detecta faltantes, sobrantes, casi duplicados, posición, altura, giro, tipo, nivel, etiqueta, grupo y montaje, y que no da por aprobado un caso ambiguo. La clasificación de montaje se hace contra el soporte real, también dentro de vínculos (al ras, empotrado, separado o sin referencia). Y hay un "¿por qué no se ve?", que distingue filtro, categoría, workset, fase, recorte y rango de vista, y dice qué no se puede comprobar por API.
- **Un instalador verificable.** Comprueba el SHA-256 de cada archivo antes y después de copiar. `Setup.exe /diagnostico` dice la versión instalada, la cargada en cada Revit abierto, el estado del puente y si falta reiniciar. Además conserva el modo directo y la elección de versiones al actualizar.

## 2. Qué se probó dentro de Revit

**Nada de 0.6.0 se ha ejecutado todavía dentro de Revit.** Este entorno no tiene Revit.

| Nivel | Qué cubre |
|---|---|
| Compilado | Compila para Revit 2022, 2023, 2024, 2025 y 2026. Las 5 recetas, los scripts del benchmark y los de aceptación compilan contra la API de los 5 años; lo comprueba una prueba automática. |
| Probado sin Revit | **193 pruebas en verde.** El motor de tareas real se probó con un modelo simulado e inyección de fallos: caída entre commit y diario, reinicio de Revit antes y después de guardar, pausa, documento cerrado, conflicto, cancelación y diario no escribible. También se probó el transporte de extremo a extremo en ambos perfiles, la identidad documental, las 5 regresiones de transporte, el QC por manifiesto con la fixture D de desviaciones conocidas, el instalador (hashes, diagnóstico, configuración conservada) y la medición del resumen. |
| Nativo | Sólo hay evidencia previa de 0.5.0 en Revit 2026: lectura, una muestra, el 20-10-2026. El backend de tareas sobre Revit (`RevitTaskBackend`), las ayudas `Bim.*` y las recetas **sólo están compilados**. |

La aceptación nativa está escrita paso a paso para 2024 y 2026 sobre copias de práctica, con su criterio de aprobado: [tools/aceptacion/PROCEDIMIENTO_0.6.0.md](../tools/aceptacion/PROCEDIMIENTO_0.6.0.md). Ninguno de esos pasos se declara aprobado aquí.

## 3. Resultados del benchmark

| Medida | 0.5.0 | 0.6.0 | Cómo |
|---|---|---|---|
| Costo fijo, perfil compacto (lo que la IA carga por conversación) | 5 herramientas, 5 810 caracteres | 5 herramientas, **5 685 caracteres** (~1 780 tokens estimados), con `tarea` incluida | bench `tokens` |
| Costo fijo, perfil completo | — | 20 herramientas, 18 437 caracteres | bench `tokens` |
| Seguir/retomar una tarea de 1 000 unidades con 10 fallos | no existía: la IA lo reconstruía con muchas llamadas | **996 caracteres**, idéntico para un segundo cliente | `Bench_CompactSummary_*` |
| MCP vs pyRevit en Revit, 3 cargas, 1 fría + 10 calientes | — | **no ejecutado** | `tools/bench_060` |

La única comparación nativa con pyRevit sigue siendo la de 0.5.0 en Revit 2026: lectura, una muestra por prueba y conteos no idénticos. Dio el MCP en ≈1,3–1,5 s frente a pyRevit en ≈3,6–5,2 s, con entre ½ y ¼ del texto. No es un benchmark equivalente.

Para el benchmark equivalente ya están las tres cargas en ambos motores, con el mismo algoritmo y alcance: familia, QC geométrico de luces, y grupos/vistas/láminas/PDF. También está la plantilla de corridas y el resumen p50/p95, que sólo vale si los resultados de QC coinciden entre motores.

Límites de estas cifras:
- Las cifras de tokens son caracteres ÷ 3,2, no tokens reales del cliente.
- Las medidas sin Revit no dicen nada del tiempo dentro de Revit.

## 4. Limitación material que permanece

**El camino real de tareas dentro de Revit no se ha ejecutado.** El motor está probado y el adaptador a Revit compila, pero transacciones por unidad con avisos reales, rayos contra vínculos, exportación PDF desde la tarea y reapertura tras una caída real sólo se confirmarán con el procedimiento nativo. Hasta entonces, 0.6.0 es candidata, no aprobada.

Otros límites conocidos (por la API, no por elección):
- El rango de vista de las regiones de plano no es legible por la API pública: `PorQueNoSeVe` lo marca como no comprobable.
- El conteo de páginas PDF es orientativo.
- La API no dice qué opción de diseño muestra una vista.
- Que un grupo entero esté en espejo se infiere de sus miembros.

## 5. Instalación

1. Descomprimir `PDI.RevitMcp.Setup_0.6.0.zip`.
2. Comprobar el SHA-256 del `.exe` contra `MANIFIESTO_0.6.0.txt`.
3. Ejecutarlo y marcar 2024 y/o 2026. Con Revit abierto, lo nuevo entra al reiniciar; el diagnóstico lo dice.
4. Diagnóstico: `PDI.RevitMcp.Setup.exe /diagnostico`.
5. Volver a la versión anterior: botón "Versión" de la cinta → versión anterior. Se conserva el instalador previo.

Prueba corta de una tarea real: [PRUEBA_RAPIDA_OPERATOR.md](PRUEBA_RAPIDA_OPERATOR.md).

## 6. Archivos de la entrega

| Archivo | Qué es |
|---|---|
| `PDI.RevitMcp.Setup_0.6.0.zip` | instalador 2022–2026, con `manifest.json` de hashes dentro |
| `MANIFIESTO_0.6.0.txt` | SHA-256 de cada archivo de la entrega y de cada archivo del paquete |
| `PDI.RevitMcp_codigo_0.6.0_<commit>.zip` y `codigo_0.6.0.gitbundle` | fuente completo y repositorio con historia (tag v0.6.0) |
| `CHANGELOG_0.6.0.md` | cambios con su nivel de comprobación |
| `PROTOCOLO_0.6.0.md`, `ESQUEMA_TAREA_0.6.0.md`, `RECETAS.md` | contrato de la operación `tarea`, esquema de checkpoint, recetas |
| `MATRIZ_CAPACIDADES.md`, `ESTADO_REQUISITOS_0.6.0.csv` | capacidad por orden y requisito, con evidencia |
| `aceptacion\` | procedimiento nativo, scripts de manifiesto y perturbación, `recoger_evidencia.ps1` |
| `bench_060\` | benchmark equivalente MCP/pyRevit y resumen |
| `evidencia\` | salida de las pruebas, mediciones de costo y resumen de tarea |
