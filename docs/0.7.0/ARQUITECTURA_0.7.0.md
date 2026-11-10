# PDI.RevitMcp 0.7.0-alpha1 — Arquitectura de ejecución autónoma

Base: v0.6.1-rc3 (commit 7be12ad). Rama: `feature/0.7.0-agenthost`. Nada de rc3 fue modificado (`git diff 7be12ad -- src/` solo toca `src/PDI.RevitMcp.AgentHost/`).
**Las escrituras van SOLO a un backend simulado. Revit no se toca; el adaptador Revit tiene escrituras deshabilitadas.**

## Capas

```
documento (no confiable) ─► RequirementExtractor ─► RequirementInterpreter ─► PlanBuilder ─► PlanDraft (no confiable)
                                                                                              │ IrValidator (catálogo OpSpec)
                                                                                              ▼
                                                                                   ValidatedPlan (inmutable, hash)
                                                                                              │
           AgentHostService (cola acotada, tubería CurrentUserOnly) ──► PlanRunner ──► IExecutionBackend
                                                                         │   │             ├─ SimulatedBackend → DurableSimModel (WAL + snapshot, en disco)
                                                                         │   │             └─ RevitTaskAdapter (mapea a recetas rc3; escribir = rechazado)
                                                                         │   └─ PlanLease (= TaskStore/lease de Engine rc3, con fencing)
                                                                         └─ ExecJournal: diario.jsonl (autoridad, cadena hash) + estado.json (checkpoint sellado)
```

## IR (esquema 2) — `Ir.cs`, `IrValidator.cs`, `PlanGraph.cs`
- Plan inmutable (`ValidatedPlan`/`ValidatedOp`, diccionarios congelados en profundidad). Solo el estado de ejecución es mutable.
- Id de operación = `op-` + 20 hex de sha256(doc, huella, alcance, operación, parámetros, identidad de fuente). Misma intención con otra fuente ⇒ otro id. Clave semántica detecta duplicados.
- Autorización, postcondiciones, idempotencia y clase de reintento se **derivan** del catálogo OpSpec; el plan no puede declararlas (`campo_no_declarable`).
- Unidades de longitud y sistema de coordenadas obligatorios; un valor numérico exige `unidades`.
- Dependencias: orden de Kahn determinista; ciclos rechazados.
- Precondición soportada: `objetivos` min/max. `permitir_vacio` explícito (AH-05). Reintentos 0–3.

## Ejecución durable — `Exec/`
- **Diario** (`ChainLog`): JSONL encadenado (seq, prev, h), cada línea con WriteThrough + Flush(true). Cola rota ⇒ se recorta; corrupción en medio ⇒ error `estado_corrupto`, no se recupera (AH-03).
- **Checkpoint** `estado.json`: derivado y sellado; si no coincide con el diario se descarta y manda el diario.
- Validación previa a la recuperación (AH-03): fencing, transiciones legales, ops conocidas, estados conocidos, esquema, plan/documento.
- **Recuperación con postcondiciones (AH-01)**: una operación encontrada `en_ejecucion`/`ejecutada` tras una caída se observa en el modelo (`Observe` → Observado/NoObservado/Parcial/Incierto/SinObjetivos). Solo se marca `verificada` si `CheckPostconditions` las satisface; efecto parcial o incierto ⇒ `requiere_reconciliar`; efecto probado sin evidencia ⇒ `requiere_verificacion`. Nunca se re-ejecuta algo ya confirmado.
- **Propiedad exclusiva (AH-04)**: `PlanLease` reutiliza el lease de Engine rc3 (`TaskStore`, id "plan"), TTL por defecto 30 s, renovación a TTL/3. Cada dueño recibe un fencing creciente que se escribe en el diario y que el modelo exige en cada commit; un zombi con fencing viejo es rechazado.
- **Conjunto vacío (AH-05)**: sin objetivos ⇒ `fallida(sin_objetivos)` sin escribir, salvo `permitir_vacio=true` ⇒ verificada con evidencia "0 objetivos".

## Modelo simulado durable (AH-02) — `Sim/`
`modelo.json` (snapshot sellado, gen, tx por op, fencing) + `modelo.<gen>.wal` (una línea por transacción). Sobrevive a procesos hijos reales (las pruebas lanzan `dotnet AgentHost.dll` y lo matan con SIGKILL). Fallos configurables: rechazar, transitorio, desviaciones de QC, retardo.

## Servicio — `Service/`
`BackgroundService` (.NET Generic Host, no servicio de Windows) con `Channel` acotado, N trabajadores, tubería con nombre `CurrentUserOnly`, JSON por línea. Comandos: salud, enviar, lista, resumen, siguiente, excepciones, cambios, fallos, informe, pausar, reanudar, cancelar, apagar. Al arrancar re-encola los planes activos con trabajo pendiente. Apagado ordenado: termina la operación en curso, checkpoint, libera lease.

## Documentos — `Documents/DocumentPipeline.cs`
El contenido del documento es dato no confiable. Se extraen requisitos con página/sección/texto original y sha256. Solo lo inequívoco se convierte; lo demás queda en revisión con motivo (`lenguaje_ambiguo`, `unidades_no_explicitas`, `contenido_sospechoso`, `categoria_desconocida`, `dependencia_no_ejecutable`, `duplicado_de`, `alcance_sin_nivel`). `VerifySources` detecta fuente alterada.

## Adaptador Revit — `Revit/RevitTaskAdapter.cs`
Traduce operaciones validadas a `TaskSubmission` de las recetas rc3 (`parametro_por_lote`, `qc_manifiesto`, …) con clave de idempotencia = id de op y concesión requerida `revit_modificar`. `WritesEnabled=false`; `Execute` devuelve `sin_conexion_alpha1`. No hay acceso a objetos de la API de Revit.

## Kernel candidato (P2) — `Kernels/MountKernelBench.cs`
Ver RENDIMIENTO_0.7.0.md. No se implementó Rust.
