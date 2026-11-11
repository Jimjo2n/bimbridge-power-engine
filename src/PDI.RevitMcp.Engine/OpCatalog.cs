// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Catálogo de operaciones de escritura (plan). Una sola fuente para validar, para la guía que lee la IA
    /// (guide tema=escritura) y para los nombres que ve operator en la Cola. Cada operación cubre llamadas de la API
    /// que los 60 scripts "modify" de pyRevit en el equipo de prueba usaron de verdad (conteos del análisis del 15/10/2026).
    /// </summary>
    public static class OpCatalog
    {
        public sealed class Info
        {
            public string Kind;
            public string Nombre;        // cómo se ve en la Cola
            public bool NeedsIds;
            public bool Creates;
            public string[] Required;
            public string[] Optional;
            public string Help;          // una línea para la guía
            public string Example;       // JSON de ejemplo
        }

        public static readonly List<Info> All = new List<Info>
        {
            I("set_param", "Parámetro", true, false, new[] { "name", "value" }, new string[0],
              "parámetro de instancia (o de vista/lámina). value: número en unidades internas, texto con unidades (\"9' - 3\\\"\", \"2820 mm\"), true/false, o nombre para parámetros que apuntan a un elemento (nivel, fase, scope box, plantilla).",
              "{\"op\":\"set_param\",\"ids\":\"$sin_tag\",\"name\":\"Comments\",\"value\":\"REVISAR\"}"),
            I("set_values", "Valores por elemento", false, false, new[] { "name", "values" }, new string[0],
              "un valor distinto por elemento: values {\"id\": valor}. name: parámetro, o elev / ws / type.",
              "{\"op\":\"set_values\",\"name\":\"Mark\",\"values\":{\"17855001\":\"L-01\",\"17855002\":\"L-02\"}}"),
            I("change_type", "Tipo", true, false, new[] { "type" }, new string[0],
              "cambiar tipo: \"Familia: Tipo\" (también tags, grupos y viewports).", "{\"op\":\"change_type\",\"ids\":\"1,2\",\"type\":\"LTG_STRIP LIGHT: L5\"}"),
            I("set_elevation", "Altura", true, false, new string[0], new[] { "ft", "valor" },
              "Elevation from Level: ft (pies) o valor con unidades (\"9' - 3\\\"\", \"2820 mm\").", "{\"op\":\"set_elevation\",\"ids\":\"$l5\",\"valor\":\"10' - 9\\\"\"}"),
            I("set_workset", "Workset", true, false, new[] { "ws" }, new string[0], "mover a otro workset por nombre.", "{\"op\":\"set_workset\",\"ids\":\"$l5\",\"ws\":\"LIGHTING - DIST\"}"),
            I("delete", "Borrar", true, false, new string[0], new string[0], "borrar los elementos listados (Revit borra también sus dependientes, p. ej. sus tags).", "{\"op\":\"delete\",\"ids\":\"$duplicados\"}"),
            I("set_type_param", "Parámetro de tipo", false, false, new[] { "name", "value" }, new[] { "type", "ids" },
              "parámetro de TIPO (afecta a todas sus instancias): type \"Familia: Tipo\" o ids de instancias.", "{\"op\":\"set_type_param\",\"type\":\"LTG_STRIP LIGHT: L5\",\"name\":\"Description\",\"value\":\"...\"}"),
            I("set_level", "Nivel", true, false, new[] { "level" }, new[] { "mantener" },
              "cambiar el nivel asignado. mantener=posicion (por defecto: ajusta el offset para no moverlo) u offset (se mueve con el nivel).",
              "{\"op\":\"set_level\",\"ids\":\"$nivel_distinto\",\"level\":\"LEVEL 02\"}"),
            I("snap_to_surface", "Ajustar a losa", true, false, new string[0], new[] { "lado", "holgura", "contra", "puntos", "max", "vinculos" },
              "calcula con la geometría la altura para que la cara superior toque la cara inferior de lo de arriba (lado=abajo: base sobre el piso). En pendiente manda la esquina más cercana: nunca queda empotrada. holgura (por defecto 0), puntos 1/5/9 (9), contra (losas,vigas,rampas,escaleras,techos).",
              "{\"op\":\"snap_to_surface\",\"ids\":\"$flotando\",\"holgura\":\"0\"}"),
            I("move", "Mover", true, false, new string[0], new[] { "dx", "dy", "dz", "to" },
              "mover: dx, dy, dz (longitudes) o to [x,y,z] absoluto (un solo elemento).", "{\"op\":\"move\",\"ids\":\"123\",\"dx\":\"6 in\"}"),
            I("rotate", "Rotar", true, false, new[] { "deg" }, new[] { "centro" },
              "rotar en planta deg grados (antihorario). centro=propio (por defecto, cada uno sobre su punto) o [x,y].", "{\"op\":\"rotate\",\"ids\":\"$l5\",\"deg\":90}"),
            I("mirror", "Simetría", true, false, new[] { "eje" }, new[] { "copiar" },
              "simetría respecto al eje [[x1,y1],[x2,y2]] en planta; copiar=true deja el original.", "{\"op\":\"mirror\",\"ids\":\"123\",\"eje\":[[0,0],[0,10]],\"copiar\":true}"),
            I("copy", "Copiar", true, true, new string[0], new[] { "dx", "dy", "dz", "nivel" },
              "copiar con desplazamiento dx/dy/dz, o a otro nivel (nivel=\"LEVEL 03\": misma posición en planta y misma altura sobre su nivel).",
              "{\"op\":\"copy\",\"ids\":\"$l5_tipo\",\"nivel\":\"LEVEL 06\"}"),
            I("place", "Colocar", false, true, new[] { "type", "level", "puntos" }, new[] { "elev", "montaje", "holgura", "contra", "rot", "ws", "params" },
              "colocar una familia: puntos [[x,y],…] en pies, elev (offset sobre el nivel) o montaje=losa (cara superior en la cara inferior de lo de arriba), rot grados, ws, params {nombre: valor}. Máx. 500 puntos.",
              "{\"op\":\"place\",\"type\":\"LTG_DOWNLIGHT-SURFACE - ROUND: G\",\"level\":\"LEVEL 09\",\"puntos\":[[10.5,22]],\"montaje\":\"losa\",\"ws\":\"LIGHTING - DIST\"}"),
            I("tag", "Etiquetar", true, true, new[] { "view" }, new[] { "tag_type", "leader", "orientacion", "offset", "estilo" },
              "un tag por elemento en la vista (omite los que ya tienen tag ahí). Copia el desplazamiento y el leader de los tags existentes del mismo tipo en esa vista; si no hay, offset [dx,dy] en pulgadas de papel.",
              "{\"op\":\"tag\",\"ids\":\"$sin_tag\",\"view\":17732282}"),
            I("tag_move", "Mover tag", true, false, new string[0], new[] { "head", "dx", "dy", "dxp", "dyp", "leader", "extremo", "codo", "orientacion" },
              "mover cabeza de tags: head [x,y] absoluto, dx/dy (pies) o dxp/dyp (pulgadas de papel); leader true/false; extremo/codo [x,y]; orientacion horizontal|vertical.",
              "{\"op\":\"tag_move\",\"ids\":\"$tags_l5\",\"dxp\":0.25}"),
            I("group_create", "Crear grupo", true, true, new string[0], new[] { "nombre" },
              "agrupar elementos en un grupo de modelo (nombre del tipo de grupo opcional).", "{\"op\":\"group_create\",\"ids\":\"$unidad_a\",\"nombre\":\"UNIT A - LIGHTING\"}"),
            I("ungroup", "Desagrupar", true, false, new string[0], new string[0], "desagrupar grupos (los miembros quedan sueltos).", "{\"op\":\"ungroup\",\"ids\":\"555\"}"),
            I("group_place", "Colocar grupo", false, true, new[] { "tipo", "puntos" }, new string[0],
              "colocar copias de un tipo de grupo (nombre o @n de un group_create del mismo plan) en puntos [[x,y,z]].",
              "{\"op\":\"group_place\",\"tipo\":\"UNIT A - LIGHTING\",\"puntos\":[[100,40,30]]}"),
            I("view_create", "Crear vista", false, true, new[] { "tipo" }, new[] { "nivel", "nombre", "plantilla", "origen", "opcion", "caja", "vista_tipo", "profundidad", "escala" },
              "tipo: plano | rcp | seccion | callout | 3d | duplicar | dibujo (drafting view, escala opcional). nivel (plano/rcp), origen (vista a duplicar o padre del callout), opcion duplicar|detalles|dependiente. caja [[x0,y0,z0],[x1,y1,z1]]: sección = línea de corte de (x0,y0) a (x1,y1) entre z0 y z1, mirando a la izquierda de la línea, profundidad (6' por defecto); callout = rectángulo en planta; 3d = caja de sección.",
              "{\"op\":\"view_create\",\"tipo\":\"rcp\",\"nivel\":\"LEVEL 09\",\"nombre\":\"LEVEL 09 - NON-UNIT LIGHTING\",\"plantilla\":\"10-PRECONSTRUCTION\"}"),
            I("view_set", "Configurar vista", true, false, new string[0], new[] { "plantilla", "rango", "escala", "detalle", "nombre", "recorte", "scope_box", "caja_seccion" },
              "plantilla (nombre o \"ninguna\"), rango {top,corte,fondo,profundidad: [\"NIVEL\", offset]}, escala (96 = 1/8\"), detalle fino|medio|bajo, nombre, recorte true/false, scope_box, caja_seccion (3D). ids de vistas o @n.",
              "{\"op\":\"view_set\",\"ids\":\"@1\",\"rango\":{\"top\":[\"LEVEL 10\",5],\"corte\":[\"LEVEL 09\",0]}}"),
            I("view_hide", "Ocultar en vista", false, false, new[] { "view" }, new[] { "ids", "cats", "ocultar" },
              "ocultar (o mostrar con ocultar=false) elementos o categorías en una vista.", "{\"op\":\"view_hide\",\"view\":123,\"cats\":\"LightingDevices\"}"),
            I("view_override", "Gráficos en vista", false, false, new[] { "view" }, new[] { "ids", "cats", "color", "medio_tono", "transparencia", "grosor", "reset" },
              "sobrescribir gráficos de elementos o categorías: color #RRGGBB, medio_tono, transparencia 0-100, grosor 1-16; reset=true los quita.",
              "{\"op\":\"view_override\",\"view\":123,\"ids\":\"$flotando\",\"color\":\"#D27300\"}"),
            I("sheet_create", "Crear lámina", false, true, new[] { "numero", "nombre" }, new[] { "cajetin" },
              "crear lámina con número, nombre y cajetín \"Familia: Tipo\" (por defecto el primero del modelo).", "{\"op\":\"sheet_create\",\"numero\":\"10E.09.NUL\",\"nombre\":\"LEVEL 09 - NON-UNIT LIGHTING\",\"cajetin\":\"PDI_TITLEBLOCK: 30x42\"}"),
            I("sheet_place", "Colocar vista en lámina", false, true, new[] { "hoja", "vista" }, new[] { "punto", "viewport_tipo" },
              "poner una vista en una lámina (hoja/vista: id, número/nombre o @n). Por defecto al centro del cajetín. Revisa que la vista no esté ya en otra lámina.",
              "{\"op\":\"sheet_place\",\"hoja\":\"@2\",\"vista\":\"@1\",\"viewport_tipo\":\"No Title\"}"),
            I("sheet_set", "Conjunto de impresión", false, true, new[] { "nombre", "hojas" }, new[] { "reemplazar" },
              "guardar un conjunto de impresión (ViewSheetSet) con esas láminas (número, nombre o id; sin @n). Existe y reemplazar≠true: no se toca. El orden de impresión lo decide Revit, no esta lista.",
              "{\"op\":\"sheet_set\",\"nombre\":\"NON-UNIT LIGHTING\",\"hojas\":[\"10E.01.NUL\",\"10E.02.NUL\"]}"),
            I("text_note", "Nota de texto", false, true, new[] { "view", "punto", "texto" }, new[] { "tipo" }, "nota de texto en una vista.",
              "{\"op\":\"text_note\",\"view\":123,\"punto\":[10,20],\"texto\":\"DOUBLE HIGH AREA\"}"),
            I("filled_region", "Región rellena", false, true, new[] { "view", "puntos" }, new[] { "tipo" }, "región rellena (polígono [[x,y],…]) en una vista.",
              "{\"op\":\"filled_region\",\"view\":123,\"tipo\":\"PDI - DOUBLE HIGH AREA\",\"puntos\":[[0,0],[20,0],[20,10],[0,10]]}"),
            // 0.8.0 (Paquete C): dibujo 2D general. Coordenadas de VISTA en pies (planta: x,y del modelo; sección/alzado: derecha/arriba).
            I("detail_line", "Líneas de detalle", false, true, new[] { "view", "puntos" }, new[] { "cerrado", "estilo" },
              "polilínea de líneas de detalle [[x,y],…] (2–2000 vértices); cerrado=true la cierra; estilo = estilo de línea (subcategoría de Lines).",
              "{\"op\":\"detail_line\",\"view\":\"@1\",\"puntos\":[[0,0],[10,0],[10,5]],\"estilo\":\"PDI - MAGENTA\"}"),
            I("detail_arc", "Arco de detalle", false, true, new[] { "view", "centro", "radio", "inicio", "fin" }, new[] { "estilo" },
              "arco de detalle: centro [x,y], radio (pies o con unidades), inicio/fin en grados (antihorario, barrido < 360).",
              "{\"op\":\"detail_arc\",\"view\":123,\"centro\":[5,5],\"radio\":\"6 in\",\"inicio\":0,\"fin\":180}"),
            I("detail_rect", "Rectángulo de detalle", false, true, new[] { "view", "esquinas" }, new[] { "estilo" },
              "rectángulo de líneas de detalle a partir de dos esquinas opuestas [[x0,y0],[x1,y1]].",
              "{\"op\":\"detail_rect\",\"view\":123,\"esquinas\":[[0,0],[4,2]]}"),
            I("detail_polygon", "Polígono de detalle", false, true, new[] { "view", "centro", "radio", "lados" }, new[] { "rot", "estilo" },
              "polígono regular cerrado (símbolos simples): centro, radio, lados 3–64, rot en grados.",
              "{\"op\":\"detail_polygon\",\"view\":123,\"centro\":[2,2],\"radio\":0.5,\"lados\":6}"),
            I("detail_component", "Componente de detalle", false, true, new[] { "view", "tipo", "punto" }, new[] { "rot" },
              "colocar un componente de detalle o símbolo \"Familia: Tipo\" (cargado) en un punto de la vista, rot en grados.",
              "{\"op\":\"detail_component\",\"view\":\"@1\",\"tipo\":\"PDI_RISER_PANEL: 42 CKT\",\"punto\":[3,12]}"),
            I("dimension", "Cota", false, true, new[] { "view", "refs", "linea" }, new[] { "tipo" },
              "cota lineal entre 2+ líneas de detalle paralelas existentes (refs: ids), dibujada sobre linea [[x0,y0],[x1,y1]].",
              "{\"op\":\"dimension\",\"view\":123,\"refs\":\"171001,171002\",\"linea\":[[0,-1],[10,-1]]}"),

            // ---- 0.9.1 (Bloque C/D): modelado tipado con verificación de lo creado ----
            I("level_create", "Crear nivel", false, true, new[] { "elev" }, new[] { "nombre", "vista_planta", "vista_rcp" },
              "nivel a la elevación pedida (longitud con unidades). nombre único; vista_planta / vista_rcp true crean sus vistas.",
              "{\"op\":\"level_create\",\"elev\":\"12' - 0\\\"\",\"nombre\":\"LEVEL 02\",\"vista_planta\":true}"),
            I("grid_create", "Crear rejilla", false, true, new[] { "puntos" }, new[] { "nombre" },
              "rejilla recta de puntos[0] a puntos[1] (pies o con unidades). nombre único.",
              "{\"op\":\"grid_create\",\"puntos\":[[0,0],[0,60]],\"nombre\":\"A\"}"),
            I("wall_create", "Crear muros", false, true, new[] { "puntos", "nivel" }, new[] { "tipo", "altura", "nivel_superior", "desfase", "cerrado", "estructural" },
              "un muro por segmento de la polilínea puntos (cerrado=true une el último con el primero). altura (por defecto 10') o nivel_superior; desfase sobre el nivel.",
              "{\"op\":\"wall_create\",\"puntos\":[[0,0],[30,0],[30,20]],\"nivel\":\"LEVEL 01\",\"tipo\":\"Basic Wall: Generic - 8\\\"\",\"altura\":\"10'\"}"),
            I("floor_create", "Crear suelo", false, true, new[] { "puntos", "nivel" }, new[] { "tipo", "desfase", "estructural" },
              "suelo con contorno cerrado puntos (sin repetir el primero) en el nivel; desfase sobre el nivel.",
              "{\"op\":\"floor_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"LEVEL 01\"}"),
            I("ceiling_create", "Crear techo", false, true, new[] { "puntos", "nivel" }, new[] { "tipo", "altura" },
              "techo con contorno cerrado puntos a la altura pedida sobre el nivel (por defecto 8').",
              "{\"op\":\"ceiling_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"LEVEL 01\",\"altura\":\"9'\"}"),
            I("room_create", "Crear habitación", false, true, new[] { "nivel", "punto" }, new[] { "nombre", "numero" },
              "habitación en el punto [x,y] del nivel (debe estar dentro de un recinto cerrado). La verificación exige área > 0 (delimitada).",
              "{\"op\":\"room_create\",\"nivel\":\"LEVEL 01\",\"punto\":[15,10],\"nombre\":\"OFFICE\",\"numero\":\"101\"}"),
            I("duct_create", "Crear conducto", false, true, new[] { "inicio", "fin", "nivel", "sistema" }, new[] { "tipo", "ancho", "alto", "diametro" },
              "conducto recto de inicio [x,y,z] a fin [x,y,z]; sistema = nombre del tipo de sistema mecánico; tamaño opcional.",
              "{\"op\":\"duct_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"LEVEL 01\",\"sistema\":\"Supply Air\",\"ancho\":\"12 in\",\"alto\":\"8 in\"}"),
            I("pipe_create", "Crear tubería", false, true, new[] { "inicio", "fin", "nivel", "sistema" }, new[] { "tipo", "diametro" },
              "tubería recta de inicio [x,y,z] a fin [x,y,z]; sistema = nombre del tipo de sistema de tuberías; diámetro opcional.",
              "{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"LEVEL 01\",\"sistema\":\"Domestic Cold Water\",\"diametro\":\"1 in\"}"),
            I("circuit_create", "Crear circuito eléctrico", true, true, new string[0], new[] { "panel" },
              "circuito de fuerza con los ids (dispositivos con conector eléctrico libre y del mismo tipo de sistema). panel: nombre o id del cuadro que lo alimenta. Verifica miembros y cuadro (BaseEquipment); NO verifica tensión, polos ni fases: si Revit los rechaza, el plan se revierte.",
              "{\"op\":\"circuit_create\",\"ids\":\"$receptaculos_101\",\"panel\":\"LP-1\"}"),
            I("schedule_create", "Crear tabla", false, true, new[] { "categoria", "campos" }, new[] { "nombre" },
              "tabla de planificación de la categoría (p. ej. LightingFixtures) con los campos en orden (nombres tal como los muestra Revit). Si un campo no existe en la categoría, la tabla no se crea (el plan se revierte) y se listan los campos posibles.",
              "{\"op\":\"schedule_create\",\"categoria\":\"LightingFixtures\",\"campos\":[\"Level\",\"Type\",\"Count\"],\"nombre\":\"LIGHTING FIXTURE SCHEDULE\"}"),
        };

        private static Info I(string kind, string nombre, bool needsIds, bool creates, string[] req, string[] opt, string help, string example) =>
            new Info { Kind = kind, Nombre = nombre, NeedsIds = needsIds, Creates = creates, Required = req, Optional = opt, Help = help, Example = example };

        public static readonly Dictionary<string, Info> ByKind = All.ToDictionary(i => i.Kind, StringComparer.OrdinalIgnoreCase);

        public static string NameOf(string kind) => kind != null && ByKind.TryGetValue(kind, out var i) ? i.Nombre : kind;

        public static string KindList => string.Join(", ", All.Select(i => i.Kind));

        /// <summary>Texto de la guía de escritura (herramienta guide). Se genera del catálogo: nunca se desincroniza.</summary>
        public static string Guide() => Guide(null);

        /// <summary>Guía de escritura; con <paramref name="only"/> (p. ej. "tag,place") sólo esas operaciones: ~10 veces menos texto.</summary>
        public static string Guide(string only)
        {
            var sb = new StringBuilder();
            var filter = string.IsNullOrWhiteSpace(only) ? null
                : new HashSet<string>(only.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
            if (filter != null)
            {
                var unknown = filter.Where(k => All.All(i => !string.Equals(i.Kind, k, StringComparison.OrdinalIgnoreCase))).ToList();
                if (unknown.Count > 0) sb.Append("No existe: ").Append(string.Join(", ", unknown)).Append(". Operaciones: ").AppendLine(KindList);
                foreach (var i in All.Where(x => filter.Contains(x.Kind))) AppendOne(sb, i);
                return sb.ToString();
            }
            sb.AppendLine("ESCRITURA: plan → mostrar el diff al usuario → commit. Un plan = una sola entrada de Deshacer. Máx. 50 ops por plan.");
            sb.AppendLine("ids: lista, \"1,2,3\", $conjunto o @n (elementos creados por la op n del mismo plan, contando desde 1).");
            sb.AppendLine("Longitudes: pies decimales o texto con unidades (10'-9\", 1/2\", 3277 mm, 3.2 m). Puntos [x,y] o [x,y,z] en pies del modelo.");
            sb.AppendLine("Validación antes de aplicar: parámetro de sólo lectura, elemento en grupo o prestado a otro usuario, tipo o vista inexistente, vista ya en otra lámina. Lo omitido sale en 'avisos'.");
            sb.AppendLine("Tras aplicar se releen los elementos tocados ('verificacion') y los creados se guardan en $creados.");
            sb.AppendLine();
            foreach (var i in All) AppendOne(sb, i);
            return sb.ToString();
        }

        private static void AppendOne(StringBuilder sb, Info i)
        {
            sb.Append("- ").Append(i.Kind).Append(" (").Append(i.Nombre).Append("): ");
            if (i.NeedsIds) sb.Append("ids, ");
            if (i.Required.Length > 0) sb.Append(string.Join(", ", i.Required));
            if (i.Optional.Length > 0) sb.Append(" [").Append(string.Join(", ", i.Optional)).Append("]");
            sb.Append(". ").Append(i.Help).AppendLine();
            sb.Append("  ej.: ").AppendLine(i.Example);
        }

        /// <summary>Valida claves obligatorias y formas básicas. Los valores contra el modelo se validan en Revit al planificar.</summary>
        public static void Validate(Info info, IDictionary<string, object> o, bool hasIds)
        {
            if (info.NeedsIds && !hasIds) throw new QueryException(info.Kind + ": falta ids");
            foreach (var r in info.Required)
                if (!o.TryGetValue(r, out var v) || v == null || (v is string s && s.Trim().Length == 0))
                    throw new QueryException(info.Kind + ": falta " + r + ". Uso: " + info.Help + " Ej.: " + info.Example);
            var known = new HashSet<string>(info.Required.Concat(info.Optional).Concat(new[] { "op", "ids" }), StringComparer.OrdinalIgnoreCase);
            var unknown = o.Keys.Where(k => !known.Contains(k)).ToList();
            if (unknown.Count > 0)
                throw new QueryException(info.Kind + ": no conozco " + string.Join(", ", unknown) + ". Claves: " + string.Join(", ", known.Where(k => k != "op")));
        }

        // ---------- lectores tolerantes para argumentos de operaciones ----------

        /// <summary>[x,y] o [x,y,z] en pies (acepta textos con unidades en cada coordenada).</summary>
        public static double[] Point(object v, string what, bool allow3 = true)
        {
            if (!(v is List<object> l) || l.Count < 2 || l.Count > 3 || (!allow3 && l.Count == 3))
                throw new QueryException(what + ": use [x,y]" + (allow3 ? " o [x,y,z]" : "") + " en pies");
            var r = new double[l.Count];
            for (var i = 0; i < l.Count; i++)
            {
                if (!Lengths.TryFeet(l[i], out r[i], out var err)) throw new QueryException(what + ": " + err);
            }
            return r;
        }

        public static List<double[]> Points(object v, string what, int min, int max, bool allow3 = true)
        {
            if (!(v is List<object> l) || l.Count < min) throw new QueryException(what + ": lista de al menos " + min + " puntos [[x,y],…]");
            if (l.Count > max) throw new QueryException(what + ": máximo " + max + " puntos por operación");
            return l.Select((p, i) => Point(p, what + "[" + i + "]", allow3)).ToList();
        }

        public static double? Length(IDictionary<string, object> o, string key, string kind)
        {
            if (!o.TryGetValue(key, out var v) || v == null) return null;
            if (!Lengths.TryFeet(v, out var ft, out var err)) throw new QueryException(kind + " " + key + ": " + err);
            return ft;
        }

        public static double? Number(IDictionary<string, object> o, string key, string kind)
        {
            if (!o.TryGetValue(key, out var v) || v == null) return null;
            if (v is double d) return d;
            if (v is long l) return l;
            if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
            throw new QueryException(kind + " " + key + ": debe ser un número");
        }
    }
}
