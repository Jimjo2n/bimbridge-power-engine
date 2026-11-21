// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Catalog of write operations (plan). A single source for validation, for the guide the AI reads
    /// (guide tema=escritura) and for the names the operator sees in the Queue. Each operation covers API calls
    /// that the 60 pyRevit "modify" scripts on the test machine actually used (counts from the 10/15/2026 analysis).
    /// </summary>
    public static class OpCatalog
    {
        public sealed class Info
        {
            public string Kind;
            public string Name;        // how it is shown in the Queue
            public bool NeedsIds;
            public bool Creates;
            public string[] Required;
            public string[] Optional;
            public string Help;          // one line for the guide
            public string Example;       // example JSON
        }

        public static readonly List<Info> All = new List<Info>
        {
            I("set_param", "Parameter", true, false, new[] { "name", "value" }, new string[0],
              "instance parameter (or view/sheet parameter). value: number in internal units, text with units (\"9' - 3\\\"\", \"2820 mm\"), true/false, or a name for parameters that point to an element (level, phase, scope box, template).",
              "{\"op\":\"set_param\",\"ids\":\"$sin_tag\",\"name\":\"Comments\",\"value\":\"REVIEW\"}"),
            I("set_values", "Values per element", false, false, new[] { "name", "values" }, new string[0],
              "a different value per element: values {\"id\": value}. name: parameter, or elev / ws / type.",
              "{\"op\":\"set_values\",\"name\":\"Mark\",\"values\":{\"17855001\":\"L-01\",\"17855002\":\"L-02\"}}"),
            I("change_type", "Type", true, false, new[] { "type" }, new string[0],
              "change type: \"Family: Type\" (also tags, groups and viewports).", "{\"op\":\"change_type\",\"ids\":\"1,2\",\"type\":\"LTG_STRIP LIGHT: L5\"}"),
            I("set_elevation", "Height", true, false, new string[0], new[] { "ft", "valor" },
              "Elevation from Level: ft (feet) or valor with units (\"9' - 3\\\"\", \"2820 mm\").", "{\"op\":\"set_elevation\",\"ids\":\"$l5\",\"valor\":\"10' - 9\\\"\"}"),
            I("set_workset", "Workset", true, false, new[] { "ws" }, new string[0], "move to another workset by name.", "{\"op\":\"set_workset\",\"ids\":\"$l5\",\"ws\":\"LIGHTING - DIST\"}"),
            I("delete", "Delete", true, false, new string[0], new string[0], "delete the listed elements (Revit also deletes their dependents, e.g. their tags).", "{\"op\":\"delete\",\"ids\":\"$duplicados\"}"),
            I("set_type_param", "Type parameter", false, false, new[] { "name", "value" }, new[] { "type", "ids" },
              "TYPE parameter (affects all its instances): type \"Family: Type\" or instance ids.", "{\"op\":\"set_type_param\",\"type\":\"LTG_STRIP LIGHT: L5\",\"name\":\"Description\",\"value\":\"...\"}"),
            I("set_level", "Level", true, false, new[] { "level" }, new[] { "mantener" },
              "change the assigned level. mantener=posicion (default: adjusts the offset so it does not move) or offset (moves with the level).",
              "{\"op\":\"set_level\",\"ids\":\"$nivel_distinto\",\"level\":\"LEVEL 02\"}"),
            I("snap_to_surface", "Snap to slab", true, false, new string[0], new[] { "lado", "holgura", "contra", "puntos", "max", "vinculos" },
              "uses the geometry to compute the height so the top face touches the bottom face of what is above (lado=abajo: base on the floor). On a slope the nearest corner rules: it never ends up embedded. holgura (default 0), puntos 1/5/9 (9), contra (slabs, beams, ramps, stairs, ceilings).",
              "{\"op\":\"snap_to_surface\",\"ids\":\"$flotando\",\"holgura\":\"0\"}"),
            I("move", "Move", true, false, new string[0], new[] { "dx", "dy", "dz", "to" },
              "move: dx, dy, dz (lengths) or absolute to [x,y,z] (a single element).", "{\"op\":\"move\",\"ids\":\"123\",\"dx\":\"6 in\"}"),
            I("rotate", "Rotate", true, false, new[] { "deg" }, new[] { "centro" },
              "rotate in plan by deg degrees (counterclockwise). centro=propio (default, each one about its own point) or [x,y].", "{\"op\":\"rotate\",\"ids\":\"$l5\",\"deg\":90}"),
            I("mirror", "Mirror", true, false, new[] { "eje" }, new[] { "copiar" },
              "mirror about the axis [[x1,y1],[x2,y2]] in plan; copiar=true keeps the original.", "{\"op\":\"mirror\",\"ids\":\"123\",\"eje\":[[0,0],[0,10]],\"copiar\":true}"),
            I("copy", "Copy", true, true, new string[0], new[] { "dx", "dy", "dz", "nivel" },
              "copy with a dx/dy/dz offset, or to another level (nivel=\"LEVEL 03\": same plan position and same height above its level).",
              "{\"op\":\"copy\",\"ids\":\"$l5_type\",\"nivel\":\"LEVEL 06\"}"),
            I("place", "Place", false, true, new[] { "type", "level", "puntos" }, new[] { "elev", "montaje", "holgura", "contra", "rot", "ws", "params", "origen", "anfitrion", "items_fuente" },
              "place a family: puntos [[x,y],…] in feet, elev (offset above the level) or montaje=losa (top face at the bottom face of what is above), rot degrees, ws, params {name: value}. Max. 500 points. origen: token set by source workflows (PDF); do not write it by hand: it makes the commit idempotent per batch.",
              "{\"op\":\"place\",\"type\":\"LTG_DOWNLIGHT-SURFACE - ROUND: G\",\"level\":\"LEVEL 09\",\"puntos\":[[10.5,22]],\"montaje\":\"losa\",\"ws\":\"LIGHTING - DIST\"}"),
            I("tag", "Tag", true, true, new[] { "view" }, new[] { "tag_type", "leader", "orientacion", "offset", "estilo" },
              "one tag per element in the view (skips those already tagged there). Copies the offset and leader of existing tags of the same type in that view; if there are none, offset [dx,dy] in paper inches.",
              "{\"op\":\"tag\",\"ids\":\"$sin_tag\",\"view\":17732282}"),
            I("tag_move", "Move tag", true, false, new string[0], new[] { "head", "dx", "dy", "dxp", "dyp", "leader", "extremo", "codo", "orientacion" },
              "move tag heads: absolute head [x,y], dx/dy (feet) or dxp/dyp (paper inches); leader true/false; extremo/codo [x,y]; orientacion horizontal|vertical.",
              "{\"op\":\"tag_move\",\"ids\":\"$tags_l5\",\"dxp\":0.25}"),
            I("group_create", "Create group", true, true, new string[0], new[] { "nombre" },
              "group elements into a model group (optional group type name).", "{\"op\":\"group_create\",\"ids\":\"$unit_a\",\"nombre\":\"UNIT A - LIGHTING\"}"),
            I("ungroup", "Ungroup", true, false, new string[0], new string[0], "ungroup groups (the members are left loose).", "{\"op\":\"ungroup\",\"ids\":\"555\"}"),
            I("group_place", "Place group", false, true, new[] { "tipo", "puntos" }, new[] { "nivel", "rot", "ws", "prototipo", "reutilizar" },
              "place copies of a group type (name or @n of a group_create in the same plan) at puntos [[x,y,z]].",
              "{\"op\":\"group_place\",\"tipo\":\"UNIT A - LIGHTING\",\"puntos\":[[100,40,30]]}"),
            I("group_variant", "Create fixture variant of a group", true, true, new[] { "nombre", "reglas" }, Array.Empty<string>(),
              "rebuilds a model group with explicit substitutions by family and type. Checks the position, host and orientation of each member before consolidating.",
              "{\"op\":\"group_variant\",\"ids\":[555],\"nombre\":\"UNIT A - DOWNLIGHTS\",\"reglas\":{\"Old: Round\":\"New: Round\",\"Old: Square\":\"New: Square\"}}"),
            I("view_create", "Create view", false, true, new[] { "tipo" }, new[] { "nivel", "nombre", "plantilla", "origen", "opcion", "caja", "vista_tipo", "profundidad", "escala" },
              "tipo: plano | rcp | seccion | callout | 3d | duplicar | dibujo (drafting view, optional scale). nivel (plano/rcp), origen (view to duplicate or callout parent), opcion duplicar|detalles|dependiente. caja [[x0,y0,z0],[x1,y1,z1]]: section = cut line from (x0,y0) to (x1,y1) between z0 and z1, looking to the left of the line, profundidad (6' by default); callout = rectangle in plan; 3d = section box.",
              "{\"op\":\"view_create\",\"tipo\":\"rcp\",\"nivel\":\"LEVEL 09\",\"nombre\":\"LEVEL 09 - NON-UNIT LIGHTING\",\"plantilla\":\"10-PRECONSTRUCTION\"}"),
            I("view_set", "Configure view", true, false, new string[0], new[] { "plantilla", "rango", "escala", "detalle", "nombre", "recorte", "scope_box", "caja_seccion" },
              "plantilla (name or \"ninguna\"), rango {top,corte,fondo,profundidad: [\"LEVEL\", offset]}, escala (96 = 1/8\"), detalle fino|medio|bajo, nombre, recorte true/false, scope_box, caja_seccion (3D). View ids or @n.",
              "{\"op\":\"view_set\",\"ids\":\"@1\",\"rango\":{\"top\":[\"LEVEL 10\",5],\"corte\":[\"LEVEL 09\",0]}}"),
            I("view_hide", "Hide in view", false, false, new[] { "view" }, new[] { "ids", "cats", "ocultar" },
              "hide (or show with ocultar=false) elements or categories in a view.", "{\"op\":\"view_hide\",\"view\":123,\"cats\":\"LightingDevices\"}"),
            I("view_override", "Graphics in view", false, false, new[] { "view" }, new[] { "ids", "cats", "color", "medio_tono", "transparencia", "grosor", "reset" },
              "override graphics of elements or categories: color #RRGGBB, medio_tono (halftone), transparencia 0-100, grosor (line weight) 1-16; reset=true removes them.",
              "{\"op\":\"view_override\",\"view\":123,\"ids\":\"$flotando\",\"color\":\"#D27300\"}"),
            I("sheet_create", "Create sheet", false, true, new[] { "numero", "nombre" }, new[] { "cajetin" },
              "create a sheet with number, name and titleblock \"Family: Type\" (the model's first one by default).", "{\"op\":\"sheet_create\",\"numero\":\"10E.09.NUL\",\"nombre\":\"LEVEL 09 - NON-UNIT LIGHTING\",\"cajetin\":\"BIMBRIDGE_TITLEBLOCK: 30x42\"}"),
            I("sheet_place", "Place view on sheet", false, true, new[] { "hoja", "vista" }, new[] { "punto", "viewport_tipo" },
              "put a view on a sheet (hoja/vista: id, number/name or @n). Centered on the titleblock by default. Checks that the view is not already on another sheet.",
              "{\"op\":\"sheet_place\",\"hoja\":\"@2\",\"vista\":\"@1\",\"viewport_tipo\":\"No Title\"}"),
            I("sheet_set", "Sheet set", false, true, new[] { "nombre", "hojas" }, new[] { "reemplazar", "preset_pdf" },
              "save a sheet set (ViewSheetSet) with those sheets (number, name or id; no @n). The PDF export uses the saved order and an optional preset_pdf (paper, color, zoom, combine, raster, hide unreferenced tags).",
              "{\"op\":\"sheet_set\",\"nombre\":\"NON-UNIT LIGHTING\",\"hojas\":[\"10E.01.NUL\",\"10E.02.NUL\"]}"),
            I("text_note", "Text note", false, true, new[] { "view", "punto" }, new[] { "texto", "tipo", "negrita", "subrayado", "luminaria", "codigo_param", "altura_param", "descontar_tipo_param" }, "static note or callout bound to a lighting fixture with code and height.",
              "{\"op\":\"text_note\",\"view\":123,\"punto\":[10,20],\"texto\":\"DOUBLE HIGH AREA\"}"),
            I("text_note_refresh", "Regenerate fixture callouts", true, true, Array.Empty<string>(), Array.Empty<string>(),
              "updates the selected managed callouts after re-reading their fixture; keeps the location relative to the origin.",
              "{\"op\":\"text_note_refresh\",\"ids\":[123]}"),
            I("annotation_rebind", "Repair annotation reference", true, true, Array.Empty<string>(), new[] { "destino", "refs_estables" },
              "recreates the chosen tag or dimension in its view; destino is the ID of a tag's new element. A dimension requires the complete ordered list of refs_estables.",
              "{\"op\":\"annotation_rebind\",\"ids\":[101],\"destino\":202}"),
            I("filled_region", "Filled region", false, true, new[] { "view", "puntos" }, new[] { "tipo" }, "filled region (polygon [[x,y],…]) in a view.",
              "{\"op\":\"filled_region\",\"view\":123,\"tipo\":\"Hatch - Double Height\",\"puntos\":[[0,0],[20,0],[20,10],[0,10]]}"),
            // 0.8.0 (Package C): general 2D drawing. VIEW coordinates in feet (plan: model x,y; section/elevation: right/up).
            I("detail_line", "Detail lines", false, true, new[] { "view", "puntos" }, new[] { "cerrado", "estilo" },
              "polyline of detail lines [[x,y],…] (2–2000 vertices); cerrado=true closes it; estilo = line style (subcategory of Lines).",
              "{\"op\":\"detail_line\",\"view\":\"@1\",\"puntos\":[[0,0],[10,0],[10,5]],\"estilo\":\"Lines - Magenta\"}"),
            I("detail_arc", "Detail arc", false, true, new[] { "view", "centro", "radio", "inicio", "fin" }, new[] { "estilo" },
              "detail arc: centro [x,y], radio (feet or with units), inicio/fin in degrees (counterclockwise, sweep < 360).",
              "{\"op\":\"detail_arc\",\"view\":123,\"centro\":[5,5],\"radio\":\"6 in\",\"inicio\":0,\"fin\":180}"),
            I("detail_rect", "Detail rectangle", false, true, new[] { "view", "esquinas" }, new[] { "estilo" },
              "rectangle of detail lines from two opposite corners [[x0,y0],[x1,y1]].",
              "{\"op\":\"detail_rect\",\"view\":123,\"esquinas\":[[0,0],[4,2]]}"),
            I("detail_polygon", "Detail polygon", false, true, new[] { "view", "centro", "radio", "lados" }, new[] { "rot", "estilo" },
              "closed regular polygon (simple symbols): centro, radio, lados 3–64, rot in degrees.",
              "{\"op\":\"detail_polygon\",\"view\":123,\"centro\":[2,2],\"radio\":0.5,\"lados\":6}"),
            I("detail_component", "Detail component", false, true, new[] { "view", "tipo", "punto" }, new[] { "rot" },
              "place a detail component or symbol \"Family: Type\" (loaded) at a point of the view, rot in degrees.",
              "{\"op\":\"detail_component\",\"view\":\"@1\",\"tipo\":\"Panel: 42 CKT\",\"punto\":[3,12]}"),
            I("dimension", "Dimension", false, true, new[] { "view", "refs", "linea" }, new[] { "tipo" },
              "linear dimension between 2+ existing parallel detail lines (refs: ids), drawn along linea [[x0,y0],[x1,y1]].",
              "{\"op\":\"dimension\",\"view\":123,\"refs\":\"171001,171002\",\"linea\":[[0,-1],[10,-1]]}"),

            // ---- 0.9.1 (Block C/D): typed modeling with verification of what is created ----
            I("level_create", "Create level", false, true, new[] { "elev" }, new[] { "nombre", "vista_planta", "vista_rcp" },
              "level at the requested elevation (length with units). Unique nombre; vista_planta / vista_rcp true create its views.",
              "{\"op\":\"level_create\",\"elev\":\"12' - 0\\\"\",\"nombre\":\"LEVEL 02\",\"vista_planta\":true}"),
            I("grid_create", "Create grid", false, true, new[] { "puntos" }, new[] { "nombre" },
              "straight grid from puntos[0] to puntos[1] (feet or with units). Unique nombre.",
              "{\"op\":\"grid_create\",\"puntos\":[[0,0],[0,60]],\"nombre\":\"A\"}"),
            I("wall_create", "Create walls", false, true, new[] { "puntos", "nivel" }, new[] { "tipo", "altura", "nivel_superior", "desfase", "cerrado", "estructural" },
              "one wall per segment of the puntos polyline (cerrado=true joins the last one to the first). altura (10' by default) or nivel_superior; desfase (offset) above the level.",
              "{\"op\":\"wall_create\",\"puntos\":[[0,0],[30,0],[30,20]],\"nivel\":\"LEVEL 01\",\"tipo\":\"Basic Wall: Generic - 8\\\"\",\"altura\":\"10'\"}"),
            I("floor_create", "Create floor", false, true, new[] { "puntos", "nivel" }, new[] { "tipo", "desfase", "estructural" },
              "floor with closed boundary puntos (without repeating the first one) on the level; desfase (offset) above the level.",
              "{\"op\":\"floor_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"LEVEL 01\"}"),
            I("ceiling_create", "Create ceiling", false, true, new[] { "puntos", "nivel" }, new[] { "tipo", "altura" },
              "ceiling with closed boundary puntos at the requested height above the level (8' by default).",
              "{\"op\":\"ceiling_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"LEVEL 01\",\"altura\":\"9'\"}"),
            I("room_create", "Create room", false, true, new[] { "nivel", "punto" }, new[] { "nombre", "numero" },
              "room at point [x,y] of the level (must be inside a closed enclosure). Verification requires area > 0 (bounded).",
              "{\"op\":\"room_create\",\"nivel\":\"LEVEL 01\",\"punto\":[15,10],\"nombre\":\"OFFICE\",\"numero\":\"101\"}"),
            I("duct_create", "Create duct", false, true, new[] { "inicio", "fin", "nivel", "sistema" }, new[] { "tipo", "ancho", "alto", "diametro" },
              "straight duct from inicio [x,y,z] to fin [x,y,z]; sistema = mechanical system type name; optional size.",
              "{\"op\":\"duct_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"LEVEL 01\",\"sistema\":\"Supply Air\",\"ancho\":\"12 in\",\"alto\":\"8 in\"}"),
            I("pipe_create", "Create pipe", false, true, new[] { "inicio", "fin", "nivel", "sistema" }, new[] { "tipo", "diametro" },
              "straight pipe from inicio [x,y,z] to fin [x,y,z]; sistema = piping system type name; optional diameter.",
              "{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"LEVEL 01\",\"sistema\":\"Domestic Cold Water\",\"diametro\":\"1 in\"}"),
            I("conduit_create", "Create conduit", false, true, new[] { "inicio", "fin", "nivel", "tipo", "diametro" }, new[] { "ws" },
              "straight conduit from inicio [x,y,z] to fin [x,y,z]; requires tipo, nivel and diameter. ws assigns the workset.",
              "{\"op\":\"conduit_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"LEVEL 01\",\"tipo\":\"EMT\",\"diametro\":\"1.25 in\"}"),
            I("conduit_route", "Create or replace a conduit run", false, true, new[] { "puntos", "nivel", "tipo", "diametro" }, new[] { "ws", "reemplazar_ids", "terminal_inicio", "terminal_fin" },
              "open run with straight segments and elbows between XYZ vertices. It can join free terminal connectors or replace the IDs of a run while keeping its ends and connections.",
              "{\"op\":\"conduit_route\",\"puntos\":[[0,0,9],[20,0,9],[20,10,9]],\"nivel\":\"LEVEL 01\",\"tipo\":\"EMT\",\"diametro\":\"1.25 in\"}"),
            I("circuit_create", "Create electrical circuit", true, true, new string[0], new[] { "panel" },
              "power circuit with the ids (devices with a free electrical connector and of the same system type). panel: name or id of the panel that feeds it. Verifies members and panel (BaseEquipment); does NOT verify voltage, poles or phases: if Revit rejects them, the plan is rolled back.",
              "{\"op\":\"circuit_create\",\"ids\":\"$receptacles_101\",\"panel\":\"LP-1\"}"),
            I("schedule_create", "Create schedule", false, true, new[] { "categoria", "campos" }, new[] { "nombre" },
              "schedule for the category (e.g. LightingFixtures) with the fields in order (names as Revit shows them). If a field does not exist in the category, the schedule is not created (the plan is rolled back) and the possible fields are listed.",
              "{\"op\":\"schedule_create\",\"categoria\":\"LightingFixtures\",\"campos\":[\"Level\",\"Type\",\"Count\"],\"nombre\":\"LIGHTING FIXTURE SCHEDULE\"}"),
        };

        private static Info I(string kind, string name, bool needsIds, bool creates, string[] req, string[] opt, string help, string example) =>
            new Info { Kind = kind, Name = name, NeedsIds = needsIds, Creates = creates, Required = req, Optional = opt, Help = help, Example = example };

        public static readonly Dictionary<string, Info> ByKind = All.ToDictionary(i => i.Kind, StringComparer.OrdinalIgnoreCase);

        public static string NameOf(string kind) => kind != null && ByKind.TryGetValue(kind, out var i) ? i.Name : kind;

        public static string KindList => string.Join(", ", All.Select(i => i.Kind));

        /// <summary>Write guide text (guide tool). Generated from the catalog: never out of sync.</summary>
        public static string Guide() => Guide(null);

        /// <summary>Write guide; with <paramref name="only"/> (e.g. "tag,place") only those operations: ~10 times less text.</summary>
        public static string Guide(string only)
        {
            var sb = new StringBuilder();
            var filter = string.IsNullOrWhiteSpace(only) ? null
                : new HashSet<string>(only.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
            if (filter != null)
            {
                var unknown = filter.Where(k => All.All(i => !string.Equals(i.Kind, k, StringComparison.OrdinalIgnoreCase))).ToList();
                if (unknown.Count > 0) sb.Append("Does not exist: ").Append(string.Join(", ", unknown)).Append(". Operations: ").AppendLine(KindList);
                foreach (var i in All.Where(x => filter.Contains(x.Kind))) AppendOne(sb, i);
                return sb.ToString();
            }
            sb.AppendLine("WRITING: plan → show the diff to the user → commit. One plan = a single Undo entry. Max. 50 ops per plan.");
            sb.AppendLine("ids: list, \"1,2,3\", $set or @n (elements created by op n of the same plan, counting from 1).");
            sb.AppendLine("Lengths: decimal feet or text with units (10'-9\", 1/2\", 3277 mm, 3.2 m). Points [x,y] or [x,y,z] in model feet.");
            sb.AppendLine("Validation before applying: read-only parameter, element in a group or borrowed by another user, nonexistent type or view, view already on another sheet. What is skipped appears in 'avisos'.");
            sb.AppendLine("After applying, the touched elements are re-read ('verificacion') and the created ones are saved in $creados.");
            sb.AppendLine();
            foreach (var i in All) AppendOne(sb, i);
            return sb.ToString();
        }

        private static void AppendOne(StringBuilder sb, Info i)
        {
            sb.Append("- ").Append(i.Kind).Append(" (").Append(i.Name).Append("): ");
            if (i.NeedsIds) sb.Append("ids, ");
            if (i.Required.Length > 0) sb.Append(string.Join(", ", i.Required));
            if (i.Optional.Length > 0) sb.Append(" [").Append(string.Join(", ", i.Optional)).Append("]");
            sb.Append(". ").Append(i.Help).AppendLine();
            sb.Append("  e.g.: ").AppendLine(i.Example);
        }

        /// <summary>Validates required keys and basic shapes. Values against the model are validated in Revit when planning.</summary>
        public static void Validate(Info info, IDictionary<string, object> o, bool hasIds)
        {
            if (info.NeedsIds && !hasIds) throw new QueryException(info.Kind + ": ids is missing");
            foreach (var r in info.Required)
                if (!o.TryGetValue(r, out var v) || v == null || (v is string s && s.Trim().Length == 0))
                    throw new QueryException(info.Kind + ": " + r + " is missing. Usage: " + info.Help + " E.g.: " + info.Example);
            var known = new HashSet<string>(info.Required.Concat(info.Optional).Concat(new[] { "op", "ids" }), StringComparer.OrdinalIgnoreCase);
            var unknown = o.Keys.Where(k => !known.Contains(k)).ToList();
            if (unknown.Count > 0)
                throw new QueryException(info.Kind + ": unknown " + string.Join(", ", unknown) + ". Keys: " + string.Join(", ", known.Where(k => k != "op")));
        }

        // ---------- tolerant readers for operation arguments ----------

        /// <summary>[x,y] or [x,y,z] in feet (accepts text with units in each coordinate).</summary>
        public static double[] Point(object v, string what, bool allow3 = true)
        {
            if (!(v is List<object> l) || l.Count < 2 || l.Count > 3 || (!allow3 && l.Count == 3))
                throw new QueryException(what + ": use [x,y]" + (allow3 ? " or [x,y,z]" : "") + " in feet");
            var r = new double[l.Count];
            for (var i = 0; i < l.Count; i++)
            {
                if (!Lengths.TryFeet(l[i], out r[i], out var err)) throw new QueryException(what + ": " + err);
            }
            return r;
        }

        public static List<double[]> Points(object v, string what, int min, int max, bool allow3 = true)
        {
            if (!(v is List<object> l) || l.Count < min) throw new QueryException(what + ": list of at least " + min + " points [[x,y],…]");
            if (l.Count > max) throw new QueryException(what + ": maximum " + max + " points per operation");
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
            throw new QueryException(kind + " " + key + ": must be a number");
        }
    }
}
