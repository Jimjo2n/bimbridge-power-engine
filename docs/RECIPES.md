# Recipes (0.6.0)

A recipe is a C# class that inherits from `Recipe`. It is registered **by file** (no MCP tool per recipe is needed):

1. `%LOCALAPPDATA%\Bimbridge\recipes\<name>.cs` — the user's or team's recipes (these take precedence);
2. `recipes\` next to the add-in — the ones shipped by the installer.

`revit "task recipes"` lists them with version and SHA-256. A task can also be started with `file=<full path>` or `code=`.

## Contract

```csharp
public class MyRecipe : Recipe
{
    public override string Version => "1";                      // part of the task fingerprint
    public override string Description => "…";
    public override TxPolicy Transaction => TxPolicy.PerUnit;  // PerUnit | None (opens its own) | ReadOnly
    public override IEnumerable<string> Inputs => new[] { "view" };
    public override void Prepare(ScriptContext ctx) { base.Prepare(ctx); /* validate, read-only */ }
    public override IEnumerable<Unit> Units(ScriptContext ctx) { /* yield, STABLE keys (UniqueId) */ }
    public override object Run(ScriptContext ctx, Unit u) { /* one unit; ctx.Skip("reason") if already done */ }
    public override Verification Verify(ScriptContext ctx, Unit u) { /* after a crash: did it stick? */ }
    public override object Postcondition(ScriptContext ctx) { /* final QC; rolled back; return {deviations:[…]} */ }
}
```

- `new Unit(key, data, ids)`: `ids` = elements it reads or changes; if another user changes them first, the unit becomes `conflict` (not overwritten).
- Barriers: `Unit.Save()`, `Unit.Sync(key, comment)`, `Unit.Pdf(key, options)`.
- Memory: `ctx.Decision(text)`, `ctx.Exception(reason, detail)`, `ctx.Artifact(name, content)` (with SHA-256).
- `Bim.*` helpers (mm): `Groups` (Inspect, Signature, Compose, ChangeType, VerifyComposition, Instances), `Mounting` (Body, Support, View3D, Transformation), `Views` (Range, ApplyRange, CompareTemplate, WhyNotVisible, FilterByCategory), `Manifest` (Collect, Qc). Those that write are called inside the engine's transaction.

## Bundled

| Recipe | What it does | Inputs | Transaction |
|---|---|---|---|
| `tag_lights` | one tag per lighting fixture in a view; skips those already tagged; saves and optionally exports a PDF; QC: 0 untagged and 0 doubles | `view`, `tag_type_name`, `save`, `pdf_sheets`, `pdf_folder`, `pdf_name` | per unit |
| `batch_parameter` | sets an instance parameter; skips if it already has the value; verifies after a crash | `ids` or `sheet_set`, `parameter`, `value`, `save_at_end` | per unit |
| `qc_manifest` | one-to-one QC against a JSON manifest (position, height, rotation, type, level, tag); CSV of deviations | `manifest` (JSON or path), `category`, `level`, `tags_view` | read |
| `mount_supports` | classifies flush / embedded / separated / no_reference against the real support, also in links | `category`, `level`, `direction` (up/down), `tol_mm`, `max_mm`, `batch` | read |
| `export_sheets` | saves or syncs (optional) and exports PDF as verifiable barriers | `sheet_set` or `sheets`, `folder`, `name`, `before` (nothing/save/sync) | read |

All compile against the 2022–2026 API (`ScriptingTests.BuiltInRecipesAndBimHelpers_Compile`). **None has been run inside Revit yet.**

## Manifest (qc_manifest)

```json
{"tolerances":{"xy":150,"z":25,"rotation":2,"symmetry":0,"ambiguity":50,"duplicate":100,"same_level":true},
 "elements":[{"ref":"L-001","x":1200.0,"y":3400.0,"z":2700.0,"rotation":90,"level":"LEVEL 03","types":["LTG: D"],"tag":true,
              "group":"UNIT A","mounting":"flush","source":"E-1.3.03 p.1 region B2 sha:…","not_committed":["z"]}]}
```
One-to-one matching by distance; ambiguity (two candidates within the margin) counts as **not approved**; detects missing, extra and near-duplicate elements. Codes: `missing, ambiguous, position, height, orientation, type, level, no_tag, duplicate_tag, extra_tag, group, mounting, leftover, near_duplicate, not_committed`. A manifest misread from the drawing yields a false "approved" QC: coverage of the PDF interpretation is a separate check (the `source` field). Manifests with the 0.9.4 (Spanish) keys are still read until 0.9.5.
