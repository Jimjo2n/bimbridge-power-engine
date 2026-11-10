# Recetas (0.6.0)

Una receta es una clase C# que hereda de `Receta`. Se registra **por archivo** (no hace falta una herramienta MCP por receta):

1. `%LOCALAPPDATA%\PDI.RevitMcp\recetas\<nombre>.cs` — las del usuario o del equipo (ganan);
2. `recetas\` junto al add-in — las que trae el instalador.

`revit "tarea recetas"` las lista con versión y SHA-256. También se puede iniciar con `archivo=<ruta completa>` o `codigo=`.

## Contrato

```csharp
public class MiReceta : Receta
{
    public override string Version => "1";                      // entra en la huella de la tarea
    public override string Descripcion => "…";
    public override PoliticaTx Transaccion => PoliticaTx.PorUnidad;  // PorUnidad | Ninguna (abre las suyas) | Lectura
    public override IEnumerable<string> Entradas => new[] { "vista" };
    public override void Preparar(ScriptContext ctx) { base.Preparar(ctx); /* validar, sólo lectura */ }
    public override IEnumerable<Unidad> Unidades(ScriptContext ctx) { /* yield, claves ESTABLES (UniqueId) */ }
    public override object Ejecutar(ScriptContext ctx, Unidad u) { /* una unidad; ctx.Omitir("motivo") si ya estaba */ }
    public override Verificacion Verificar(ScriptContext ctx, Unidad u) { /* tras una caída: ¿quedó? */ }
    public override object Postcondicion(ScriptContext ctx) { /* QC final; se revierte; devolver {desviaciones:[…]} */ }
}
```

- `new Unidad(clave, datos, ids)`: `ids` = elementos que lee o cambia; si otro usuario los cambia antes, la unidad queda `conflicto` (no se pisa).
- Barreras: `Unidad.Guardar()`, `Unidad.Sync(clave, comentario)`, `Unidad.Pdf(clave, opciones)`.
- Memoria: `ctx.Decision(texto)`, `ctx.Excepcion(motivo, detalle)`, `ctx.Artefacto(nombre, contenido)` (con SHA-256).
- Ayudas `Bim.*` (mm): `Grupos` (Inspeccionar, Firma, Componer, CambiarTipo, VerificarComposicion, Instancias), `Montaje` (Cuerpo, Soporte, Vista3D, Transformacion), `Vistas` (Rango, AplicarRango, CompararPlantilla, PorQueNoSeVe, FiltroPorCategoria), `Manifiesto` (Juntar, Qc). Las que escriben se llaman dentro de la transacción del motor.

## Incluidas

| Receta | Qué hace | Entradas | Transacción |
|---|---|---|---|
| `etiquetar_luces` | una etiqueta por luminaria en una vista; omite las ya etiquetadas; guarda y exporta PDF opcional; QC: 0 sin etiqueta y 0 dobles | `vista`, `tipo_etiqueta`, `guardar`, `pdf_hojas`, `pdf_carpeta`, `pdf_nombre` | por unidad |
| `parametro_por_lote` | fija un parámetro de instancia; omite si ya tiene el valor; verifica tras caída | `ids` o `conjunto`, `parametro`, `valor`, `guardar_al_final` | por unidad |
| `qc_manifiesto` | QC uno a uno contra un manifiesto JSON (posición, altura, giro, tipo, nivel, etiqueta); CSV de desviaciones | `manifiesto` (JSON o ruta), `categoria`, `nivel`, `vista_etiquetas` | lectura |
| `montaje_soportes` | clasifica al_ras / empotrado / separado / sin_referencia contra el soporte real, también en vínculos | `categoria`, `nivel`, `direccion`, `tol_mm`, `max_mm`, `lote` | lectura |
| `exportar_laminas` | guarda o sincroniza (opcional) y exporta PDF como barreras verificables | `conjunto` u `hojas`, `carpeta`, `nombre`, `antes` | lectura |

Todas compilan contra la API de 2022–2026 (`ScriptingTests.BuiltInRecipesAndBimHelpers_Compile`). **Ninguna se ha ejecutado aún dentro de Revit.**

## Manifiesto (qc_manifiesto)

```json
{"tolerancias":{"xy":150,"z":25,"giro":2,"simetria":0,"ambiguedad":50,"duplicado":100,"mismo_nivel":true},
 "elementos":[{"ref":"L-001","x":1200.0,"y":3400.0,"z":2700.0,"giro":90,"nivel":"LEVEL 03","tipos":["LTG: D"],"etiqueta":true,
               "grupo":"UNIT A","montaje":"al_ras","fuente":"E-1.3.03 p.1 región B2 sha:…","no_confirmado":["z"]}]}
```
Emparejamiento uno a uno por distancia; ambigüedad (dos candidatos dentro del margen) cuenta como **no aprobado**; detecta faltantes, sobrantes y casi duplicados. Códigos: `faltante, ambiguo, posicion, altura, orientacion, tipo, nivel, sin_etiqueta, etiqueta_duplicada, etiqueta_sobra, grupo, montaje, sobrante, casi_duplicado, no_confirmado`. Un manifiesto mal leído del plano da un QC "aprobado" falso: la cobertura de interpretación del PDF es otra comprobación (campo `fuente`).
