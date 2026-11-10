# Hoja de ruta tras 0.7.0-alpha1

**alpha2 (sin Revit)**
- Ejecutar la suite en Windows (Revit 2024/2026 instalados pero sin abrir modelos).
- Corregir la carrera de clave de GrantStore (requiere aprobación del operador, toca rc3).
- Extracción PDF de especificaciones con página/sección; más patrones de requisitos.
- Precondiciones adicionales (valor actual, existencia de parámetro, tipo).

**beta1 (Revit, solo lectura, copia de práctica, con aprobación)**
- Conectar `RevitTaskAdapter.Observe/Prepare` al add-in rc3 en modo lectura.
- Comparar observación simulada vs real en el mismo plan.

**beta2 (escritura en copia de práctica, con aprobación explícita del operador)**
- `Execute` vía recetas rc3 con concesión `revit_modificar`; recuperación AH-01 contra el diario de transacciones de rc3.

**Rust**: solo si una medición en Revit real muestra que el cálculo (no la extracción) domina; usar el banco y digest de RENDIMIENTO.
