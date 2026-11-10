// SPDX-License-Identifier: GPL-3.0-only
// Receta incluida en BIMBRIDGE - Power Engine (desde 0.6.0). Exporta un conjunto de láminas a PDF como barreras verificables: opcionalmente guarda
// (o sincroniza) antes, y después exporta. Las barreras sólo corren cuando lo anterior está confirmado; el PDF queda como
// artefacto con SHA-256 y conteo de páginas orientativo. No cambia el contenido del modelo.
// args: conjunto (nombre del ViewSheetSet) u hojas (lista de números), carpeta, nombre, antes (nada|guardar|sync, por defecto nada).
public class ExportarLaminas : Receta
{
    public override string Version => "1";
    public override string Descripcion => "Guarda/sincroniza (opcional) y exporta láminas a PDF como barreras verificables";
    public override PoliticaTx Transaccion => PoliticaTx.Lectura;

    public override void Preparar(ScriptContext ctx)
    {
        if (!ctx.Args.ContainsKey("conjunto") && !ctx.Args.ContainsKey("hojas")) throw new ArgumentException("faltan conjunto u hojas");
    }

    public override IEnumerable<Unidad> Unidades(ScriptContext ctx)
    {
        var antes = ctx.Args.TryGetValue("antes", out var a) ? Convert.ToString(a) : "nada";
        if (antes == "guardar") yield return Unidad.Guardar("guardar_antes");
        else if (antes == "sync") yield return Unidad.Sync("sync_antes", "BIMBRIDGE: antes de exportar");
        var opts = new Dictionary<string, object>();
        foreach (var k in new[] { "conjunto", "hojas", "carpeta", "nombre", "combinar", "sobrescribir", "color" })
            if (ctx.Args.TryGetValue(k, out var v)) opts[k] = v;
        yield return Unidad.Pdf("pdf", opts);
    }

    public override object Ejecutar(ScriptContext ctx, Unidad u) => null;   // sólo barreras
}
