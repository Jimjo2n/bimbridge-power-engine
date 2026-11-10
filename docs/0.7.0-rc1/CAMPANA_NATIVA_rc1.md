# Campaña nativa rc1 — preparada, NO ejecutada

Estado: `BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION`. Solo empieza cuando operator diga explícitamente que Revit está disponible.

## Requisitos del entorno
- Instancia de Revit 2024 **dedicada** a la prueba, distinta de la de trabajo. La de trabajo no se toca ni se conecta.
- Modelo **desechable**:
  - copia local de `DynamoSample_2024.rvt`, no workshared y sin ACC ni BIM 360;
  - una copia limpia por prueba: `NRT05_*.rvt`, `NRT03_*.rvt` y `NRT06_*.rvt`;
  - IsModified=false al abrir, y al menos 5 puertas con Comments editable.
- Add-in rc1 instalado con niveles RPW; Retener desactivado y cola vacía.

## Preflight (cada prueba)
1. `set PDI_REVIT_NATIVO=AUTORIZADO`.
2. `agenthost revit instancias --revit-disponible` muestra un único PID de prueba.
3. `agenthost revit identidad --revit-pid N --revit-disponible` debe dar:
   - la ruta igual a la copia desechable;
   - worksharing=false y modificado=false;
   - la huella presente;
   - la versión del add-in igual a `0.7.0-rc1+<commit>`.

## Pruebas (una corrida cada una; si falla, se corrige y se repite sin límite artificial)
| Prueba | Comando | Criterio de APROBADA |
|---|---|---|
| NRT-05 regresión | `revit nrt05 --revit-pid N --ruta-esperada <NRT05.rvt> --autorizo-persistente --salida …\nrt05 --categoria Doors --ids a,b,c,d,e --revit-disponible` | 13/13 comprobaciones |
| NRT-03 | `revit nrt03 … --ruta-esperada <NRT03.rvt> --autorizo-persistente --salida …\nrt03 --categoria Doors --ids a,b,c --revit-disponible` | En las 5 rondas (antes_enviar, tras_commit, runner.tras_ejecutar, en_verificacion, tras_verificar): cayó, quedó verificada y hubo exactamente 1 commit. El AgentHost "cae" por excepción inyectada; Revit nunca se mata. |
| NRT-06 | `revit nrt06 … --ruta-esperada <NRT06.rvt> --autorizo-persistente --salida …\nrt06 --categoria Doors --ids a,b,c,d,e --revit-disponible` | Debe cumplir todo esto: plan válido; rechazo sin autorización; rechazo fuera de alcance sin commit; consulta, lote y fijar verificados; QC ejecutado con evidencia; exactamente 2 commits; relectura independiente correcta; reanudar sin escritura; sin guardar ni sync |

Para el QC de NRT-06 se exige que **se ejecute y deje evidencia**, no que dé 0 desviaciones: el modelo de muestra puede tener luminarias sin etiqueta reales.

## Reversión
- NRT-03 y NRT-06 no guardan. Al terminar, el operador cierra la instancia de prueba **sin guardar**.
- NRT-05 guarda solo su copia desechable.
- Si algo queda incierto (`requiere_verificacion` o `requiere_reconciliar`), no se repite: se informa con el diario.

## Manifiesto de evidencias (por prueba, en `--salida`)
- `<prueba>_informe.json`, `llamadas.jsonl` (ms y bytes por llamada), `plan_ir.json` y `estado*/diario.jsonl`.
- En NRT-03, una carpeta `rondaN` por cada punto de caída.
- Al final, `SHA256_campaña.txt` con el hash de cada archivo.
