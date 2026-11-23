# Roadmap after 0.7.0-alpha1

**alpha2 (without Revit)**
- Run the suite on Windows (Revit 2024/2026 installed but without opening models).
- Fix the GrantStore key race (requires operator approval, touches rc3).
- PDF extraction of specifications with page/section; more requirement patterns.
- Additional preconditions (current value, parameter existence, type).

**beta1 (Revit, read-only, practice copy, with approval)**
- Connect `RevitTaskAdapter.Observe/Prepare` to the rc3 add-in in read mode.
- Compare simulated vs real observation on the same plan.

**beta2 (writes on a practice copy, with explicit operator approval)**
- `Execute` via rc3 recipes with the `revit_modificar` grant; AH-01 recovery against the rc3 transaction journal.

**Rust**: only if a measurement in real Revit shows that computation (not extraction) dominates; use the bench and digest from RENDIMIENTO.
