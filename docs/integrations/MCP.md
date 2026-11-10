# MCP integration

The C# bridge is an MCP stdio server. The add-in exposes Revit operations through a local protocol; AgentHost reuses the bridge transport for workflows.

The bridge source documents client registration examples in `src/PDI.RevitMcp.Bridge/Program.cs`. Client-specific configuration paths remain part of compatibility: removing the names of supported clients would make the installation guide inaccurate.

## Sessions and identity

Each client connection has a session. Requests have identifiers so responses can be correlated with pending calls. `clientInfo` provides declared client metadata; an alias can be configured with `BIMBRIDGE_AGENTE` or registered through `ctx agente=...`. Registration updates that session, not all clients.

The Windows principal is distinct from declared aliases. MCP does not provide a verified chat title or universally authenticated model identity. See [agent identity](../0.9.0/AGENT_IDENTITY_PROTOCOL.md).

## Profiles

The compact profile groups commands into a smaller tool surface. The complete profile has a different registration surface; do not assume a command is available merely because another profile exposes it. Consult the checked-in schemas and source registration for the selected profile.

## Lifecycles and errors

The bridge discovers an instance, checks its identity, performs a handshake, assigns a session and sends correlated requests. It sends periodic heartbeats and tracks outstanding responses. Writes must remain pinned to their selected target. A closed connection or timeout can produce an uncertain write outcome; this does not authorize repeating the write.

Keep protocol stdout free of diagnostic output. Use bounded diagnostics and sanitized logs. Workers use the `pe=1` JSONL contract; changing a field or operation requires compatibility review and tests.
