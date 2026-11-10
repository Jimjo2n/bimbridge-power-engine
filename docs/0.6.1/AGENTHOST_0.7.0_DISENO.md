# 0.7.0 — PDI.RevitMcp.AgentHost: fronteras iniciales

Diseño mínimo para empezar la 0.7.0 sin usar capacidades que 0.6.1 deja restringidas. No se implementa en 0.6.1.

## 1. Lugar en la arquitectura

`Claude/Codex → AgentHost (C#, proceso propio) → contratos de ejecución → Engine C# en Revit (+ workers de cálculo)`

AgentHost **no** tiene acceso a la API de Revit ni a las concesiones: habla con el add-in por el mismo puente (pipe por usuario), con un cliente como cualquier otro. Todo lo que modifique el modelo pasa por `tarea` y, por tanto, por concesiones de una persona, auditoría en dos fases, leases y reconciliación.

## 2. Representación intermedia (BIM Task IR), esquema 1

```json
{
  "ir": 1,
  "id": "op-7f3a…",                       // estable: sha256 de (fuente + alcance + operación + parámetros canónicos)
  "documento": {"clave": "archivo:c:\\…\\practica.rvt", "huella": "…"},
  "alcance": {"categoria": "OST_LightingFixtures", "nivel": "LEVEL 03", "ids": [], "consulta": "…"},
  "operacion": "fijar_parametro",          // del catálogo cerrado (= recetas instaladas); nada de código libre
  "parametros": {"parametro": "Mounting Height", "valor": 2700, "unidades": "mm"},
  "precondiciones": ["vista RCP existe", "elementos sin etiqueta"],
  "depende_de": ["op-12ab…"],
  "autorizacion": ["revit_modificar"],     // calculada, no declarada por el agente
  "resultado_esperado": {"cambiados": 43},
  "verificacion": {"receta": "qc_manifiesto", "tolerancias": {"z": 25}},
  "reintento": "nunca_si_dudoso",          // idempotencia: sólo se repite lo que el motor prueba que no quedó
  "estado": "propuesta",                   // propuesta → validada → simulada → aprobada → en_tarea → verificada | rechazada
  "trazabilidad": [{"fuente": "E-1.3.03 p.1 región B2", "sha256": "…", "cita": "MH 22'-4\""}]
}
```

- `estado` sólo avanza a `aprobada` con una concesión real (botón «Autorizar» en Revit); AgentHost no la puede crear.
- `operacion` se compila a `tarea iniciar receta=<instalada>`; una IR que requiera código fuera del catálogo queda en `propuesta` con el motivo.

## 3. Lo que se puede construir ya (sin capacidades restringidas)

| Orden | Trabajo | Usa | Depende de |
|---|---|---|---|
| 1 | Esquema IR + validador (JSON Schema + pruebas) | nada de Revit | — |
| 2 | Extracción de requisitos de documentos con trazabilidad (`fuente`, `sha256`, cita) | archivos | 1 |
| 3 | Grafo de dependencias y orden topológico; detección de ciclos | memoria | 1 |
| 4 | Consultas de sólo lectura al modelo vía `revit`/`exec modo=consulta` (perfil restringido) | puente | — |
| 5 | Simulación: `qc_manifiesto` y `montaje_soportes` (recetas de lectura instaladas) | puente | 4 |
| 6 | Memoria de contexto resumida por tarea (ya existe `tarea resumen`) y por documento | disco | — |
| 7 | Adaptador simulado del motor (el `FakeBackend` de las pruebas) para desarrollar el orquestador sin Revit | pruebas | — |
| 8 | Coordinación Claude/Codex: cola de propuestas con dueño por IR, usando los mismos leases con fencing | Engine | 3 |

## 4. Lo que queda bloqueado hasta cumplir sus condiciones

- Escrituras autónomas sobre modelos reales: requieren la aceptación nativa D-01..D-12 en copias de práctica (ver NATIVE_ACCEPTANCE_0.6.1.md).
- Reanudación automática de escrituras dudosas: nunca automática; sólo `reconciliar` con evidencia y concesión.
- Código libre autónomo: sólo con aprobación de esa receta (`receta_externa`).
- Workers Rust: sólo tras el benchmark de RUST_INTEROP_DESIGN.md §6.
