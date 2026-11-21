// SPDX-License-Identifier: GPL-3.0-only
// Recipe shipped with BIMBRIDGE - Power Engine (since 0.6.0). Exports a sheet set to PDF as verifiable barriers: optionally saves
// (or syncs) first, then exports. Barriers only run when everything before them is committed; the PDF is kept as an
// artifact with SHA-256 and an approximate page count. Does not change the model content.
// args: conjunto (ViewSheetSet name) or hojas (list of numbers), carpeta, nombre, antes (nada|guardar|sync, default nada).
public class ExportarLaminas : Receta
{
    public override string Version => "1";
    public override string Descripcion => "Saves/syncs (optional) and exports sheets to PDF as verifiable barriers";
    public override PoliticaTx Transaccion => PoliticaTx.Lectura;

    public override void Preparar(ScriptContext ctx)
    {
        if (!ctx.Args.ContainsKey("conjunto") && !ctx.Args.ContainsKey("hojas")) throw new ArgumentException("missing conjunto or hojas");
    }

    public override IEnumerable<Unidad> Unidades(ScriptContext ctx)
    {
        var antes = ctx.Args.TryGetValue("antes", out var a) ? Convert.ToString(a) : "nada";
        if (antes == "guardar") yield return Unidad.Guardar("guardar_antes");
        else if (antes == "sync") yield return Unidad.Sync("sync_antes", "BIMBRIDGE: before export");
        var opts = new Dictionary<string, object>();
        foreach (var k in new[] { "conjunto", "hojas", "carpeta", "nombre", "combinar", "sobrescribir", "color" })
            if (ctx.Args.TryGetValue(k, out var v)) opts[k] = v;
        yield return Unidad.Pdf("pdf", opts);
    }

    public override object Ejecutar(ScriptContext ctx, Unidad u) => null;   // barriers only
}
