# BIMBRIDGE 0.9.1 — estado de entrega (rama `feat/revit-functional-coverage-0.9.1`, PR #29)

Estado: **candidato para nueva auditoría, sin campaña nativa**. Nada es NATIVE_VERIFIED. La versión del producto sigue en 0.9.0+commit (#23).

## Inventario
- **Símbolos públicos de la API** (RevitAPI + RevitAPIUI, Nice3point 2022.1.80 … 2026.4.10): 31.592 / 31.924 / 32.621 / 33.118 / 33.873 por versión
  (tipos, constructores, métodos con firma completa, propiedades, campos, eventos, enums, herencia, obsoletos).
  Listados gzip deterministas con SHA-256 del ensamblado y del listado: `docs/0.9.1/inventory/revit-symbols-*`. Reproducible (dos ejecuciones idénticas).
  Diferencias: 2022→23 +755/−423, 23→24 +823/−126, 24→25 +754/−257, 25→26 +867/−112. Presencia técnica: no se infiere semántica.
- **Capacidades**: 222 (`docs/0.9.1/capabilities/coverage.md`): OFFLINE_VERIFIED 12, IMPLEMENTED_UNVERIFIED 72, DYNAMIC_CSHARP_ONLY 92, MISSING 7,
  UNKNOWN 32, UNSUPPORTED_BY_PUBLIC_API 2, NOT_APPLICABLE 5.
- **Comandos de la cinta**: 596, todos enlazados a una capacidad. Por estado: IMPLEMENTED_UNVERIFIED 91, DYNAMIC_CSHARP_ONLY 256, UNKNOWN 189
  (analizados, con investigación pendiente registrada), NOT_APPLICABLE 48 (interfaz/sesión), UNSUPPORTED 4, MISSING 8.
  DYNAMIC_CSHARP_ONLY = hay API pública verificada en el listado; no es automatización completa.
- **Descubrimiento**: `check name=cobertura` (filas compactas paginadas; contrato completo por id). Índice inmutable, sin reflexión ni hilo de Revit.
  Benchmarks: `docs/0.9.1/benchmarks/inventory-bench.md` (catálogo real: construcción ≈1,7 ms, 87 KB retenidos; 10.000 sintéticas: ≈41 ms, 2,2 MB;
  página de descubrimiento ≈0,4–1,7 KB ≈100–430 tokens estimados frente a 6,3 MB del catálogo completo de 10.000).

## Hallazgos de auditoría corregidos (d772755)
AUD-06 NaN/parámetros ausentes; AUD-07 recibos con bloqueo entre procesos, evidencia privilegiada e integración en `check copy_monitor`;
AUD-08 dimensiones separadas y continuidad tras traspaso manual; AUD-09 commit ≠ postcondiciones (`con_diferencias`);
AUD-10 fragmento durable antes de truncar con inyección de fallos; AUD-11 transformación completa (rotación, reflexión, traslación; rechaza escala, cizalla, inclinación).

## Bloques
| Bloque | Commits | Contenido |
|---|---|---|
| A | cf5acde…f70ed73 | AUD-01…05, #27, #28 |
| B | 5143dc1, 2f64467, 463f32e, be4a880 | inventario completo, índice, cobertura F01–F37 |
| C/D/E | 990ae2a | 10 operaciones tipadas con verificación de lo creado |
| F | 1948fda, d772755 | recibos, capacidades nativas, Copy/Monitor, destino, diagnóstico |
| G | 03f6d15 | `doc ifc`, `doc tabla_csv` |
| H | be4a880 | `check name=model_health` |
| I | 76c10b9 | historial, cancelación, consejo de reintento (`check name=historial`) |

## Decisiones
D54 operaciones de modelado; D55 flujos por evidencia; D56 Copy/Monitor sin API de copia por lotes; D57 IFC con transacción revertida;
D58 inventario completo en gzip determinista y trazable; D59 índice inmutable y descubrimiento paginado; D60 UNKNOWN con investigación pendiente en lugar de promociones por nombre;
D61 recibos con bloqueo por intento y evidencia privilegiada; D62 historial con consejo de reintento.

## Pendiente (explícito)
- 189 comandos UNKNOWN: cada uno con su motivo en `commands.json` (`pending_research`).
- Panel de estado (#5): mostrar historial y cancelación en la interfaz; los datos ya existen.
- PDF→Revit: el plan desde PDF existe (F37-PDF-PLAN-001); su ejecución en Revit queda para la campaña nativa.
- Scheduler multiagente con prioridades: no implementado (sin perfiles nativos que lo justifiquen).
- Campaña nativa única: requiere autorización de instancia y modelo de prueba desechable.
