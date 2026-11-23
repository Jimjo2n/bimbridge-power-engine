# Native acceptance 0.6.1 (level D)

**Status: NOT_EXECUTED.** Nothing in 0.6.1 has run inside Revit. The validation environment for this review did not include an isolated Revit instance.

Procedure (practice copies, never the production SAMPLE PROJECT or central models), when operator authorizes it:

| Id | Step | Pass criterion | Status |
|---|---|---|---|
| D-01 | Install 0.6.1 on Revit 2024 and 2026; `Setup.exe /diagnostico` | version 0.6.1 loaded after restart; hashes OK | NOT_EXECUTED |
| D-02 | Read and write `exec` on a copy | both in `auditoria.jsonl`; `Verify` OK | NOT_EXECUTED |
| D-03 | Make the `auditoria` folder read-only and request a writing `exec` | rejected with "audit not available"; a query one still runs | NOT_EXECUTED |
| D-04 | `tarea iniciar etiquetar_luces` (0.6.0 procedure) | applied; policy without denials | NOT_EXECUTED |
| D-05 | Test recipe with `System.Diagnostics.Process.Start` | `tarea iniciar` rejects it with the list of capabilities | NOT_EXECUTED |
| D-06 | Two Revits with the same copy, `tarea reanudar` in both at once | only one runs; the other reports who holds it; fencing increments when the second takes it | NOT_EXECUTED |
| D-07 | Connect the bridge with two Revits open | connects to the announced PID; no identity errors | NOT_EXECUTED |
| D-08 | Roll back to 0.6.0 with the "Version" button | 0.6.0 loads; tasks with the new `lease.json` remain readable | NOT_EXECUTED |

Evidence: 0.6.0's `acceptance\collect_evidence.ps1` + `auditoria\auditoria.jsonl`.
