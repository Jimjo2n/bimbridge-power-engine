# Pruebas de aceptación en Revit (0.5.0): pendientes de ejecutar

Nada de esto se ha corrido todavía. Se ejecuta en el equipo de prueba, Revit 2024, **sólo con el modelo de práctica** y una copia del RFA del lavamanos.
Cada paso dice qué se escribe a la IA (perfil compacto) y qué cuenta como aprobado. Si un paso falla, se anota el texto exacto de la respuesta.

## 0. Preparación
1. Instalar `PDI.RevitMcp.Setup.exe` 0.5.0 (zip `PDI.RevitMcp.Setup_0.5.0.zip`). Comprobar el SHA-256 contra `MANIFIESTO_0.5.0.txt`.
2. Abrir Revit 2024 con el modelo de práctica. Modo **En vivo** (Retener rechaza exec y familias con `necesita_usuario`, a propósito).
3. `revit "ctx"`: aprobado si muestra `addin 0.5.0`, `protocolo 2`, `runner auto`, la lista `capacidades` y la ruta del compilador sin error.

## 1. Runner general (A02)
- `exec codigo="doc.Title"`: devuelve el título; `modo consulta`; tiempo de compilación en frío anotado.
- Repetir el mismo código: `en_cache` verdadero y `compilar_ms` 0.
- `exec codigo="return noExiste;"`: `error CS0103 l1`.
- `exec modo=ejecutar codigo="var w = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement(); w.LookupParameter(\"Comments\").Set(\"MCP\"); return w.Id;"` → `cambios` incluye ese id; Ctrl+Z en Revit lo deshace en un paso.

## 2. Lavamanos (A03, A04, A05)
Datos fijos: **radio** 6.5 mm (no diámetro). Mirando el aparato de frente: **fría a la derecha, caliente a la izquierda**. Flujo **In** o **Bidirectional** está sin decidir: no se pasa `flujo=` hasta que el operador lo decida.

1. `revit "fam info familia=<lavamanos>"` y `revit "fam caras familia=<lavamanos>"`: anotar conectores existentes y elegir las dos caras (normal y centro).
2. `act "abrir familia=<lavamanos>"` → handle `dN` del RFA.
3. Camino tipado:
   `act "conector nombre=fria sistema=fria cara=<e:n> radio=6.5mm doc=dN clave=lav-fria"`
   `act "conector nombre=caliente sistema=caliente cara=<e:n> radio=6.5mm doc=dN clave=lav-cal"`
   Aprobado: radio 6.5 mm exacto, sistema DomesticColdWater / DomesticHotWater, cara y normal correctas, `pendientes` menciona el flujo.
4. Camino general (mismo resultado con exec): `exec modo=ejecutar tx=auto doc=dN args={"cara_fria":"<e:n>","cara_caliente":"<e:n>","radio_mm":6.5}` con el código de `lavamanos_exec.cs`. Aprobado: `n_fria=1`, `n_caliente=1`, `lado_correcto=true` (o `frente=` corregido si la familia no usa -Y como frente).
5. `act "guardar doc=dN"` → archivo con hash nuevo. `act "cargar en=d1 doc=dN sobrescribir=valores"` → relectura desde el proyecto con los dos conectores.
6. **Repetir 3–5** (mismas claves y luego claves nuevas): no aparece ningún conector duplicado; las claves repetidas devuelven el resultado anterior.
7. Cerrar Revit sin guardar el proyecto y reabrir: `fam info` sigue mostrando un solo `fria` y un solo `caliente`.

## 3. Trabajo largo (A10, A13–A15, A24)
Sólo después de que el operador confirme el alcance: luces + grupos + vistas en el modelo de práctica, en planes de ≤200 operaciones con `tarea=practica-larga` y `clave=` por lote; al final `act "pdf conjunto=\"NON-UNIT LIGHTING\""`. Aprobado: PDF nuevo (fecha, hash y páginas verificados), `chk lighting_audit tags=uno` sin diferencias en el alcance pedido.

## 4. Recuperación (A09, A11)
1. Lanzar un commit grande con `clave=rec-1 tarea=practica-larga` y cerrar la conversación antes de la respuesta.
2. Conversación nueva: `revit "job tarea=practica-larga"` muestra el trabajo; repetir el commit con `clave=rec-1` → `ya_aplicado` (no se aplica dos veces).
3. Cerrar Revit a mitad de un exec con `clave=rec-2`; reabrir y repetir → `interrumpido`; tras revisar, `reconciliar=si`.

## 5. Comparación con pyRevit (R068/R069)
Mismo modelo, misma operación (lavamanos y una auditoría de luces), 1 vez en frío y 10 en caliente con cada motor; anotar p50 y p95 y si el QC final coincide. Sin esta ejecución no hay benchmark equivalente.
