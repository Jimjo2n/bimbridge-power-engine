# BIMBRIDGE 0.9.0 — estado

**Estado:** candidato interno **NATIVE_ACCEPTANCE_PENDING**. Rama `feature/0.9.0` desde 0.8.0 (2413aec), que también sigue NATIVE_ACCEPTANCE_PENDING. Nada de 0.8.0 ni de 0.9.0 se ha ejecutado en Revit: toda prueba nativa está **BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION**. Los números de este documento salen de pruebas sin Revit o de mediciones en un contenedor Linux; ninguno es evidencia nativa.

Arquitectura: [POWER_ENGINE_ARCHITECTURE.md](POWER_ENGINE_ARCHITECTURE.md). Trazabilidad: [MATRIZ_TRAZABILIDAD_0.9.0.md](MATRIZ_TRAZABILIDAD_0.9.0.md). Campaña nativa: `tools/aceptacion/0.9.0/PROCEDIMIENTO_0.9.0.md`.

## 1. Correcciones de 0.8.0

| Id | Corrección | Prueba |
|---|---|---|
| P0-1 | Actualizaciones con procedencia criptográfica (RSA-SHA256 contra claves de confianza) obligatoria en producción; la firma y los hashes se re-verifican antes de instalar | ZipUpdateTests (16) |
| P0-2 | Paquete sin firma sólo en el perfil de laboratorio con `actualizacion_sin_firma` explícito; un paquete preparado en laboratorio se rechaza en producción | ZipUpdateTests |
| P0-3 | Independencia QA/QC demostrable: SHA-256 y linaje distintos de la construcción; si no, `no_independiente` | Hardening090Tests, PdfToModelWorkflowTests |
| P0-4 | Recibos críticos de exportación y entrega con revisión, origen y evidencia COMPROBADO | Hardening090Tests |
| P1-5 | Draw2D: geometría degenerada y tramos colineales superpuestos rechazados | Hardening090Tests |
| P1-6 | Prueba intermitente de Retener (HeldCommit) estabilizada | suite completa |
| P1-7 | Referencias B1/B8 corregidas frente al procedimiento | Hardening090Tests |

Cambio de comportamiento: en producción, `exec ejecutar/ensayo` exige el SHA-256 del código en `codigo_autorizado`.

## 2. Nuevo en 0.9.0

