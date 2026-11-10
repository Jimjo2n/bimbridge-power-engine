# BIMBRIDGE 0.8.0 — estado del candidato interno

**Estado de versión:** candidato interno **NATIVE_ACCEPTANCE_PENDING**. Rama `feature/0.8.0` desde `v0.7.0-rc1` (14ec21d). Sin tag hasta que la campaña nativa apruebe. Nada de 0.8.0 se ha ejecutado en Revit: la aceptación requiere una instancia de prueba aislada y un alcance aprobado (`BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION`).

Este documento es el único informe de 0.8.0: estado, pruebas, rendimiento, seguridad, limitaciones y hoja de ruta. La matriz está en [MATRIZ_TRAZABILIDAD_0.8.0.md](MATRIZ_TRAZABILIDAD_0.8.0.md) y la campaña en `tools/aceptacion/0.8.0/PROCEDIMIENTO_0.8.0.md`.

## 1. Qué cambió desde rc1

| Paquete | Commit | Contenido |
|---|---|---|
| A1 | 974f1db | Cierre técnico de 0.7.0: cada commit persistente deja intención y resultado en `auditoria.jsonl` (H2 de rc1); `Home.WriteAtomic` reintenta y no deja temporales; arnés NRT-07 (respuesta IPC perdida tras commit). |
| A2–A3 | ca3e4e1, 65099e7 | Requisitos con procedencia y autoridad (OBSERVED/INFERRED/UNKNOWN; una inferencia nunca es OBSERVED); `verificar_independiente` (fuente independiente → modelo → diferencias; misma fuente que la construcción = `no_independiente`); 12 estados de entrega con recibos; handoff estructurado. |
| C1–C2 | 099724f, dfc2b0e | Núcleo 2D sin Revit (`Draw2D`) y 7 operaciones nuevas: `detail_line`, `detail_rect`, `detail_polygon`, `detail_arc`, `detail_component`, `dimension`, `sheet_set`; `view_create tipo=dibujo`. Catálogo: 34 operaciones. |
| D1 | d9925fc | Biblioteca de familias: `fam buscar` indexa sólo `carpetas_familias`; `fam cargar_archivo` carga un RFA indexado y sin cambios, con sha-256. |
| E1 | ec979fe | **BIMBRIDGE → Developer Tools → Actualizar desde ZIP** (detalle en §4). Pestaña de la cinta renombrada a BIMBRIDGE. |
| E2 | 45ac7e9 | Tokens ESTIMADOS (caracteres/4) en las métricas; benchmark B0 reproducible; catálogo B1–B8. |
| E3 | 426132c | Permisos separados en 10 clases, aplicados en el add-in antes de despachar. |
| F1 | 8ec9c51 | Registro único de capacidades y matriz de trazabilidad generada desde código. |
| G1 | 40b2c43 | Campaña nativa de 13 ítems con arnés `revit campana080`. |

## 2. Pruebas

| Conjunto | Resultado |
|---|---|
| Suite completa (sin Revit) | **440/440** en la última corrida. En una corrida anterior del mismo árbol falló una vez `EndToEndTests.HeldCommit_WaitsForUser_ThenApplies_AndDetectsConflicts` por tiempo (12 s bajo carga) y pasó al repetirla: inestable, no regresión. |
| Nuevas de 0.8.0 | ZipUpdate 12, Permissions 3, Traceability 2, Campaign080 4, SheetSetOp 1, FamilyLibrary 3, Draw2D 12, Workflow 9, V080 (AgentHost) 4, Bench B0 1 |
| Add-in | compila sin errores para Revit 2022–2026 |
| Nativas de 0.8.0 | **0 ejecutadas**. 13 ítems BLOCKED (ver procedimiento). |
| Nativas previas (otro build) | rc1 14ec21d en Revit 2024, copias desechables: NRT-03 15/15, NRT-05, NRT-06 11/11. No cuentan para 0.8.0. |

## 3. Rendimiento y tokens

Todas las cifras son **MEDIDO_SIMULADO**: add-in falso por la tubería real, sin Revit. Los tokens son **ESTIMADOS** (caracteres/4). Reproducir: `BIMBRIDGE_BENCH_OUT=<carpeta> dotnet test --filter Bench_B0`.

| B0 · 586 elementos (leer y cambiar un parámetro) | Uno a uno | Plan BIMBRIDGE |
|---|---|---|
| Llamadas del agente | 1.172 | 3 |
| Llamadas por cable | 1.759 | 16 |
| Commits | 586 | 3 (lotes de ≤200) |
| Caracteres que ve el agente | 254.762 | 12.799 |
| Tokens estimados del agente | 63.691 | 3.200 |

