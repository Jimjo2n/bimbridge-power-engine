# Roadmap (corrected after the technical review of 2026-10-28)

C#/.NET is the main engine **for good**: AgentHost, Revit API, transactions, security and journal. Neither the AgentHost nor the journal will be migrated to Rust, and no parallel orchestrator will be built in Python.

| Version | Content | Exit criterion |
|---|---|---|
| **0.7.0-alpha3** (current) | AgentHost connected to Revit through the typed path; reversible trial; P1 fixed; P0 IsModified in the add-in | Standard NRT-04 (non-workshared copy, no exceptions) and NRT-04N (rejection with IsModified=true) passed natively |
| **0.7.0-beta1** | Stabilization and controlled persistent writes on disposable models | Persistent NRT-04 with reconciliation verified natively |
| **0.7.0-beta2** | Native NRT-03 recovery and authorized autonomous writes | Native NRT-03 passed |
| **0.8.0** | Document intelligence: PDF/DOCX and requirement extraction. Python only if it brings measured advantages | Benchmarks that justify it |
| **0.9.0** | Geometric performance and parallelism. Rust versus C# only through benchmarks | Measured improvement over C# |
| **1.0.0** | Candidate for corporate evaluation | Native acceptance and IT review |

No intermediate alpha versions: alpha3 is fixed on its branch and goes straight to beta1.
