# Campaña nativa BIMBRIDGE 0.8.0

**Estado de todos los ítems: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION.** No se ejecuta nada hasta una autorización explícita nueva del operador. Nada de esta campaña se ha corrido en Revit.

## Reglas

- Sólo copias **desechables, locales y no workshared** de un modelo **público** (por ejemplo, una muestra de Autodesk). Nada corporativo, nada de ACC/BIM 360, ninguna central.
- Nunca sobre el Revit de trabajo del operador: una instancia aparte, abierta por operator, con la copia; el PID se toma de `revit instancias` y lo confirma operator.
- Cada comando lleva `PDI_REVIT_NATIVO=AUTORIZADO` y `--revit-disponible` (si falta, el arnés imprime el estado bloqueado y sale con código 3).
- Los pasos de escritura exigen `--autorizo-persistente` y `--ruta-esperada` = la ruta real de la copia; si la ruta no coincide o el documento es workshared, el ítem queda BLOQUEADO sin enviar ningún paso.
- No se guarda, sincroniza, cierra ni reinicia Revit salvo en los ítems que lo dicen (N8-12 exporta; N8-13 repite NRT-05, que guarda la copia).
- Instalación: operator instala a mano `BIMBRIDGE-0.8.0-actualizacion.zip` con **BIMBRIDGE → Developer Tools → Actualizar desde ZIP** (sin firma: pedirá confirmación doble) o con el instalador. Esa instalación es en sí la prueba N8-13c.

## Ítems

Comando base (PowerShell, desde la carpeta del AgentHost):

```
$env:PDI_REVIT_NATIVO="AUTORIZADO"
.\PDI.RevitMcp.AgentHost.exe revit campana080 --revit-disponible --revit-pid <PID> --ruta-esperada "<C:\...\copia.rvt>" --autorizo-persistente --pasos <N8-xx.json> --salida <C:\...\evidencia\N8-xx> [--var nombre=valor ...]
```

| Ítem | Qué demuestra | Estado inicial distinto | Arnés | Criterio de aprobación |
|---|---|---|---|---|
| N8-01 | Identidad correcta de documento | — | `N8-01.json` | contexto con la ruta autorizada; destino inexistente → `destino_no_disponible`; lectura sólo en el documento fijado |
| N8-02 | Vistas y plantillas | — | `N8-02.json` (`--var nivel=...`) | RCP creada, detalle medio, Lighting Devices ocultos; la vista aparece en el catálogo |
| N8-03 | Modificación de elementos | — | `N8-03.json` (`--var ids=a,b,c`) | comentario aplicado y releído; mover y devolver sin error |
| N8-04 | Dibujo 2D | — | `N8-04.json`, luego `N8-04b.json` | vista de dibujo con línea, rectángulo, polígono, arco, nota y región; tramo de 0,001 ft rechazado antes de Revit; componente de detalle y cota en N8-04b |
| N8-05 | Láminas y composición | después de N8-04 | `N8-05.json` (`--var vista_lamina=<id>`) | lámina creada con la vista; conjunto `BB-N8-05 SET` guardado con sus vistas verificadas; repetirlo sin `reemplazar` se rechaza |
| N8-06 | Familias externas | `carpetas_familias` = carpeta de prueba con un RFA público | `N8-06.json` | búsqueda sólo en la carpeta; ruta fuera rechazada; carga con sha-256; familia en el catálogo |
| N8-07 | Operaciones por lotes | — | `N8-07.json` (50–200 ids) + `revit nrt06` + B0 nativo (abajo) | un commit por lote, valores releídos; NRT-06 11/11 |
| N8-08 | Autorización y rechazo | `"permisos": ["leer","modificar_proyecto","interfaz"]`, Revit reiniciado | `N8-08.json` | código dinámico, sincronizar, exportar y cargar archivo → `rechazado` con su clase; modificar proyecto → ok |
| N8-09 | Recuperación | — | `revit nrt03` y `revit nrt07` (copia distinta de la de N8-13) | NRT-03 15/15; NRT-07: respuesta perdida registrada como incierta, un solo commit, verificada por observación |
| N8-10 | Validación independiente | puertas con `Mark` sembrado: dos esperadas, una omitida (D-999), una extra justificada | `ejecutar N8-10_plan.json --revit-pid <PID> --escritura lectura` (clave y huella del N8-01) | estado `diferencias` con D-999 omitida; la excepción justificada no cuenta; si `fuente_construccion` = `fuente_id` → `no_independiente`; cero commits |
| N8-11 | Visibilidad y representación | una vista con luminarias | `N8-11.json` (`--var vista=<id>`) | auditoría por vista y captura PNG sin error |
| N8-12 | Entregables | después de N8-05; exportar permitido | `N8-12.json` (`--var carpeta_pdf=...`) | PDF creado; luego `DeliveryTrack`: páginas = láminas del conjunto, sha del archivo = sha del recibo |
| N8-13 | Regresión del núcleo 0.7.0 | copia nueva | `revit nrt05`, `revit nrt06`, `revit nrt03` (mismos parámetros que rc1) + instalación por ZIP (N8-13c) | mismos resultados que rc1 sobre el build 0.8.0; auditoria.jsonl con intención y resultado de cada commit (cierra H2); sin temporales residuales en `%LOCALAPPDATA%\PDI.RevitMcp` |

B0 nativo (opcional, dentro de N8-07): 586 elementos de una copia con al menos esa cantidad; uno a uno con la herramienta del puente frente a un plan de 3 lotes; y el mismo trabajo con un script de pyRevit para la comparación B1–B8. Hasta entonces B0 es MEDIDO_SIMULADO y B1–B8 NO_MEDIDO.

## Evidencia que se devuelve

Por ítem: `<item>_informe.json` (pasos, estados, motivos, métricas por cable), `llamadas.jsonl`. Para N8-09/N8-13: los informes NRT habituales. Al final, un zip con todas las carpetas `evidencia\N8-*`, sin el .rvt.

## Puerta de salida

0.8.0 no se declara aprobada si falla un ítem crítico (N8-01, N8-03, N8-08, N8-09, N8-10, N8-13), si hay duda sobre el documento, si un permiso se puede eludir, si hay commits duplicados, si un resultado incierto se confirma sin observar, o si alguna capacidad se declara nativa sin su informe.
