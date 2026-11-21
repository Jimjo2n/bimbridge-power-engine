# Bimbridge 0.7.0-alpha3

This version connects the AgentHost to a real Revit through the typed chain that already existed, without creating a second transaction engine:

`BIM Task IR → AgentHost (PlanRunner) → RevitBackend → RevitConnection (Bridge typed IPC) → Add-in (plan/commit) → Revit API → verification → audit`

## What is new

- **Separate read and write in `exec`.**
  - `modo=consulta` runs without a transaction, so Revit rejects any change.
  - The new `modo=ensayo` wraps the script in a group with a transaction that is always rolled back. It requires level W and is classified as a write.
- **Reversible typed trial (`commit modo=ensayo`).**
  - Uses the same PlanApplier: applies, verifies inside the group, calls `RollBack()` and saves the literal transaction status.
  - Then re-reads each original value and checks that no created element persists.
  - The plan is not consumed.
- **AgentHost with a real backend (`RevitBackend`).**
  - Pins one instance by PID and never chooses a session on its own.
  - Performs the handshake, checks the pipe server PID and the document identity (`clave_doc`, `huella_doc`) and applies timeouts.
  - A write timeout is treated as an uncertain effect: the model is observed and it is never blindly repeated.
- **Catalog limited to `consultar` and `fijar_parametro`** (with `unidades=texto` and at most 30 targets). QC verifications are not offered because they cannot yet be demonstrated natively.
- **Real writes need the operator's authorization.** `--escritura ensayo|persistente` is set on the command line and must match the backend mode; the agent's request is not enough.
- **NRT-04 harness:** `Bimbridge.AgentHost.exe revit nrt04 ...`, described in `TEST_REPORT_alpha3.md`.
- **P1 fixes:** the grant key race, the service queue and memory when opening large journals.
- **NRT-03 prepared without Revit:** AgentHost crashes at 5 critical points, never Revit.

## Commands

```
Bimbridge.AgentHost.exe revit instancias
Bimbridge.AgentHost.exe revit identidad --revit-pid 45440
Bimbridge.AgentHost.exe revit nrt04 --revit-pid 45440 --salida C:\...\NRT04 --categoria Doors --ids 1,2,3 --parametro Comments --valor NRT04
Bimbridge.AgentHost.exe ejecutar --plan p.json --dir estado --revit-pid 45440 --escritura ensayo --llamadas llamadas.jsonl
```

`persistente` is only accepted on a local document that is not workshared, not read-only, not in the cloud, and without a Hold queue. It is not used in alpha3.
