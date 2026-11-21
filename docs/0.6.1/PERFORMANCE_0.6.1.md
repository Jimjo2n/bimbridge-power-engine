# Performance 0.6.1 (without Revit)

| Measure | Value | How |
|---|---|---|
| ValidateOwnership (lock + read) | 0.0689 ms per call | 1,000 calls, Linux, container local disk |
| RenewLease (lock + atomic write) | 2.3653 ms per call | 1,000 calls |
| Cost per batch | 1 validation per batch; renewal every 20 s | TaskRunner |
| Capability analysis | done on the already compiled tree; no extra compilation | Compiler.CompileWithCapabilities |
| Tokens | no changes to the tools: the compact profile stays at 5,685 characters | 0.6.0 tokens bench |

This is not a Windows or Revit measurement. On Windows with antivirus, opening a file may cost more; it will be measured during native acceptance.
