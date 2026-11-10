// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using System.Text;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Guías bajo demanda (herramienta guide). Las descripciones de las herramientas son cortas porque se cargan en cada sesión;
    /// el detalle vive aquí y la IA sólo lo pide cuando lo necesita (y sólo el tema o la operación que necesita).
    /// </summary>
    public static class Guides
    {
        public static readonly string[] Topics = { "flujo", "consulta", "catalogo", "geo", "escritura", "exec", "familias", "documento", "recuperacion", "tareas", "recetas", "captura", "lote" };

        public static string Get(string tema, string op = null)
        {
            switch ((tema ?? "flujo").Trim().ToLowerInvariant())
            {
                case "flujo": return Flow;
                case "consulta": case "query": return Query;
                case "catalogo": case "catálogo": return Catalog;
                case "geo": case "geometria": case "geometría": return Geo;
                case "escritura": case "plan": return OpCatalog.Guide(op);
                case "captura": case "capture": return CaptureText;
                case "lote": return Batch;
                case "exec": case "codigo": case "código": return Exec;
                case "familias": case "familia": case "fam": case "conector": return Families;
                case "documento": case "doc": case "pdf": case "sync": case "guardar": return DocOps;
                case "recuperacion": case "recuperación": case "clave": return Recovery;
                case "tarea": case "tareas": return Tasks;
                case "receta": case "recetas": return Recipes;
                default: return "Temas: " + string.Join(", ", Topics) + ". escritura admite op=tag,place… para ver sólo esas operaciones.";
            }
        }

        private const string Flow =
            "FLUJO RECOMENDADO (menos llamadas, menos texto):\n" +
            "1. revit_context: versión, documento, rev. Si hay varios Revit abiertos, aparecen en 'instancias' (cambie con instancia=<pid>).\n" +
            "2. catalog que=resumen: niveles, vistas, láminas, vínculos y categorías con conteo, en una llamada.\n" +
            "3. query con count=true o group_by=lvl,type para resumir; pida filas sólo si las va a usar y sólo los campos necesarios (f=id,type).\n" +
            "4. Guarde resultados con save_as=$nombre y reutilícelos (ids=$nombre) en query, get, geo, plan, show y capture.\n" +
            "5. Varias lecturas independientes → lote (una sola llamada).\n" +
            "6. Para VER la vista: capture (imagen) con colorear=categorias para distinguir luminarias de tomas e interruptores; resaltar=$conjunto.\n" +
            "7. Física real (alturas, empotradas, flotando, choques): geo. Repetir sin cambios en el modelo es instantáneo (caché).\n" +
            "8. Cambios: plan → commit (tipado, diff, un Deshacer) para lo que cubren sus operaciones; exec (C#) para todo lo demás; act/fam y doc para familias, " +
            "conectores, guardar, sync y PDF. Si la tarea está autorizada, aplique sin pedir confirmación en cada lote; si no, enseñe el diff antes. " +
            "estado=retenido = espera en la cola de Revit; no repetir. Pausa del usuario = deténgase.\n" +
            "9. show ids=… para señalarle al usuario los elementos en pantalla (selecciona y encuadra).\n" +
            "Estados: lea 'aviso' y siga su indicación. job espera trabajos largos (job_id).";

        private const string Exec =
            "EXEC (C# general sobre la API pública de Revit; compilado con Roslyn 4.8 dentro de Revit):\n" +
            "Formas: expresión (doc.Title) | cuerpo con return | clase con public static object Run(ScriptContext ctx). using al principio se suben solos.\n" +
            "Ya existen: doc, uidoc, app, uiapp, args (objeto JSON de entrada), ctx. ctx.Mm(6.5) mm→pies, ctx.ToMm(pies), ctx.E(id), ctx.Set(\"$x\"), " +
            "ctx.SaveSet(\"$y\", ids), ctx.Log(x), ctx.DocOf(\"d3\"), ctx.ThrowIfCancelled().\n" +
            "modo=consulta (por defecto): corre dentro de una transacción que SIEMPRE se revierte; sirve para medir o probar sin tocar el modelo.\n" +
            "modo=ejecutar tx=auto: una transacción, un Deshacer | tx=grupo: varias transacciones del script en un grupo | tx=ninguna: el script abre las suyas " +
            "(obligatorio para EditFamily, LoadFamily, Save, SaveAs). Requiere permiso W (o R para consulta).\n" +
            "Devuelve: valor en JSON (ElementId → número, XYZ → [x,y,z] en pies, Element → id/cat/nombre), log, avisos de Revit agrupados por tipo, " +
            "elementos creados/cambiados/borrados por documento ($exec_cambiados). Errores con línea del script. Todo script queda auditado (hash + archivo).\n" +
            "guardar=nombre lo guarda; nombre=X lo repite; nombre=lista. Un script corre en el hilo de Revit: Revit no responde mientras corre.";

        private const string Families =
            "FAMILIAS (act / fam). Flujo típico del lavamanos:\n" +
            "1. revit \"fam info familia=X\" (tipos, parámetros, fórmulas, conectores) y \"fam caras familia=X\" (caras planas elemento:n con normal y centro).\n" +
            "2. act \"abrir familia=X\" → handle dN del RFA (reutiliza el abierto).\n" +
            "3. act \"conector nombre=fria sistema=fria cara=812:3 radio=6.5mm doc=dN\" (radio o diametro SIEMPRE con unidad; no se supone ninguno). " +
            "Busca por nombre (descripción del conector), adopta uno sin nombre en esa cara o lo crea; no duplica. flujo=entrada|salida|bidireccional sólo si se decidió.\n" +
            "4. act \"param nombre=X valor=6.5mm\" | formula= | crear=tipo|instancia dato=longitud | asociar=elem:CONNECTOR_RADIUS. Si un parámetro de familia controla " +
            "el radio, se informa como pendiente: no se rompe la asociación.\n" +
            "5. act \"guardar doc=dN\" y \"cargar en=d1 doc=dN sobrescribir=valores\": recarga en el proyecto y relee los conectores desde las instancias.\n" +
            "Repetir 3–5 con la misma clave= no aplica dos veces.";

        private const string DocOps =
            "DOCUMENTO (act o doc_op): guardar | guardar_como como=ruta [sobrescribir=si] | sync [comentario=] [liberar=si] | " +
            "pdf conjunto=\"NON-UNIT LIGHTING\" o hojas=$x|10E.01.NUL,10E.02.NUL [carpeta=] [nombre=] | cerrar [guardar=si|descartar=si].\n" +
            "Cada uno corre como trabajo (job_id si tarda) y se VERIFICA: archivo existe, fecha nueva, tamaño, SHA-256, páginas del PDF; sync confirma por evento. " +
            "No cierra el documento activo (Revit no lo permite por la API). Permiso D o W.";

        private const string Recovery =
            "RECUPERACIÓN: clave=<texto> en commit, exec ejecutar, act y doc_op la registra en un diario en disco (operaciones.jsonl).\n" +
            "Misma clave ya hecha → devuelve el resultado anterior sin repetir. Interrumpida (Revit se cerró a mitad) → estado=interrumpido: revise el modelo " +
            "y repita con reconciliar=si. Misma clave con otro contenido → invalido.\n" +
            "tarea=<nombre> en plan/commit/exec: los trabajos de esa tarea no se cancelan al cortarse la conversación y otra conversación con la misma tarea " +
            "puede consultarlos (job) y aplicar sus planes. Documentos: doc=dN fija el destino; si ese documento se cerró → destino_no_disponible (no se cambia de documento solo).";

        private const string Tasks =
            "TAREAS LARGAS (0.6.0): act \"tarea iniciar receta=<nombre>|archivo=<ruta.cs> id=<id> objetivo=… args.campo=valor [guardar_cada=N] [al_final=guardar,pdf] [en_error=continuar]\".\n" +
            "UNA orden: el motor enumera las unidades de la receta, las ejecuta por lotes medidos (una transacción y un Deshacer por unidad), cede Revit entre visitas, " +
            "deja checkpoint durable tras cada lote, guarda cada N y corre las barreras finales sólo con todo lo anterior confirmado. Responde a los ~10 s (espera_s=) con el estado.\n" +
            "Seguir: revit \"tarea esperar <id> desde=<rev> max=60\" (espera cambios sin sondear) | tarea estado|resumen|unidades <id> [estado=conflicto] | tarea lista.\n" +
            "Otro cliente (Codex↔Claude) la retoma con el id: tarea resumen <id> da dónde quedó, qué falta y las decisiones; no hay que reenviar código.\n" +
            "Estados: en_cola, ejecutando, esperando_contexto (pausa/Retener/diálogo/documento cerrado: sigue sola), aplicada, aplicada_con_diferencias, parcial, interrumpida, cancelada.\n" +
            "interrumpida/parcial: act \"tarea reanudar <id>\" verifica lo dudoso (UniqueId de lo creado o Verificar de la receta; tras reabrir, lo posterior al último guardado de esa apertura) " +
            "y sigue sin duplicar. Lo que no se puede probar queda desconocida: act \"tarea reconciliar <id> decision=aplicada|no_aplicada claves=…\". " +
            "Cancelar: act \"tarea cancelar <id>\" (entre unidades; lo confirmado queda). Mientras corre, el documento queda tomado: otras escrituras → documento_ocupado (exec/commit con tarea=<id> sí pasan).";

        private const string Recipes =
            "RECETAS: clase C# que hereda de Receta (por archivo en %LOCALAPPDATA%\\PDI.RevitMcp\\recetas\\<nombre>.cs, en recetas\\ del add-in, o código). tarea recetas las lista.\n" +
            "public class X : Receta { public override string Version => \"1\"; public override IEnumerable<string> Entradas => new[]{\"nivel\"};\n" +
            "  public override IEnumerable<Unidad> Unidades(ScriptContext ctx) { foreach (var e in …) yield return new Unidad(\"luz:\" + e.UniqueId, datos, new[]{ ctx.Long(e.Id) }); yield return Unidad.Guardar(); }\n" +
            "  public override object Ejecutar(ScriptContext ctx, Unidad u) { … return resumen; }   // dentro de su transacción; ctx.Omitir(\"motivo\") la salta\n" +
            "  public override Verificacion Verificar(ScriptContext ctx, Unidad u) => …;   // tras una caída: ¿quedó aplicada?\n" +
            "  public override object Postcondicion(ScriptContext ctx) => new { desviaciones = … }; }   // QC final de sólo lectura (se revierte)\n" +
            "Claves ESTABLES (UniqueId, no ElementId). Ids de la unidad = lo que lee/cambia: si otro los cambia antes, la unidad queda conflicto (no se pisa). " +
            "ctx.Decision(texto), ctx.Excepcion(motivo, detalle), ctx.Artefacto(nombre, contenido) quedan en la memoria de la tarea. Transaccion => PoliticaTx.Ninguna si la receta abre las suyas (EditFamily/LoadFamily). " +
            "La tarea guarda una copia exacta de la receta (con su SHA-256): cambiar el archivo no altera tareas ya iniciadas.\n" +
            "INCLUIDAS: etiquetar_luces (vista, tipo_etiqueta, guardar, pdf_hojas), parametro_por_lote (ids|conjunto, parametro, valor), qc_manifiesto (manifiesto JSON o ruta; CSV de desviaciones), " +
            "montaje_soportes (al_ras/empotrado/separado/sin_referencia, incluye vínculos), exportar_laminas (conjunto|hojas, antes=guardar|sync).\n" +
            "AYUDAS Bim.* (en scripts y recetas, mm): Grupos.Inspeccionar/Firma/Componer/CambiarTipo/VerificarComposicion; Montaje.Cuerpo/Soporte/Vista3D/Transformacion; " +
            "Vistas.Rango/AplicarRango/CompararPlantilla/PorQueNoSeVe/FiltroPorCategoria; Manifiesto.Juntar/Qc. Las que escriben van dentro de la transacción del motor.";

        private const string Query =
            "CONSULTA (query): cat (LightingFixtures, ElectricalFixtures, LightingDevices…), lvl, type (contiene), ws, ids/$conjunto, view (visibles en esa vista).\n" +
            "where: campo=valor, !=, >, <, >=, <=, ~ (contiene), campo? (no vacío), !campo (vacío). Ej.: where=[\"elev>9\",\"type~LT-06\"].\n" +
            "f: id,cat,fam,type,lvl,ws,elev,mark,comments,host,x,y,z,name,uid,view o cualquier parámetro por nombre.\n" +
            "group_by (hasta 3 campos) cuenta por grupos; count=true sólo el total; page/page_size (máx. 200); since=rev sólo lo cambiado después.\n" +
            "units=mm cambia elev,x,y,z (también en where). full=true no recorta textos.\n" +
            "get ids=… f=*: TODOS los parámetros (instancia y tipo) de hasta 10 elementos; lo común sale una vez en 'comunes'. buscar=texto filtra nombres.";

        private const string Catalog =
            "CATÁLOGO (catalog que=…): resumen | categorias | tipos | familias | niveles | vistas | plantillas | laminas | worksets | vinculos | filtros.\n" +
            "cat filtra tipos y familias; lvl y tipo (plano, rcp, seccion, alzado, 3d, leyenda, tabla…) filtran vistas; buscar=texto en cualquier columna.\n" +
            "f añade parámetros por nombre a vistas y láminas (p. ej. f=Sub-Discipline,Set Group o f=Sheet Set,Drawn By).\n" +
            "niveles: elev = cota mostrada; z = cota interna (la de x,y,z). vistas: hoja = número de la lámina donde está colocada.";

        private const string Geo =
            "GEO (mallas reales del modelo y de los vínculos, sin modificar nada). name=receta:\n" +
            "- montaje: cara superior del cuerpo contra la cara inferior de lo de arriba (losas, vigas, rampas, escaleras) en 1/5/9 puntos; manda la esquina más baja. " +
            "Clases ok, empotrada, flotando, sin_superficie; guarda $empotradas, $flotando, $sin_superficie. tol=1/16\" por defecto.\n" +
            "- holgura: lo mismo en 1 punto. choques: penetraciones (rayos + booleana exacta) → $choques. cerca: columnas/vigas alrededor (radio) → $cerca_choque.\n" +
            "- muro: distancia y lado del muro → $penetra_muro, $separadas_muro. nivel_real: nivel físico vs. asignado → $nivel_distinto. duplicados → $duplicados.\n" +
            "- cuarto: cuarto (vínculo) y espacio de cada elemento → $sin_cuarto. rango_vista: dentro/encima/debajo del rango de cada plano → $rango_encima, $rango_debajo. indice: estado de la caché.\n" +
            "Claves: cat, lvl, ids/$conjunto, ws, type, contra (losas, vigas, columnas, rampas, escaleras, muros, cielos, techos o categorías), vinculos (contiene|todos|ninguno), " +
            "anfitrion=false, lado=arriba|abajo, puntos, tol, max, radio, sub_excluir (subcategorías del cuerpo a ignorar, p. ej. JBox), solo=clase|todos, page.\n" +
            "Encadenar: geo name=montaje → plan [{op:snap_to_surface, ids:$flotando}] → commit. Si tarda devuelve job_id: espere con job.";

        private const string CaptureText =
            "CAPTURA (capture): imagen PNG de una vista para mirarla. vista=id (por defecto la activa; catalog que=vistas para ids). px 400–2400 (1400 por defecto; 1000 basta para un vistazo).\n" +
            "colorear=categorias: luminarias naranja, interruptores verde, tomas azul, tableros magenta, alarma rojo, datos/comunicaciones cian… (leyenda en la respuesta).\n" +
            "resaltar=\"$sin_tag\" o \"$a=rojo;$b=azul;123,456=#00AAFF\" (hasta 6 grupos). encuadre=ids|$conjunto|resaltado recorta a esos elementos.\n" +
            "Resaltado y encuadre se aplican sólo para la imagen y se revierten en el acto: el modelo no cambia y no aparece en Deshacer. " +
            "Hojas: se capturan tal cual. 'contenido' cuenta lo visible por categoría (modelo propio).";

        private const string Batch =
            "LOTE (lote): varias lecturas en una llamada. llamadas=[{\"tool\":\"query\",\"cat\":\"LightingFixtures\",\"count\":true}, {\"tool\":\"catalog\",\"que\":\"niveles\"}, …] (máx. 8).\n" +
            "Herramientas admitidas: revit_context, query, get, catalog, check, geo, sets. Se ejecutan a la vez y vuelven en el mismo orden; un error no detiene las demás.";

        public static string TopicList => string.Join(", ", Topics);

        public static int Size(string tema) => Encoding.UTF8.GetByteCount(Get(tema));

        public static string All() => string.Join("\n\n", Topics.Select(t => Get(t)));
    }
}
