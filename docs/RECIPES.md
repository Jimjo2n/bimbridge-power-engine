# Recipes (0.6.0)

A recipe is a C# class that inherits from `Recipe`. It is registered **by file** (no MCP tool per recipe is needed):

1. `%LOCALAPPDATA%\Bimbridge\recetas\<name>.cs` — the user's or team's recipes (these take precedence);
2. `recipes\` next to the add-in — the ones shipped by the installer.

`revit "tarea recetas"` lists them with version and SHA-256. A task can also be started with `archivo=<full path>` or `codigo=`.

## Contract

```csharp
public class MyRecipe : Recipe
{
    public override string Version => "1";                      // part of the task fingerprint
    public override string Description => "…";
    public override TxPolicy Transaction => TxPolicy.PerUnit;  // PerUnit | None (opens its own) | ReadOnly
    public override IEnumerable<string> Inputs => new[] { "vista" };
    public override void Prepare(ScriptContext ctx) { base.Prepare(ctx); /* validate, read-only */ }
    public override IEnumerable<Unit> Units(ScriptContext ctx) { /* yield, STABLE keys (UniqueId) */ }
    public override object Run(ScriptContext ctx, Unit u) { /* one unit; ctx.Skip("reason") if already done */ }
    public override Verification Verify(ScriptContext ctx, Unit u) { /* after a crash: did it stick? */ }
    public override object Postcondition(ScriptContext ctx) { /* final QC; rolled back; return {desviaciones:[…]} */ }
}
```

- `new Unit(key, data, ids)`: `ids` = elements it reads or changes; if another user changes them first, the unit becomes `conflicto` (not overwritten).
- Barriers: `Unit.Save()`, `Unit.Sync(key, comment)`, `Unit.Pdf(key, options)`.
- Memory: `ctx.Decision(text)`, `ctx.Exception(reason, detail)`, `ctx.Artifact(name, content)` (with SHA-256).
- `Bim.*` helpers (mm): `Groups` (Inspect, Signature, Compose, ChangeType, VerifyComposition, Instances), `Mounting` (Body, Support, View3D, Transformation), `Views` (Range, ApplyRange, CompareTemplate, WhyNotVisible, FilterByCategory), `Manifest` (Collect, Qc). Those that write are called inside the engine's transaction.

## Bundled

| Recipe | What it does | Inputs | Transaction |
|---|---|---|---|
| `tag_fixtures` | one tag per lighting fixture in a view; skips those already tagged; saves and optionally exports a PDF; QC: 0 untagged and 0 doubles | `vista`, `tipo_etiqueta`, `guardar`, `pdf_hojas`, `pdf_carpeta`, `pdf_nombre` | per unit |
| `batch_parameter` | sets an instance parameter; skips if it already has the value; verifies after a crash | `ids` or `conjunto`, `parametro`, `valor`, `guardar_al_final` | per unit |
| `qc_manifest` | one-to-one QC against a JSON manifest (position, height, rotation, type, level, tag); CSV of deviations | `manifiesto` (JSON or path), `categoria`, `nivel`, `vista_etiquetas` | read |
| `mounting_supports` | classifies al_ras / empotrado / separado / sin_referencia (flush / recessed / offset / no reference) against the real support, also in links | `categoria`, `nivel`, `direccion`, `tol_mm`, `max_mm`, `lote` | read |
| `export_sheets` | saves or syncs (optional) and exports PDF as verifiable barriers | `conjunto` or `hojas`, `carpeta`, `nombre`, `antes` | read |

All compile against the 2022–2026 API (`ScriptingTests.BuiltInRecipesAndBimHelpers_Compile`). **None has been run inside Revit yet.**

## Manifest (qc_manifest)

```json
{"tolerancias":{"xy":150,"z":25,"giro":2,"simetria":0,"ambiguedad":50,"duplicado":100,"mismo_nivel":true},
 "elementos":[{"ref":"L-001","x":1200.0,"y":3400.0,"z":2700.0,"giro":90,"nivel":"LEVEL 03","tipos":["LTG: D"],"etiqueta":true,
               "grupo":"UNIT A","montaje":"al_ras","fuente":"E-1.3.03 p.1 region B2 sha:…","no_confirmado":["z"]}]}
```
One-to-one matching by distance; ambiguity (two candidates within the margin) counts as **not approved**; detects missing, extra and near-duplicate elements. Codes: `faltante, ambiguo, posicion, altura, orientacion, tipo, nivel, sin_etiqueta, etiqueta_duplicada, etiqueta_sobra, grupo, montaje, sobrante, casi_duplicado, no_confirmado`. A manifest misread from the drawing yields a false "approved" QC: coverage of the PDF interpretation is a separate check (the `fuente` field).
