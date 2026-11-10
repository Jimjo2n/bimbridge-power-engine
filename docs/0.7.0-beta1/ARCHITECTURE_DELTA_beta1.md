# Delta de arquitectura beta1 (sobre alpha3)

No hay frameworks nuevos. Todo es C#/.NET; no hay Python ni Rust.

| Pieza | Cambio |
|---|---|
| Add-in `DocOps.VerifyOnDisk` | Verbo `verificar_disco` de `doc`. Exige un .rvt local guardado, no workshared y sin cambios. Calcula el SHA-256 del archivo, abre una copia temporal con `Application.OpenDocumentFile` y lee `LookupParameter(p)` de 1 a 200 ids. Cierra con `Close(false)` y borra la copia. Requiere el nivel D o W, como el resto de `doc`. |
| AgentHost `RevitCli.Nrt05Run` | El núcleo del arnés es testeable con un add-in falso: recibe la conexión ya abierta. |
| AgentHost `OpState.DirectiveName` | Correspondencia de estados (ver abajo). |
| Setup.Core `SetupCore.CopyOver` | Quita el atributo de sólo lectura antes de sobrescribir. `Platform.LastWarning` recoge los fallos no fatales. |
| build.sh / build.ps1 | Árbol limpio obligatorio y `SourceRevisionId=HEAD`. `PDI_ALLOW_DIRTY=1` sólo para desarrollo. |

## Estados (contrato de estados de ejecución → OpState existente)
| Decisión de diseño | OpState |
|---|---|
| PREPARED | `validada` |
| AUTHORIZED | `validada` con una autorización vigente del plan (GrantStore + `RealWrites` del operador) |
| EXECUTING | `en_ejecucion` |
| COMMITTED | `ejecutada` |
| VERIFIED | `verificada` |
| ROLLED_BACK | `ensayada` (ensayo revertido) |
| REJECTED | `bloqueada`, `cancelada` o `fallida` sin intento de escritura |
| OUTCOME_UNKNOWN | `requiere_verificacion` o `requiere_reconciliar` |

Un éxito de IPC nunca es VERIFIED: `ejecutada` sólo pasa a `verificada` tras observar el modelo. La semántica de ensayo (TrialGuard) no se aplica a `persistente`.

## Auditoría de exec y tareas (§6)
- `exec modo=ejecutar` ya distingue estos casos:
  - `revertido`: nada aplicado, se puede repetir.
  - `parcial`: hay cambios confirmados; reconciliar.
  - `desconocido`: Revit no confirmó ni revirtió.

  Comprueba el estado de Commit, Assimilate y RollBack. El resultado se serializa antes del commit. No hubo hallazgos nuevos.
- Tras una interrupción, la clave de idempotencia (`opId#fencing`) y el diario del add-in impiden un segundo commit: se observa y no se repite. Lo cubren las pruebas NRT-03 (5 puntos de caída) y la de timeout persistente.

## Política de TI (§12)
- **Proveedores identificables:**
  - Cada cliente MCP (Claude Code o Codex) se registra por separado, y cada sesión queda en el diario del add-in con su id.
  - AgentHost registra pid, tubería e instancia en cada llamada (`llamadas.jsonl`).
- **Restricción:** la configuración de niveles del add-in (`R`, `P`, `W`, `D`) es el interruptor por instancia:
  - sin `W` no hay escritura;
  - sin `D` (o `W`) no hay guardar ni verificar_disco;
  - "Pausar IA" rechaza todo.
- **Separación por modo:** `lectura`, `ensayo` (rollback literal) y `persistente`.
  - El modo lo fija el operador al lanzar el AgentHost; el plan no puede ampliarlo.
  - `persistente` exige un archivo local, no workshared, no de sólo lectura y sin cola.
- **Auditoría:** diario del plan (`diario.jsonl`), diario de operaciones del add-in (clave de idempotencia), `llamadas.jsonl` con ms y bytes, e informe JSON de cada NRT.
