# Agent identity protocol

## What BIMBRIDGE can and cannot know

| Data | Source | Verified? |
|---|---|---|
| Windows user (`principal`) | Pipe ACL: only that user can connect | **Yes** |
| Client application and version | `clientInfo` from the MCP initialization (e.g. `claude-code 2.1`) | No (declared by the client) |
| Agent alias | `BIMBRIDGE_AGENT` in the MCP configuration, or `ctx agente="…"` | No (declared) |
| Conversation and model | `BIMBRIDGE_CONVERSATION`, `BIMBRIDGE_MODEL`, or `ctx conversacion= modelo=` | No (declared) |
| Chat title | **The MCP protocol does not transmit it.** It is never made up | — |

## Flow

1. **Connection:** the bridge greets with `client`, `client_version` and, if configured, `agente` with source `configuracion`.
2. **Registration:** `ctx agente="Integration client"`, optionally with `conversacion=` and `modelo=`. It only changes that session, and the bridge repeats it on reconnect (source `registro`). An empty or invalid alias is rejected.
3. **Heartbeat:** the bridge sends a ping every 10 s. If there is no traffic for 30 s **and the session has no requests in progress**, the add-in closes it with reason `inactividad` (#1). A long operation does not close it.
4. **Requests:** each journal line carries `agente`, `cliente`, `doc` and the correlation (#4): `agent_id`, `session_id`, `request_id`, `trace_id` (also in `traza`), `job_id` and `transaction_id` when applicable. A client can send `_traza` (8–64 characters `[A-Za-z0-9-_.:]`) to tie its processes together; the AgentHost sends one per run. Per-session counters hold requests, errors, in progress and accumulated ms.
5. **Disconnection:** the session moves to the bounded history of closed sessions (50) with its reason (`conexion_cerrada`, `inactividad` or `revit_cerrando`). A Revit restart empties the history: no ghost agents.
6. **Deregistration:** `ctx desregistrar=true` returns the session to «unnamed» (no reads and no changes).
7. **Identifiers:** `session_id` is unique per instance (`S-client-pid-<64 random bits>`) and is never reused, not even when reconnecting with the same pid. That way `request_id = session_id#n` never repeats. `agent_id` is stable across reconnections with the same alias and user.

## Unnamed (#3)

A session without an alias appears as «(sin nombre) · client», with **insufficient** identity.

- **Allowed without a name:** only `hello`, `ping` and registration (`ctx agente="…"`, or `ctx desregistrar=true`).
- **Requires an alias:** everything else. This includes reads (`query`, `sets`, `check`, `catalog`, `params`, `docs`, `geo`, `job`) and a `ctx` without `agente=`, because it would return model data. Also `plan`, `commit`, `exec`, `fam`, `doc`, `show`, `capture`, `tarea` and any unknown operation. They answer `rechazado` with registration instructions, and the rejection is recorded in the journal with its correlation (`aviso: identidad_insuficiente`).
- **Duplicate alias:** if another active session of another client process already uses the alias (case-insensitive), `ctx agente=` answers `conflicto`, and the greeting leaves the session unnamed and explains why. The same bridge instance reconnecting before its previous session closes keeps the name. A name is never inherited from another session.
- **Legacy integrations:** `"identidad_obligatoria": false` in `config.json` tolerates them. It is an explicit decision by the user or IT, and each anonymous operation is marked in the journal (`identidad: sin_nombre_modo_heredado`).
- **AgentHost:** without `BIMBRIDGE_AGENT`, it declares itself via configuration as `AgentHost <pid>`.

## Presence

`ejecutando` (some request active), `activo` (activity in the last 60 s) or `inactivo` (connected, no recent activity). Closed sessions appear as disconnected, with their reason.

## Typical configuration (Claude Code)

```
"env": { "BIMBRIDGE_AGENT": "Integration client" }
```

## Compatibility

No MCP tools were added: registration reuses `ctx` (compact profile, `revit "ctx agente=…"`) and `revit_context agente=` (complete profile). Identity does not depend on `Mcp-Session-Id`.

## Limits

Aliases are self-declared and authenticate no one. The agent_id is a stable fingerprint of (principal, alias), not a credential. The trace reaches the AgentHost; the Python and Rust workers do not yet record it in their own journals.
