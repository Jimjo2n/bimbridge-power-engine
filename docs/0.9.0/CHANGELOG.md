# BIMBRIDGE 0.9.0 — registro de cambios de ingeniería

## Revisión consolidada Power Engine (2026-11-09, base b944e6e)

- **C# dinámico habilitado por defecto**, con el control único y persistente «Bloquear inyección de código C#». Se eliminan «Código confiable» (4 h) y la autorización por SHA en `exec`. Ver DYNAMIC_CODE_EXECUTION_POLICY.md.
- **Rebranding visible a «BIMBRIDGE - Power Engine»:** diálogos, ventanas, cinta («Power Engine | Estado»), add-in, servidor MCP, Aplicaciones de Windows, instalador e historial de transacciones. El prefijo heredado sigue reconociéndose. Hay una prueba que impide nuevas cadenas visibles «PDI MCP».
- **Identidad de agentes:** alias declarado (`BIMBRIDGE_AGENTE` o `ctx agente=`), principal verificado (usuario de Windows), contadores por sesión, historial de desconectadas y atribución en el diario. Ver AGENT_IDENTITY_PROTOCOL.md.
- **Nuevo panel Estado:** resumen, agentes, seguridad y recuperación, rendimiento (mediana y p95) y actividad atribuida, con exportación CSV y JSONL. Ver POWER_ENGINE_OBSERVABILITY.md.
- **Rendimiento:** `commit ops=…` (plan + commit en un viaje), conteo rápido con `GetElementCount`, caché de nombres por consulta y `ctx` compacto.
- **Pruebas:** la de extremo a extremo ya no lee el `config.json` real.

## Revisión consolidada (2026-11-05, base 208cb2f)

Estado: NATIVE_ACCEPTANCE_PENDING.

- Escritura fail-closed: `PlanStore.CheckWritable` lee el disco y bloquea commit, cola, exec, act, fam, doc y tarea iniciar/reanudar con evidencia sin reconciliar, archivos dañados o errores de lectura.
- Intención durable antes de abrir el grupo (`BeginWrite`) y punto de control tras cada lote (`Checkpoint`). Sin disco no se escribe.
- `job op=pendientes` y `job op=reconciliar`: liberan sólo con el modelo como evidencia y archivan en `reconciliados\`. Vía humana con Revit cerrado: `AgentHost plan-store`.
- `PlanStore.Finish` devuelve y anota los fallos de disco. Una excepción al aplicar la cola ya no se declara revertida.
- Ensayo: `clasificacion` con cuatro resultados. Commit informa `espera_cola_ms` y `revit_ms`.
- Instalador: /quiet, /actualizar y la ventana exigen Revit cerrado y manifiesto; /quiet sobre una instalación existente da código 5 sin `/reemplazar`; el retorno sólo restaura la sección BIMBRIDGE de `.codex\config.toml`, con copia `.bak` antes de cambiarlo. Cambio de comportamiento: el botón Actualizar con Revit abierto ya no reemplaza archivos.

### Tras la campaña nativa (2026-11-05)

- Instalación atómica vista desde fuera: si falla a mitad, vuelve sola al respaldo; si el puente está en uso, se detiene antes de cambiar nada.
- Una escritura con la conexión perdida responde `desconocido`, no `revit_no_disponible`.
- `job op=pendientes` sólo muestra el código de bloqueo cuando hay bloqueo.

## Mejoras de integración

- **D32 · Worker Windows.** Se resuelve un ejecutable real en PATH o desde la ruta configurada. Las rutas WindowsApps se descartan antes del arranque. El fallo de saludo informa fase, ejecutable por nombre, código de salida observado y hasta 2048 caracteres de stderr. El protocolo de Python escribe JSONL explícitamente en UTF-8.
- **D33 · Consultas.** `q … digest=true` devuelve total y SHA-256 de todas las filas filtradas. El formato versionado conserva multiplicidad y textos completos y ordena filas para comparar corridas. `timing=true` desglosa el tiempo en el puente, el despacho, el hilo de Revit y el procesamiento de resultados. Conteos y agrupaciones omiten la lectura de campos de presentación innecesarios.
- **D34 · PDF.** `pdf grids … sin_globo=true` habilita una inferencia optativa por rótulos en extremos de líneas largas. Exige cruces y se activa en páginas sin globos reconocidos. El modo estricto sigue siendo el predeterminado.
- **D35 · Escritura reversible.** Se reutiliza el ensayo de planes tipados. La verificación temporal usa rollback en `finally`. Se comprueba el estado devuelto y los fallos inciertos se mantienen visibles como `desconocido`.
- **D36 · Métricas.** El resumen CSV informa caracteres, estimación, tokens reales informados por el cliente y diferencia para pares comparables.
