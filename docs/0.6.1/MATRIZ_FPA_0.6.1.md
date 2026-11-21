# Failure → protection → acceptance matrix (FPA) 0.6.1

| Failure | Protection | Test |
|---|---|---|
| Two Revits take the same task | file lock + fencing | 20 real processes |
| Revit A hangs, B takes the task, A wakes up | A validates fencing before the batch → interrumpida | LeaseStolen_MidRun |
| Corrupt `lease.json` | explicit error, does not run | Lease_CorruptFile |
| Full disk / audit folder without permission | write `exec` rejected; task `parcial` if the unit journal fails (0.6.0) | AuditLogTests, DurabilityTests |
| Someone deletes or edits an audit line | `Verify` detects it | AuditLogTests |
| Crash between commit and journal | verify, never blindly re-run (0.6.0) | TaskEngineTests |
| Recipe with `Process.Start`, network, reflection, `doc.Close` | rejected in the autonomous profile | HostileCode |
| Recipe altered in the task folder | SHA-256 mismatch → not resumed (0.6.0) | TaskEngineTests |
| Another process takes the pipe name | PID and executable checked | PipeServerCheckTests |
