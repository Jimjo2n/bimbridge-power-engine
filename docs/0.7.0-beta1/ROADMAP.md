# Roadmap (beta1)

C#/.NET is the definitive engine. Python and Rust only come in if a benchmark justifies it (0.8.0 / 0.9.0).

| Version | Content | Exit criterion |
|---|---|---|
| 0.7.0-alpha3 | Reversible trial from AgentHost; P0 IsModified | **Met:** NRT-04 run6 and NRT-04N passed natively |
| **0.7.0-beta1** (current) | Controlled persistent write on a disposable .rvt; reproducible build; installer fix | NRT-05 PASSED natively: verified commit, SHA changed, values on disk and no duplicate |
| 0.7.0-beta2 | Native NRT-03: AgentHost crash at each point, without killing Revit; authorized autonomous writes | Native NRT-03 passed |
| 0.8.0 | Document intelligence (PDF/DOCX, requirements) | Benchmarks |
| 0.9.0 | Geometric performance and parallelism | Measured improvement over C# |
| 1.0.0 | Candidate for corporate evaluation | Native acceptance and IT review |
