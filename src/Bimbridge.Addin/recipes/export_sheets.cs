// SPDX-License-Identifier: GPL-3.0-only
// Recipe shipped with BIMBRIDGE (since 0.6.0). Exports a sheet set to PDF as verifiable barriers: optionally saves
// (or syncs) first, then exports. Barriers only run when everything before them is committed; the PDF is kept as an
// artifact with SHA-256 and an approximate page count. Does not change the model content.
// args: sheet_set (ViewSheetSet name) or sheets (list of numbers), folder, name, before (nothing|save|sync, default nothing),
// combine, overwrite, color. The 0.9.4 names (conjunto, hojas, carpeta, nombre, antes=nada|guardar) are accepted until 0.9.5.
public class ExportSheets : Recipe
{
    public override string Version => "1";
    public override string Description => "Saves/syncs (optional) and exports sheets to PDF as verifiable barriers";
    public override TxPolicy Transaction => TxPolicy.ReadOnly;

    public override void Prepare(ScriptContext ctx)
    {
        if (!ctx.Args.ContainsKey("sheet_set") && !ctx.Args.ContainsKey("sheets")) throw new ArgumentException("missing sheet_set or sheets");
    }

    public override IEnumerable<Unit> Units(ScriptContext ctx)
    {
        var before = ctx.Args.TryGetValue("before", out var a) ? Convert.ToString(a) : "nothing";
        if (before == "save") yield return Unit.Save("save_before");
        else if (before == "sync") yield return Unit.Sync("sync_before", "BIMBRIDGE: before export");
        var opts = new Dictionary<string, object>();
        foreach (var k in new[] { "sheet_set", "sheets", "folder", "name", "combine", "overwrite", "color" })
            if (ctx.Args.TryGetValue(k, out var v)) opts[k] = v;
        yield return Unit.Pdf("pdf", opts);
    }

    public override object Run(ScriptContext ctx, Unit u) => null;   // barriers only
}
