# Informe de pruebas beta1

## Automatizadas (sin Revit; NO son validación nativa)
- **Suite:** 367/367. En alpha3 eran 359; se suman 8:
  - NRT-05 aprobada contra el add-in falso;
  - un guardado que no cambia el archivo produce FALLIDA;
  - 4 bloqueos previos a cualquier escritura: sin autorización, ruta distinta, workshared, documento modificado;
  - mapeo de estados;
  - `CopyOver` sobre un destino sólo lectura.
- **Repeticiones contra flakes:** 3 corridas completas adicionales, 367/367 en cada una (1 m 9 s – 1 m 31 s), sin fallos intermitentes.
- **Idempotencia y fallos de conexión** (pruebas existentes, siguen pasando):
  - timeout tras aplicar: se observa y no se repite, con 1 commit;
  - NRT-03 con caída del AgentHost en 5 puntos: reanuda sin commit duplicado;
  - la reanudación del mismo diario en NRT-05 hace 0 commits nuevos.
- **Compilación del add-in:** 2022, 2023, 2024, 2025 y 2026 (en `build.sh`). `verificar_disco` usa ElementId de 64 bits en 2024+ y `int` en 2022–2023.

## Nativo
| Prueba | Estado |
|---|---|
| NRT-05 (persistente, guardar, SHA, disco, duplicado) | **PENDIENTE**: requiere autorización del operador e instancia de prueba |

Criterio de APROBADA, todo verdadero en `nrt05_informe.json → comprobaciones`:
- `autorizacion_operador_requerida`, `estado_op_verificada`, `un_solo_commit`;
- `valores_tras_commit`, `mismo_documento`, `modificado_tras_commit`;
- `guardado_ok`, `sha256_cambio`, `sin_cambios_pendientes_tras_guardar`;
- `disco_valores_esperados`, `disco_sha_es_el_guardado`;
- `sin_commit_duplicado`, `reanudar_no_modifica`.

Un SHA igual antes y después del guardado nunca cuenta como éxito.
