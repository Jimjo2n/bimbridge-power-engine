# Decisiones de arquitectura 0.6.1

**ADR-001 Lease por bloqueo de archivo, no mutex nombrado.** Un mutex nombrado no existe en Linux y no se puede probar en CI con procesos reales; el bloqueo de archivo sí, y vive junto a la tarea. Alcance: una PC.

**ADR-002 Fencing en `lease.json` y liberar sin borrar.** Borrar el archivo reiniciaba la cuenta; marcar `liberada` mantiene la monotonía. `lease.json` antiguo = fencing 0 (compatible).

**ADR-003 Validación en el punto de autoridad del ejecutor.** El backend de Revit no recibe el token: la validación es inmediatamente antes de entregar cada lote, en el mismo hilo del ejecutor. Ventana residual documentada (un lote en curso).

**ADR-004 Auditoría separada de la telemetría.** La telemetría puede perder líneas para no frenar Revit; la auditoría es síncrona y su fallo bloquea el código que escribe.

**ADR-005 Política de capacidades estática en vez de sandbox.** .NET no ofrece sandbox en proceso; un proceso aparte sin API de Revit no sirve para recetas que escriben el modelo. Se adopta análisis semántico + perfiles + auditoría y se declara el límite.

**ADR-006 Sin Rust en 0.6.1.** Sólo diseño (RUST_INTEROP_DESIGN.md); no hay medición que muestre que el cálculo es el cuello de botella frente al hilo de Revit.
