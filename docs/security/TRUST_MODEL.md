# Trust model

## Local boundary

The bridge communicates with the Revit add-in over a local named pipe. The implementation checks the announced server process and uses the Windows-user transport boundary. There is no implied authorization for remote or anonymous clients.

MCP client names, versions, agent aliases, conversation labels and model labels are declared metadata. They are useful for attribution, not authenticated proof of an agent's identity. The Windows principal and configured levels form the access boundary.

## Dynamic C#

Dynamic C# is enabled by default for authorized local agents. The persistent control `bloquear_inyeccion_csharp` blocks new dynamic-code requests. Configuration errors fail closed according to the implementation. See the [execution policy](../0.9.0/DYNAMIC_CODE_EXECUTION_POLICY.md).

C# executes inside Revit with the user's privileges. It may access files, network and other process resources permitted to that user. A transaction group can revert covered document modifications; it cannot promise to undo file writes, network effects, saves or changes in other documents. This is not a sandbox.

Typed operations have a narrower contract: explicit arguments, plans, diffs, conflict detection, transaction handling and postcondition checks. They should not be described as equivalent in risk or guarantees to arbitrary C#.

## Recovery

Write intent is recorded before model writes. Unreadable journals or unresolved outcomes block further writes. A timeout or dropped connection does not prove a change failed. Reconcile against document evidence before retrying writes.

## Operational controls

Use authorized disposable models for acceptance. Keep production models, customer documents, local descriptors, tokens, signing keys and audit logs outside Git. Worker path authorization and network restrictions are separate from the general C# execution boundary.

Vulnerability reporting: [SECURITY.md](../../SECURITY.md). Hosted PR jobs must not reach a live Revit installation or privileged local runner. Protect Actions credentials and review workflow changes.