Reducción: 99,74 % de llamadas del agente y 94,98 % de tokens estimados. Coincide con la hipótesis del informe (1.172 → 3), ahora medida en simulación; el tiempo de Revit no está medido. B1–B8 (comparación con pyRevit): **NO_MEDIDO**, con el escenario y el arnés definidos en `Benchmarks.Catalog`.

## 4. Seguridad

| Tema | Estado en 0.8.0 |
|---|---|
| Documento equivocado | El AgentHost fija pid, clave y huella; la campaña inyecta el documento del preflight en cada paso (un paso no puede elegir otro, salvo los negativos marcados). Fail-closed si falta una bandera de contexto (rc1). |
| Permisos | 10 clases: leer, modificar_proyecto, modificar_familia, cargar_archivo, guardar, sincronizar, exportar, publicar, codigo_dinamico, interfaz. `permisos` en config.json restringe; vacío = derivado de los niveles R/P/W/D (mismo comportamiento que antes). Publicar no existe y se deniega siempre. El gate está en el add-in, por donde pasan el puente y el AgentHost. |
| Código dinámico (nivel B) | `exec ejecutar/ensayo` exige W, runner=auto, la clase codigo_dinamico y queda en diario y auditoría; con Retener activo se rechaza; DLL extra sólo de la carpeta del add-in o por SHA-256 salvo «Código confiable». **Riesgo residual:** con W + En vivo no hay revisión humana por llamada y el código corre con los privilegios del usuario dentro de Revit. Para producción se recomienda una lista `permisos` sin codigo_dinamico. |
| Interfaz (nivel C) | Sólo `show` y `capture`. No hay clics automatizados. |
| Discos y familias | `fam buscar` sólo recorre carpetas configuradas, sin seguir enlaces y con límite de archivos; `cargar_archivo` rechaza rutas fuera de ellas o cambiadas desde el índice. No se editan RFA corporativos: las ediciones de familia exigen modificar_familia. |
| Actualizar desde ZIP | Sólo lo pulsa una persona (la IA no tiene herramienta). Inspección: manifiesto, entradas sin rutas peligrosas, límite de tamaño, exactamente los archivos del manifiesto con su SHA-256, firma RSA-SHA256 contra claves de confianza, compatibilidad de versión y años de Revit. Muestra versión instalada y nueva y pide confirmación (doble si no hay firma). Prepara y re-verifica en disco; un proceso aparte espera a que el usuario cierre Revit (nunca lo cierra), re-verifica, instala, verifica y, si falla, vuelve a la versión anterior. Sin hot reload. **Pendiente de IT:** la clave de firma; mientras no haya claves configuradas, los paquetes se aceptan sin firma sólo con confirmación explícita. |
| Datos | Sin secretos en el repositorio ni en el paquete. Benchmarks y pruebas con datos sintéticos. |

## 5. Limitaciones conocidas

- Ninguna operación nueva se ha ejecutado en Revit; el comportamiento real de `sheet_set` (PrintManager), `dimension`, `detail_component` y `cargar_archivo` puede diferir por versión.
- `sheet_set` no controla el orden de impresión (lo decide Revit) y deja `PrintRange=Select` en el documento.
- `dimension` sólo acota líneas de detalle ya existentes (no `@n` del mismo plan).
- La verificación del PDF exportado compara páginas y sha del recibo; no hay lector de contenido PDF.
- Tokens: estimación por caracteres; el tokenizador real lo conoce el cliente.
- Pendientes de rc1 sin evidencia nativa nueva: reapertura real en NRT-05 (H1), lentitud intermitente de Revit, temporal residual (corregido en código, sin comprobar en Revit).
- El ZIP de esta entrega va sin firma (no hay clave de IT).

## 6. Hoja de ruta

- **0.8.0 → aprobada:** campaña nativa de 13 ítems con autorización del operador; B0 nativo y primer par contra pyRevit.
- **0.9.0:** registro plano–modelo (CAP-009), cobertura de grupos (CAP-010), routing físico (CAP-011), grafo de riser (CAP-012), legibilidad en papel (CAP-013), adaptadores de fuentes PDF/tablas (CAP-008), eventos mínimos (CAP-015). Rust sólo si una medición lo justifica; el núcleo sigue en C#/.NET.