- **Power Engine** (C# núcleo, Python documentos, Rust geometría) con contrato pe=1, despachador por coste medido, registro de capacidades y degradación controlada.
- **Python Document Worker**: `pdf_info`, `pdf_extract`, `pdf_tables`, `pdf_symbols`, `pdf_grids`, `docx_extract`.
- **Rust Geometry Engine** por FFI (DLL) e IPC (proceso): `transform3`, `nearest`, `match`, `duplicates`, `segment_contacts`, `components`.
- **Flujo PDF → modelo**: ejes y anclas, registro plano–modelo, plan de colocación determinista, ops `place` para `plan`→`commit`, verificación independiente.
- **Motores**: red MEP en tres capas, montantes (diagrama ≠ físico), grupos y variantes, distribución de etiquetas.
- **Herramienta `power`** en el perfil compacto: `cap`, `estado`, `pdf …`, `docx`, `flujo`, `red`. No cambia el modelo.
- **AgentHost**: `power-engine estado | flujo-pdf | bench | calibrar`.
- **Instalador y build**: DLL y exe de Rust para Windows y Python Worker con dependencias puras fijadas por SHA-256 se instalan junto al puente, verificados por hash.

## 3. Pruebas

| Conjunto | Resultado |
|---|---|
| C# (xUnit, sin Revit) | **505/505** |
| Python Worker (unittest, subproceso real) | **30/30** |
| Rust (cargo test) | **6/6** |
| Add-in | compila para Revit 2022–2026; los scripts `*_090.cs` compilan contra los 5 años |
| Nativas 0.9.0 | **0 ejecutadas**. N9-01…N9-06 y benchmark en el PC: BLOCKED |
| BIMBRIDGE frente a pyRevit (B1–B8) | DEFERRED: comparación reproducible pendiente en un entorno aislado |

## 4. Rendimiento medido (contenedor Linux x64, 4 CPU; no es el equipo de prueba)

Mediana de 5 rondas, ms. Archivo completo: `bench_power_engine_2026-11-04_linux4cpu.json`.

| Trabajo | n | C# | Rust FFI | Rust IPC | FFI frente a C# |
|---|---|---|---|---|---|
| nearest | 1 000 | 1,33 | 0,16 | 2,20 | ×8,1 |
| nearest | 100 000 | 85,0 | 13,2 | — | ×6,5 |
| match | 100 000 | 66,6 | 19,3 | — | ×3,5 |
| duplicates | 100 000 | 87,1 | 35,3 | — | ×2,5 |
| segment_contacts | 20 000 | 37,6 | 3,3 | 32,6 | ×11,3 |
| transform3 | 100 000 | 1,87 | 2,21 | — | ×0,85 |
| components | 100 000 | 4,6 | 3,6 | — | ×1,3 |

- Coste fijo por llamada: C# 0,0002 ms, Rust FFI 0,0002 ms, Rust IPC 0,14 ms. IPC sólo compensa para aislar un fallo, no por velocidad.
- Calibración por defecto (medida, 3 campañas): Rust FFI desde n=100 en nearest, match, duplicates y segment_contacts; transform3 y components se quedan en C# (Rust no gana en todos los tamaños). En el PC se recalibra con `power-engine calibrar`.
- Documentos: DOCX .NET 0,19 ms frente a Python 0,92 ms (el despachador usa .NET si no hay Python); arranque de Python más primer trabajo 71 ms; PDF sintético de una lámina 28–34 ms por trabajo.
- Flujo PDF→plan completo (dos trabajos Python + registro + plan) en las pruebas: segundos de suite, sin medición aislada en el PC.

## 5. Eficiencia de tokens (ESTIMADOS = caracteres/4; no son tokens reales del cliente)

| Caso | Antes | Ahora | Reducción |
|---|---|---|---|
| Descubrimiento: catálogo completo frente a búsqueda + una ficha | 2 253 | 402 | 82 % |
| Colocar 72 luminarias: 72 órdenes sueltas frente a un lote | 10 260 caracteres, 72 llamadas | 959 caracteres, 1 llamada | 91 % de caracteres, 99 % de llamadas |
| `power flujo`: respuesta sin ítems uno a uno (sólo resumen, ambiguos y ops) | — | < 20 000 caracteres para 72 elementos | — |
| Coste fijo de la herramienta `power` en la lista de herramientas | 6 778 caracteres | ≈ 7 400 | +9 % |

La verificación no se recorta para ahorrar tokens: el diff es compacto, pero cada diferencia se comprueba.

## 6. Limitaciones

- Ninguna operación se ha ejecutado en Revit 0.9.0; colocación, etiquetas y lecturas nativas pueden diferir por versión.
- PDF: sólo vectoriales. Escaneados → `SIN_CONTENIDO` (interpretación por visión requiere autorización). Tablas sin reglas no se detectan. Símbolos son candidatos, no luminarias confirmadas.
- Registro: necesita ejes con burbuja y nombre igual en plano y modelo; ejes curvos no son anclas.
- `mep.riser`, `groups.variants` y `annot.layout` sólo como biblioteca (sin orden `power` todavía); `annot.layout` es EXPERIMENTAL.
- El intérprete de Python no se empaqueta; PDF con cifrado AES requiere `cryptography` en el equipo.
- Benchmarks medidos en Linux; el PC con Windows puede dar otros umbrales.
- B1–B8 frente a pyRevit: NO_MEDIDO.

## 7. Siguiente fase

1. Con autorización: campaña N9 en una copia desechable y benchmark en el PC.
2. Órdenes `power` para montantes, grupos y anotaciones, y lectura nativa de ejes y conduit integrada en `flujo`.
3. Tablas sin reglas y lectura de leyendas multilínea.
4. B1–B8 frente a pyRevit requieren un entorno aislado, versiones identificadas y cargas comparables.
