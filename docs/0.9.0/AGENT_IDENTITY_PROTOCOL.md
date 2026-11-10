# Protocolo de identidad de agentes

## Qué puede saber BIMBRIDGE y qué no

| Dato | Fuente | ¿Verificado? |
|---|---|---|
| Usuario de Windows (`principal`) | ACL de la tubería: sólo ese usuario conecta | **Sí** |
| Aplicación cliente y versión | `clientInfo` del inicio MCP (p. ej. `claude-code 2.1`) | No (declarado por el cliente) |
| Alias del agente | `BIMBRIDGE_AGENTE` en la configuración MCP, o `ctx agente="…"` | No (declarado) |
| Conversación y modelo | `BIMBRIDGE_CONVERSACION`, `BIMBRIDGE_MODELO`, o `ctx conversacion= modelo=` | No (declarado) |
| Título del chat | **El protocolo MCP no lo transmite.** Nunca se inventa | — |

## Flujo

1. **Conexión:** el puente saluda con `client`, `client_version` y, si está configurado, `agente` con fuente `configuracion`.
2. **Registro:** `ctx agente="Integration client"`, opcionalmente con `conversacion=` y `modelo=`. Sólo cambia esa sesión, y el puente lo repite si reconecta (fuente `registro`). Un alias vacío o inválido se rechaza.
3. **Latido:** el puente envía ping cada 10 s. Sin tráfico durante 30 s, el add-in cierra la sesión.
4. **Solicitudes:** cada línea del diario lleva `agente`, `cliente`, `traza` (`sesion:request_id`) y `doc`. Los contadores por sesión llevan solicitudes, errores, en ejecución y ms acumulados.
5. **Desconexión:** la sesión pasa al historial acotado de cerradas (50) con su motivo (`conexion_cerrada` o `revit_cerrando`). Un reinicio de Revit vacía el historial: nada de agentes fantasma.

## Sin nombre

Una sesión sin alias aparece como «(sin nombre) · cliente», con identidad **insuficiente**, y `ctx` le indica cómo registrarse. Nunca recibe el nombre de otra sesión.

## Presencia

`ejecutando` (alguna solicitud activa), `activo` (actividad en los últimos 60 s) o `inactivo` (conectado, sin actividad reciente). Las cerradas aparecen como desconectadas, con su motivo.

## Configuración típica (Claude Code)

```
"env": { "BIMBRIDGE_AGENTE": "Integration client" }
```

## Compatibilidad

No se añadieron herramientas MCP: el registro reutiliza `ctx` (perfil compacto, `revit "ctx agente=…"`) y `revit_context agente=` (perfil completo). La identidad no depende de `Mcp-Session-Id`.

## Límites

Los alias son autodeclarados y no autentican a nadie. No hay correlación con AgentHost, el Python Worker ni el Rust Worker en esta revisión.
