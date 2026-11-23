# 0.7.0 — Bimbridge.AgentHost: initial boundaries

Minimal design to start 0.7.0 without using capabilities that 0.6.1 leaves restricted. Not implemented in 0.6.1.

## 1. Place in the architecture

`Claude/Codex → AgentHost (C#, its own process) → execution contracts → C# Engine in Revit (+ compute workers)`

AgentHost has **no** access to the Revit API or to grants: it talks to the add-in over the same bridge (per-user pipe), as a client like any other. Everything that modifies the model goes through `tarea` and therefore through a person's grants, two-phase audit, leases and reconciliation.

## 2. Intermediate representation (BIM Task IR), schema 1

```json
{
  "ir": 1,
  "id": "op-7f3a…",                       // stable: sha256 of (source + scope + operation + canonical parameters)
  "documento": {"clave": "archivo:c:\\…\\practica.rvt", "huella": "…"},
  "alcance": {"categoria": "OST_LightingFixtures", "nivel": "LEVEL 03", "ids": [], "consulta": "…"},
  "operacion": "fijar_parametro",          // from the closed catalog (= installed recipes); no free code
  "parametros": {"parametro": "Mounting Height", "valor": 2700, "unidades": "mm"},
  "precondiciones": ["RCP view exists", "untagged elements"],
  "depende_de": ["op-12ab…"],
  "autorizacion": ["revit_modificar"],     // computed, not declared by the agent
  "resultado_esperado": {"cambiados": 43},
  "verificacion": {"receta": "qc_manifiesto", "tolerancias": {"z": 25}},
  "reintento": "nunca_si_dudoso",          // idempotency: only what the engine proves did not stick is repeated
  "estado": "propuesta",                   // propuesta → validada → simulada → aprobada → en_tarea → verificada | rechazada
  "trazabilidad": [{"fuente": "E-1.3.03 p.1 region B2", "sha256": "…", "cita": "MH 22'-4\""}]
}
```

- `estado` only advances to `aprobada` with a real grant (the «Autorizar» button in Revit); AgentHost cannot create it.
- `operacion` compiles to `tarea iniciar receta=<installed>`; an IR that requires code outside the catalog stays in `propuesta` with the reason.

## 3. What can be built now (without restricted capabilities)

| Order | Work | Uses | Depends on |
|---|---|---|---|
| 1 | IR schema + validator (JSON Schema + tests) | nothing from Revit | — |
| 2 | Requirement extraction from documents with traceability (`fuente`, `sha256`, quote) | files | 1 |
| 3 | Dependency graph and topological order; cycle detection | memory | 1 |
| 4 | Read-only queries to the model via `revit`/`exec modo=consulta` (restricted profile) | bridge | — |
| 5 | Simulation: `qc_manifiesto` and `montaje_soportes` (installed read recipes) | bridge | 4 |
| 6 | Summarized context memory per task (`tarea resumen` already exists) and per document | disk | — |
| 7 | Simulated engine adapter (the tests' `FakeBackend`) to develop the orchestrator without Revit | tests | — |
| 8 | Claude/Codex coordination: proposal queue with an owner per IR, using the same leases with fencing | Engine | 3 |

## 4. What stays blocked until its conditions are met

- Autonomous writes on real models: require native acceptance D-01..D-12 on practice copies (see NATIVE_ACCEPTANCE_0.6.1.md).
- Automatic resumption of uncertain writes: never automatic; only `reconciliar` with evidence and a grant.
- Autonomous free code: only with approval of that recipe (`receta_externa`).
- Rust workers: only after the benchmark in RUST_INTEROP_DESIGN.md §6.
