# beta1 test report

## Automated (without Revit; NOT native validation)
- **Suite:** 367/367. In alpha3 there were 359; 8 are added:
  - NRT-05 passed against the fake add-in;
  - a save that does not change the file produces FAILED;
  - 4 blocks before any write: no authorization, different path, workshared, modified document;
  - state mapping;
  - `CopyOver` onto a read-only target.
- **Repeats against flakes:** 3 additional full runs, 367/367 in each (1 m 9 s – 1 m 31 s), no intermittent failures.
- **Idempotency and connection failures** (existing tests, still passing):
  - timeout after applying: observed and not repeated, with 1 commit;
  - NRT-03 with AgentHost crash at 5 points: resumes without a duplicate commit;
  - resuming the same journal in NRT-05 makes 0 new commits.
- **Add-in build:** 2022, 2023, 2024, 2025 and 2026 (in `build.sh`). `verificar_disco` uses 64-bit ElementId in 2024+ and `int` in 2022–2023.

## Native
| Test | Status |
|---|---|
| NRT-05 (persistent, save, SHA, disk, duplicate) | **PENDING**: requires operator authorization and a test instance |

PASSED criterion, all true in `nrt05_informe.json → comprobaciones`:
- `autorizacion_operador_requerida`, `estado_op_verificada`, `un_solo_commit`;
- `valores_tras_commit`, `mismo_documento`, `modificado_tras_commit`;
- `guardado_ok`, `sha256_cambio`, `sin_cambios_pendientes_tras_guardar`;
- `disco_valores_esperados`, `disco_sha_es_el_guardado`;
- `sin_commit_duplicado`, `reanudar_no_modifica`.

An identical SHA before and after the save never counts as success.
