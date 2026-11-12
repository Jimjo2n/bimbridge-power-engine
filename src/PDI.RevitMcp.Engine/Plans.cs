// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Una operación pedida por la IA en "plan" (sección 7). Sólo operaciones tipadas; nunca código libre.
    /// El catálogo completo (27 operaciones) está en <see cref="OpCatalog"/>.
    /// ids: lista de ids, "1,2,3", "$conjunto" o "@n" (lo que creó la operación n del mismo plan).
    /// </summary>
    public sealed class OpSpec
    {
        public string Kind;
        /// <summary>Posición en el plan, desde 1 (para referencias @n).</summary>
        public int Index;
        public List<long> Ids = new List<long>();
        public string Set;
        /// <summary>Operaciones anteriores del mismo plan cuyos elementos creados se usan como ids.</summary>
        public List<int> RefOps = new List<int>();
        /// <summary>Argumentos tal como llegaron (ya validadas las claves).</summary>
        public Dictionary<string, object> Args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Atajos de las operaciones originales.
        public string Name;
        public object Value;
        public string Type;
        public string Workset;
        public double? Ft;

        public static string[] Kinds => OpCatalog.All.Select(i => i.Kind).ToArray();
        public const int MaxOps = 200;

        public static List<OpSpec> ParseAll(IDictionary<string, object> args)
        {
            if (!args.TryGetValue("ops", out var raw) || !(raw is List<object> list) || list.Count == 0)
                throw new QueryException("plan necesita ops: [{op, ids, ...}]. Operaciones: " + OpCatalog.KindList + " (detalle: guide tema=escritura)");
            if (list.Count > MaxOps) throw new QueryException("máximo " + MaxOps + " operaciones por plan");
            var result = new List<OpSpec>();
            foreach (var item in list)
            {
                if (!(item is Dictionary<string, object> o)) throw new QueryException("cada op debe ser un objeto");
                var s = Parse(o, result.Count + 1);
                foreach (var r in s.RefOps)
                {
                    if (r < 1 || r >= s.Index) throw new QueryException(s.Kind + ": @" + r + " debe ser una operación anterior del mismo plan");
                    if (!OpCatalog.ByKind[result[r - 1].Kind].Creates)
                        throw new QueryException(s.Kind + ": @" + r + " (" + result[r - 1].Kind + ") no crea elementos");
                }
                result.Add(s);
            }
            return result;
        }

        public static OpSpec Parse(IDictionary<string, object> o, int index = 1)
        {
            var s = new OpSpec { Kind = (Arg.GetString(o, "op") ?? "").Trim().ToLowerInvariant(), Index = index };
            if (!OpCatalog.ByKind.TryGetValue(s.Kind, out var info))
                throw new QueryException("op desconocida '" + s.Kind + "'. Use: " + OpCatalog.KindList + " (detalle: guide tema=escritura)");
            foreach (var kv in o) s.Args[kv.Key] = kv.Value;

            foreach (var x in Arg.GetStringList(o, "ids"))
            {
                if (x.StartsWith("$")) { s.Set = NamedSets.Normalize(x); continue; }
                if (x.StartsWith("@"))
                {
                    if (!int.TryParse(x.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        throw new QueryException("ids: '" + x + "' debe ser @n (número de una operación anterior)");
                    s.RefOps.Add(n);
                    continue;
                }
                if (!long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    throw new QueryException("ids: '" + x + "' no es un id, un $conjunto ni @n");
                s.Ids.Add(id);
            }
            foreach (var key in new[] { "view", "hoja", "vista", "tipo", "origen" })
            {
                var v = Arg.GetString(o, key);
                if (v != null && v.StartsWith("@") && int.TryParse(v.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    s.RefOps.Add(n);
            }
            var hasIds = s.Ids.Count > 0 || s.Set != null || s.RefOps.Count > 0;
            OpCatalog.Validate(info, o, hasIds);

            switch (s.Kind)
            {
                case "set_param":
                    s.Name = Arg.GetString(o, "name");
                    o.TryGetValue("value", out s.Value);
                    break;
                case "set_values":
                    s.Name = Arg.GetString(o, "name");
                    if (!(o["values"] is Dictionary<string, object> vals) || vals.Count == 0)
                        throw new QueryException("set_values: values debe ser {\"id\": valor, ...}");
                    if (vals.Count > PlanOpsLimits.MaxEntries) throw new QueryException("set_values: máximo " + PlanOpsLimits.MaxEntries + " valores");
                    foreach (var k in vals.Keys)
                    {
                        if (!long.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vid))
                            throw new QueryException("set_values: la clave '" + k + "' no es un id");
                        s.Ids.Add(vid);
                    }
                    break;
                case "change_type":
                    s.Type = Arg.GetString(o, "type");
                    break;
                case "set_elevation":
                    if (o.TryGetValue("ft", out var ft) && ft != null)
                    {
                        if (!(ft is double || ft is long)) throw new QueryException("set_elevation: ft debe ser un número en pies (o use valor con unidades)");
                        s.Ft = Convert.ToDouble(ft, CultureInfo.InvariantCulture);
                    }
                    else if (o.TryGetValue("valor", out var valor) && valor != null)
                    {
                        if (!Lengths.TryFeet(valor, out var f, out var err)) throw new QueryException("set_elevation valor: " + err);
                        s.Ft = f;
                    }
                    else throw new QueryException("set_elevation: falta ft (pies) o valor (\"10' - 9\\\"\", \"3277 mm\")");
                    if (s.Ft < -1000 || s.Ft > 1000) throw new QueryException("set_elevation: fuera de rango");
                    break;
                case "set_workset":
                    s.Workset = Arg.GetString(o, "ws");
                    break;
                case "set_type_param":
                    s.Name = Arg.GetString(o, "name");
                    o.TryGetValue("value", out s.Value);
                    s.Type = Arg.GetString(o, "type");
                    if (s.Type == null && !hasIds) throw new QueryException("set_type_param: indique type \"Familia: Tipo\" o ids de instancias");
                    break;
                case "snap_to_surface":
                    if (s.RefOps.Count > 0) throw new QueryException("snap_to_surface mide elementos que ya existen: no use @n (para colocar a ras use place con montaje=losa)");
                    var lado = (Arg.GetString(o, "lado") ?? "arriba").Trim().ToLowerInvariant();
                    if (lado != "arriba" && lado != "abajo") throw new QueryException("snap_to_surface lado: arriba (por defecto) o abajo");
                    OpCatalog.Length(o, "holgura", "snap_to_surface");
                    OpCatalog.Length(o, "max", "snap_to_surface");
                    var pts = OpCatalog.Number(o, "puntos", "snap_to_surface");
                    if (pts.HasValue && pts != 1 && pts != 5 && pts != 9) throw new QueryException("snap_to_surface puntos: 1, 5 o 9");
                    break;
                case "move":
                    if (o.ContainsKey("to"))
                    {
                        OpCatalog.Point(o["to"], "move to");
                        if (s.Ids.Count + (s.Set != null ? 2 : 0) + s.RefOps.Count != 1) throw new QueryException("move to: sólo con un elemento; para varios use dx/dy/dz");
                    }
                    else
                    {
                        var any = false;
                        foreach (var k in new[] { "dx", "dy", "dz" }) any |= OpCatalog.Length(o, k, "move").HasValue;
                        if (!any) throw new QueryException("move: indique dx, dy, dz o to [x,y,z]");
                    }
                    break;
                case "rotate":
                    OpCatalog.Number(o, "deg", "rotate");
                    if (o.TryGetValue("centro", out var c) && c is List<object>) OpCatalog.Point(c, "rotate centro", false);
                    break;
                case "mirror":
                    var eje = OpCatalog.Points(o["eje"], "mirror eje", 2, 2, false);
                    if (Math.Abs(eje[0][0] - eje[1][0]) + Math.Abs(eje[0][1] - eje[1][1]) < 1e-6) throw new QueryException("mirror eje: los dos puntos son iguales");
                    break;
                case "copy":
                    if (!o.ContainsKey("nivel"))
                    {
                        var anyc = false;
                        foreach (var k in new[] { "dx", "dy", "dz" }) anyc |= OpCatalog.Length(o, k, "copy").HasValue;
                        if (!anyc) throw new QueryException("copy: indique dx/dy/dz o nivel");
                    }
                    break;
                case "place":
                    OpCatalog.Points(o["puntos"], "place puntos", 1, 5000);
                    OpCatalog.Length(o, "elev", "place");
                    OpCatalog.Length(o, "holgura", "place");
                    OpCatalog.Number(o, "rot", "place");
                    var montaje = Arg.GetString(o, "montaje");
                    if (montaje != null && !string.Equals(montaje, "losa", StringComparison.OrdinalIgnoreCase) && !string.Equals(montaje, "piso", StringComparison.OrdinalIgnoreCase))
                        throw new QueryException("place montaje: losa (cara superior bajo lo de arriba) o piso (base sobre lo de abajo)");
                    if (montaje != null && o.ContainsKey("elev")) throw new QueryException("place: use elev o montaje, no los dos");
                    if (o.TryGetValue("params", out var pr) && pr != null && !(pr is Dictionary<string, object>))
                        throw new QueryException("place params: {\"Parámetro\": valor}");
                    break;
                case "tag":
                    if (o.TryGetValue("offset", out var off) && off != null) OpCatalog.Point(off, "tag offset", false);
                    var ori = Arg.GetString(o, "orientacion");
                    if (ori != null && ori != "horizontal" && ori != "vertical") throw new QueryException("tag orientacion: horizontal o vertical");
                    break;
                case "tag_move":
                    if (o.TryGetValue("head", out var h) && h != null) OpCatalog.Point(h, "tag_move head", false);
                    foreach (var k in new[] { "dx", "dy" }) OpCatalog.Length(o, k, "tag_move");
                    foreach (var k in new[] { "dxp", "dyp" }) OpCatalog.Number(o, k, "tag_move");
                    if (o.TryGetValue("extremo", out var ex) && ex != null) OpCatalog.Point(ex, "tag_move extremo", false);
                    if (o.TryGetValue("codo", out var co) && co != null) OpCatalog.Point(co, "tag_move codo", false);
                    if (!new[] { "head", "dx", "dy", "dxp", "dyp", "leader", "extremo", "codo", "orientacion" }.Any(o.ContainsKey))
                        throw new QueryException("tag_move: indique head, dx/dy, dxp/dyp, leader, extremo, codo u orientacion");
                    break;
                case "group_place":
                    OpCatalog.Points(o["puntos"], "group_place puntos", 1, 1000);
                    break;
                case "view_create":
                    var vt = (Arg.GetString(o, "tipo") ?? "").Trim().ToLowerInvariant();
                    var allowed = new[] { "plano", "rcp", "seccion", "callout", "3d", "duplicar", "dibujo" };
                    if (Array.IndexOf(allowed, vt) < 0) throw new QueryException("view_create tipo: " + string.Join(", ", allowed));
                    if ((vt == "plano" || vt == "rcp") && Arg.GetString(o, "nivel") == null) throw new QueryException("view_create " + vt + ": falta nivel");
                    if ((vt == "duplicar" || vt == "callout") && Arg.GetString(o, "origen") == null) throw new QueryException("view_create " + vt + ": falta origen (vista)");
                    if ((vt == "seccion" || vt == "callout") && !o.ContainsKey("caja")) throw new QueryException("view_create " + vt + ": falta caja [[x0,y0,z0],[x1,y1,z1]]");
                    if (o.ContainsKey("caja")) OpCatalog.Points(o["caja"], "view_create caja", 2, 2);
                    var opc = Arg.GetString(o, "opcion");
                    if (opc != null && opc != "duplicar" && opc != "detalles" && opc != "dependiente") throw new QueryException("view_create opcion: duplicar, detalles o dependiente");
                    break;
                case "view_set":
                    if (!new[] { "plantilla", "rango", "escala", "detalle", "nombre", "recorte", "scope_box", "caja_seccion" }.Any(o.ContainsKey))
                        throw new QueryException("view_set: indique plantilla, rango, escala, detalle, nombre, recorte, scope_box o caja_seccion");
                    if (o.TryGetValue("rango", out var rg) && rg != null)
                    {
                        if (!(rg is Dictionary<string, object> rd) || rd.Count == 0) throw new QueryException("view_set rango: {top:[\"NIVEL\",offset], corte:[…], fondo:[…], profundidad:[…]}");
                        foreach (var kv in rd)
                        {
                            if (Array.IndexOf(new[] { "top", "corte", "fondo", "profundidad" }, kv.Key) < 0) throw new QueryException("view_set rango: claves top, corte, fondo, profundidad");
                            if (!(kv.Value is List<object> pair) || pair.Count != 2) throw new QueryException("view_set rango " + kv.Key + ": [\"NIVEL\", offset] (nivel: nombre, actual, arriba, abajo o ilimitado)");
                            if (!Lengths.TryFeet(pair[1], out _, out var err)) throw new QueryException("view_set rango " + kv.Key + ": " + err);
                        }
                    }
                    if (o.ContainsKey("caja_seccion")) OpCatalog.Points(o["caja_seccion"], "view_set caja_seccion", 2, 2);
                    var det = Arg.GetString(o, "detalle");
                    if (det != null && det != "fino" && det != "medio" && det != "bajo") throw new QueryException("view_set detalle: fino, medio o bajo");
                    OpCatalog.Number(o, "escala", "view_set");
                    break;
                case "view_hide":
                case "view_override":
                    if (!o.ContainsKey("ids") && !o.ContainsKey("cats")) throw new QueryException(s.Kind + ": indique ids o cats");
                    OpCatalog.Number(o, "transparencia", s.Kind);
                    OpCatalog.Number(o, "grosor", s.Kind);
                    break;
                case "text_note":
                    OpCatalog.Point(o["punto"], "text_note punto", false);
                    break;
                case "level_create":
                case "grid_create":
                case "wall_create":
                case "floor_create":
                case "ceiling_create":
                case "room_create":
                case "duct_create":
                case "pipe_create":
                case "circuit_create":
                case "schedule_create":
                    ModelOps.Validate(s.Kind, o, s);
                    break;
                case "filled_region":
                {
                    var fp = OpCatalog.Points(o["puntos"], "filled_region puntos", 3, 200, false);
                    var le = Draw2D.ValidateLoop(fp);
                    if (le != null) throw new QueryException("filled_region: " + le);
                    break;
                }
                case "detail_line":
                {
                    var lp = OpCatalog.Points(o["puntos"], "detail_line puntos", 2, Draw2D.MaxVertices, false);
                    Draw2D.Segments(lp, Arg.GetBool(o, "cerrado") == true, out var le);
                    if (le != null) throw new QueryException("detail_line: " + le);
                    break;
                }
                case "detail_rect":
                {
                    var ec = OpCatalog.Points(o["esquinas"], "detail_rect esquinas", 2, 2, false);
                    Draw2D.Rect(ec[0], ec[1], out var le);
                    if (le != null) throw new QueryException("detail_rect: " + le);
                    break;
                }
                case "detail_arc":
                {
                    OpCatalog.Point(o["centro"], "detail_arc centro", false);
                    if (!Lengths.TryFeet(o["radio"], out var rad, out var rerr)) throw new QueryException("detail_arc radio: " + rerr);
                    var a0 = OpCatalog.Number(o, "inicio", "detail_arc") ?? throw new QueryException("detail_arc: falta inicio (grados)");
                    var a1 = OpCatalog.Number(o, "fin", "detail_arc") ?? throw new QueryException("detail_arc: falta fin (grados)");
                    var ae = Draw2D.ValidateArc(rad, a0, a1);
                    if (ae != null) throw new QueryException("detail_arc: " + ae);
                    break;
                }
                case "detail_polygon":
                {
                    var pc = OpCatalog.Point(o["centro"], "detail_polygon centro", false);
                    if (!Lengths.TryFeet(o["radio"], out var rad, out var rerr)) throw new QueryException("detail_polygon radio: " + rerr);
                    var sides = OpCatalog.Number(o, "lados", "detail_polygon") ?? 0;
                    Draw2D.RegularPolygon(pc, rad, (int)sides, OpCatalog.Number(o, "rot", "detail_polygon") ?? 0, out var pe);
                    if (pe != null) throw new QueryException("detail_polygon: " + pe);
                    break;
                }
                case "detail_component":
                    OpCatalog.Point(o["punto"], "detail_component punto", false);
                    OpCatalog.Number(o, "rot", "detail_component");
                    break;
                case "dimension":
                {
                    var dl = OpCatalog.Points(o["linea"], "dimension linea", 2, 2, false);
                    Draw2D.Segments(dl, false, out var de);
                    if (de != null) throw new QueryException("dimension linea: " + de);
                    var refs = (Arg.GetString(o, "refs") ?? "").Split(',').Where(x => x.Trim().Length > 0).ToList();
                    if (refs.Count < 2) throw new QueryException("dimension refs: al menos 2 ids de líneas de detalle");
                    if (refs.Any(x => x.Trim().StartsWith("@", StringComparison.Ordinal))) throw new QueryException("dimension refs: en 0.8.0 sólo ids existentes (no @n del mismo plan): cree las líneas en un plan y acote en el siguiente");
                    break;
                }
                case "sheet_place":
                    if (o.TryGetValue("punto", out var sp) && sp != null) OpCatalog.Point(sp, "sheet_place punto", false);
                    break;
                case "sheet_set":
                {
                    if (string.IsNullOrWhiteSpace(Arg.GetString(o, "nombre"))) throw new QueryException("sheet_set: falta nombre");
                    var hs = o.TryGetValue("hojas", out var hv) && hv is List<object> hl ? hl.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "").ToList() : null;
                    if (hs == null || hs.Count == 0 || hs.Count > 1000 || hs.Any(x => x.Length == 0)) throw new QueryException("sheet_set hojas: lista de 1 a 1000 láminas (número, nombre o id)");
                    if (hs.Any(x => x.StartsWith("@", StringComparison.Ordinal))) throw new QueryException("sheet_set hojas: sin @n; cree las láminas en un plan y guarde el conjunto en el siguiente");
                    break;
                }
            }
            return s;
        }
    }

    public static class PlanOpsLimits
    {
        public const int MaxEntries = 20000;
    }

    /// <summary>Un cambio concreto sobre un elemento, con el valor que tenía al planificar y el que tendrá.</summary>
    public sealed class PlanEntry : IJsonWritable
    {
        /// <summary>Elemento que cambia; 0 si la entrada crea uno nuevo.</summary>
        public long Id;
        public string Op;
        /// <summary>Qué se cambia: nombre del parámetro, "type", "elev", "ws", "exists", "loc", "nuevo"…</summary>
        public string Key;
        /// <summary>Valor canónico (texto) al planificar. Al aplicar se compara con el actual: si difiere, hay conflicto.</summary>
        public string Before;
        public string After;
        /// <summary>Valor tipado para aplicar (double/long/string/bool), id de destino o un objeto con los datos de creación.</summary>
        public object Target;
        /// <summary>Operación del plan que la generó (desde 1), para resolver @n.</summary>
        public int OpIndex;
        /// <summary>Texto legible del "después" cuando After es técnico (p. ej. una creación).</summary>
        public string Show;
        /// <summary>
        /// &gt; 0: la entrada se aplica a lo que cree la operación Ref del mismo plan (ids=@n). Al planificar esos elementos aún no
        /// existen: no hay "antes" que comparar.
        /// </summary>
        public int Ref;

        public bool IsCreation => Id == 0 && Ref == 0;
        public bool IsTemplate => Ref > 0;

        public object ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id, ["op"] = Op, ["key"] = Key, ["before"] = Before, ["after"] = After, ["target"] = Target,
            };
            if (OpIndex > 0) d["n"] = (long)OpIndex;
            if (Show != null) d["show"] = Show;
            if (Ref > 0) d["ref"] = (long)Ref;
            return d;
        }

        public static PlanEntry FromJson(Dictionary<string, object> o) => new PlanEntry
        {
            Id = Arg.GetLong(o, "id") ?? 0,
            Op = Arg.GetString(o, "op"),
            Key = Arg.GetString(o, "key"),
            Before = Arg.GetString(o, "before"),
            After = Arg.GetString(o, "after"),
            Target = o.TryGetValue("target", out var t) ? t : null,
            OpIndex = (int)(Arg.GetLong(o, "n") ?? 0),
            Show = Arg.GetString(o, "show"),
            Ref = (int)(Arg.GetLong(o, "ref") ?? 0),
        };

        /// <summary>
        /// ¿Dos valores canónicos son el mismo? Igual texto, o los mismos números separados por , | ; = + y espacios con una diferencia
        /// menor que <paramref name="tol"/> (pies: 1e-5 ft = 0.003 mm). Evita conflictos falsos por ruido de coma flotante al encadenar
        /// planes retenidos y al verificar tras aplicar.
        /// </summary>
        public static bool Same(string a, string b, double tol = 1e-5)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            if (a == null || b == null) return false;
            var ta = Tokens(a); var tb = Tokens(b);
            if (ta.Count != tb.Count) return false;
            for (var i = 0; i < ta.Count; i++)
            {
                if (string.Equals(ta[i], tb[i], StringComparison.Ordinal)) continue;
                if (!double.TryParse(ta[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
                if (!double.TryParse(tb[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
                if (Math.Abs(x - y) > tol)
                {
                    // Ángulos: 359.99999 y 0 son el mismo giro.
                    if (!(Math.Abs(Math.Abs(x - y) - 360) <= tol)) return false;
                }
            }
            return true;
        }

        private static List<string> Tokens(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                if (ch == ',' || ch == '|' || ch == ';' || ch == '=' || ch == ' ' || (ch == '+' && sb.Length > 0))
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                    list.Add(ch.ToString());
                    continue;
                }
                sb.Append(ch);
            }
            list.Add(sb.ToString());
            return list;
        }

        /// <summary>Forma canónica para comparar valores sin problemas de redondeo ni de formato.</summary>
        public static string Canon(object v)
        {
            switch (v)
            {
                case null: return "";
                case double d: return Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture);
                case float f: return Math.Round(f, 6).ToString("R", CultureInfo.InvariantCulture);
                case bool b: return b ? "1" : "0";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }
    }

    public static class PlanStatus
    {
        public const string Listo = "listo";            // planificado, esperando commit
        public const string EnProceso = "en_proceso";
        public const string Retenido = "retenido";      // en la cola de Revit, esperando que el usuario lo aplique
        public const string Aplicado = "aplicado";
        public const string Conflicto = "conflicto";
        public const string Revertido = "revertido";
        public const string Descartado = "descartado";
        public const string Vencido = "vencido";
        /// <summary>alpha3: resultado de commit modo=ensayo (aplicado, verificado y REVERTIDO). No es un estado del plan:
        /// el plan vuelve a "listo". Nunca equivale a aplicado.</summary>
        public const string Ensayado = "ensayado";
        /// <summary>Resultado incierto ya comprobado y registrado. Terminal: nunca vuelve a listo.</summary>
        public const string Reconciliado = "reconciliado";
    }

    public sealed class Plan : IJsonWritable
    {
        public string Id;
        public string SessionId;
        public string Client;
        public string DocKey;
        public string DocTitle;
        /// <summary>Handle dN del documento en este Revit (commit aplica sobre él aunque el usuario cambie de pestaña).</summary>
        public string DocId;
        /// <summary>Tarea autorizada (tarea=…): otra conexión de la misma tarea puede hacer commit tras reconectar.</summary>
        public string Task;
        /// <summary>Clave de idempotencia del commit (clave=…).</summary>
        public string IdemKey;
        /// <summary>AUD-19: vínculo con la fuente del lote (testigo, clave y huella derivadas del contenido). null si el plan no viene de una fuente.
        /// El commit usa esta clave y esta huella en el diario de operaciones aunque el agente no pase clave=.</summary>
        public string BoundToken, BoundKey, BoundHash;
        public string Summary;          // texto corto que la IA da (o se genera) para mostrar en la cola
        public DateTime CreatedUtc;
        public string Status = PlanStatus.Listo;
        public string StatusDetail;
        public DateTime? HeldUtc;
        public DateTime? FinishedUtc;
        public List<PlanEntry> Entries = new List<PlanEntry>();
        public List<string> Warnings = new List<string>();
        public int AppliedBatches;
        public int TotalBatches;
        /// <summary>Ids creados al aplicar (en orden). Se guardan como $creados en la sesión.</summary>
        public List<long> Created = new List<long>();
        /// <summary>Creados por cada operación (n desde 1): $creados_n en la sesión.</summary>
        public Dictionary<int, List<long>> CreatedByOp = new Dictionary<int, List<long>>();
        /// <summary>Relectura tras aplicar: cuántos quedaron como se planificó y cuáles no.</summary>
        public Dictionary<string, object> Verification;
        public Dictionary<string, object> Reconciliation;
        /// <summary>Último fallo de almacenamiento de este plan. Null si su estado está en disco.</summary>
        public string StorageWarning;
        /// <summary>Copia leída de disco: la verdad del bloqueo sigue estando en el archivo (puede ser la intención viva de otro proceso).</summary>
        internal bool FromDisk;

        public bool IsFinal => Status == PlanStatus.Aplicado || Status == PlanStatus.Conflicto || Status == PlanStatus.Revertido
                               || Status == PlanStatus.Descartado || Status == PlanStatus.Vencido || Status == PlanStatus.Reconciliado;

        public int ElementCount => Entries.Where(e => e.Id != 0).Select(e => e.Id).Distinct().Count();
        public int CreationCount => Entries.Count(e => e.IsCreation);

        public object ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["session"] = SessionId, ["client"] = Client, ["doc_key"] = DocKey, ["doc_title"] = DocTitle, ["doc_id"] = DocId, ["tarea"] = Task,
            ["summary"] = Summary, ["created_utc"] = CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
            ["status"] = Status, ["status_detail"] = StatusDetail,
            ["held_utc"] = HeldUtc?.ToString("o", CultureInfo.InvariantCulture),
            ["finished_utc"] = FinishedUtc?.ToString("o", CultureInfo.InvariantCulture),
            ["entries"] = Entries.Cast<object>().ToList(), ["warnings"] = Warnings.Cast<object>().ToList(),
            ["created"] = Created.Cast<object>().ToList(),
            ["created_by_op"] = CreatedByOp.ToDictionary(k => k.Key.ToString(CultureInfo.InvariantCulture), k => (object)k.Value.Cast<object>().ToList()),
            ["applied_batches"] = (long)AppliedBatches, ["total_batches"] = (long)TotalBatches,
            ["reconciliation"] = Reconciliation,
            ["origen"] = BoundToken, ["origen_clave"] = BoundKey, ["origen_huella"] = BoundHash,
        };

        public static Plan FromJson(string json)
        {
            var o = Json.ParseObject(json);
            var p = new Plan
            {
                Id = Arg.GetString(o, "id"),
                SessionId = Arg.GetString(o, "session"),
                Client = Arg.GetString(o, "client"),
                DocKey = Arg.GetString(o, "doc_key"),
                DocTitle = Arg.GetString(o, "doc_title"),
                DocId = Arg.GetString(o, "doc_id"),
                Task = Arg.GetString(o, "tarea"),
                Summary = Arg.GetString(o, "summary"),
                CreatedUtc = DateTime.Parse(Arg.GetString(o, "created_utc") ?? DateTime.UtcNow.ToString("o"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Status = Arg.GetString(o, "status") ?? PlanStatus.Retenido,
                StatusDetail = Arg.GetString(o, "status_detail"),
            };
            var held = Arg.GetString(o, "held_utc");
            if (held != null) p.HeldUtc = DateTime.Parse(held, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (o.TryGetValue("entries", out var e) && e is List<object> list)
                foreach (var x in list.OfType<Dictionary<string, object>>()) p.Entries.Add(PlanEntry.FromJson(x));
            if (o.TryGetValue("warnings", out var w) && w is List<object> wl)
                foreach (var x in wl) p.Warnings.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
            if (o.TryGetValue("created", out var c) && c is List<object> cl)
                foreach (var x in cl) if (x is long l) p.Created.Add(l);
            if (o.TryGetValue("created_by_op", out var cbo) && cbo is Dictionary<string, object> cbod)
                foreach (var kv in cbod)
                    if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var opIndex) && kv.Value is List<object> ids)
                        p.CreatedByOp[opIndex] = ids.OfType<long>().ToList();
            var finished = Arg.GetString(o, "finished_utc");
            if (finished != null) p.FinishedUtc = DateTime.Parse(finished, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            p.AppliedBatches = (int)(Arg.GetLong(o, "applied_batches") ?? 0);
            p.TotalBatches = (int)(Arg.GetLong(o, "total_batches") ?? 0);
            if (o.TryGetValue("reconciliation", out var rec) && rec is Dictionary<string, object> recd) p.Reconciliation = recd;
            p.BoundToken = Arg.GetString(o, "origen"); p.BoundKey = Arg.GetString(o, "origen_clave"); p.BoundHash = Arg.GetString(o, "origen_huella");
            return p;
        }

        /// <summary>Diff compacto para la IA y para la cola: encabezado una vez y hasta N filas.</summary>
        public Dictionary<string, object> Describe(int maxRows = 30)
        {
            var d = new Dictionary<string, object>
            {
                ["plan_id"] = Id,
                ["estado_plan"] = Status,
                ["elementos"] = (long)ElementCount,
                ["cambios"] = (long)Entries.Count,
                ["por_op"] = Entries.GroupBy(x => x.Op).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["cols"] = new List<object> { "id", "op", "campo", "antes", "despues" },
                ["filas"] = Entries.Take(maxRows).Select(x => (object)new object[]
                {
                    x.Ref > 0 ? "@" + x.Ref : x.Id == 0 ? (object)"nuevo" : x.Id, x.Op, x.Key,
                    ResultTable.Trim(ShowValue(x, x.Before, true)), ResultTable.Trim(x.Show ?? ShowValue(x, x.After, false)),
                }).ToList(),
            };
            if (BoundKey != null) d["clave_vinculada"] = BoundKey;   // AUD-19: el commit la usa aunque no se pase clave=
            var nuevos = CreationCount;
            if (nuevos > 0) d["nuevos"] = (long)nuevos;
            if (Entries.Count > maxRows) d["filas_omitidas"] = (long)(Entries.Count - maxRows);
            if (Warnings.Count > 0) d["avisos"] = Warnings.Cast<object>().ToList();
            if (!string.IsNullOrEmpty(StatusDetail)) d["detalle"] = StatusDetail;
            if (Created.Count > 0)
            {
                d["creados"] = (long)Created.Count;
                d["ids_creados"] = Created.Take(50).Cast<object>().ToList();
            }
            if (Verification != null) d["verificacion"] = Verification;
            if (Reconciliation != null) d["reconciliacion"] = Reconciliation;
            if (StorageWarning != null) d["almacenamiento"] = StorageWarning;
            return d;
        }

        /// <summary>Los "antes" de las creaciones son guardas técnicas (qué debe seguir existiendo): se muestran como "—".</summary>
        public static string ShowValue(PlanEntry e, string v, bool before)
        {
            if (v == null) return "";
            if (before && e.Id == 0) return "—";
            if (v.StartsWith("\u0000", StringComparison.Ordinal)) return v.Substring(1);
            if (e.Key == "exists") return v == "1" ? "existe" : "borrado";
            if ((e.Key == "elev" || e.Key == "offset") && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ft))
                return Lengths.FeetInches16(ft) + " (" + Math.Round(ft, 4).ToString(CultureInfo.InvariantCulture) + " ft)";
            return v;
        }

        /// <summary>Entradas cuyo valor actual ya no es el que se vio al planificar (alguien lo cambió).</summary>
        public List<PlanEntry> FindConflicts(Func<PlanEntry, string> currentValue)
        {
            var list = new List<PlanEntry>();
            foreach (var e in Entries)
            {
                string now;
                try { now = currentValue(e); }
                catch (Exception) { now = "\u0000error"; }
                if (!PlanEntry.Same(now, e.Before)) list.Add(e);
            }
            return list;
        }
    }

    /// <summary>
    /// Planes en memoria + cola de retenidos en disco (%LOCALAPPDATA%\PDI.RevitMcp\cola\&lt;doc&gt;\P-xxxx.json).
    /// La cola en disco sobrevive a un cierre o crash de Revit; al aplicar se vuelve a validar cada valor.
    /// </summary>
    public sealed class PlanStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Plan> _plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly string _queueRoot;
        private readonly Func<DateTime> _now;
        private int _counter;
        /// <summary>Operaciones de disco; reemplazables para probar fallos de almacenamiento.</summary>
        public Action<string, string> WriteFile = Home.WriteAtomic;
        public Action<string> RemoveFile = path => { if (File.Exists(path)) File.Delete(path); };
        public TimeSpan LockWait = TimeSpan.FromSeconds(5);

        public PlanStore(string queueRoot, Func<DateTime> now = null)
        {
            _queueRoot = queueRoot;
            _now = now ?? (() => DateTime.UtcNow);
        }

        public static string DefaultQueueRoot
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("PDI_REVITMCP_HOME");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDI.RevitMcp");
                return Path.Combine(root, "cola");
            }
        }

        public string NewId()
        {
            lock (_lock)
            {
                _counter++;
                return "P-" + _now().ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + _counter.ToString("D3", CultureInfo.InvariantCulture);
            }
        }

        public void Add(Plan p) { lock (_lock) _plans[p.Id] = p; }

        public Plan Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock) return _plans.TryGetValue(id.Trim(), out var p) ? p : null;
        }

        /// <summary>Pasa a "vencido" los planes listos más viejos que el límite. Los retenidos no vencen.</summary>
        public int ExpireOld(TimeSpan maxAge)
        {
            var n = 0;
            lock (_lock)
                foreach (var p in _plans.Values)
                    if (p.Status == PlanStatus.Listo && _now() - p.CreatedUtc > maxAge)
                    {
                        p.Status = PlanStatus.Vencido;
                        p.StatusDetail = "pasaron más de " + (int)maxAge.TotalMinutes + " min; vuelva a planificar";
                        n++;
                    }
            return n;
        }

        public void Hold(Plan p)
        {
            lock (_lock)
            {
                p.Status = PlanStatus.Retenido;
                p.HeldUtc = _now();
                p.StatusDetail = "en la cola de Revit; el usuario lo aplicará con 'Aplicar cola'";
                _plans[p.Id] = p;
            }
            Persist(p);
        }

        /// <summary>Retenidos de un documento, en orden de llegada.</summary>
        public List<Plan> Held(string docKey)
        {
            lock (_lock)
                return _plans.Values
                    .Where(p => p.Status == PlanStatus.Retenido && string.Equals(p.DocKey, docKey, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.HeldUtc ?? p.CreatedUtc).ThenBy(p => p.Id, StringComparer.Ordinal)
                    .ToList();
        }

        /// <summary>
        /// Cierra un plan. Devuelve false si su estado no quedó en disco: el fallo se anota en el plan y el documento sigue bloqueado.
        /// Un resultado desconocido siempre conserva evidencia, aunque quien llama no la haya preparado.
        /// </summary>
        public bool Finish(Plan p, string status, string detail)
        {
            lock (_lock)
            {
                p.Status = status;
                p.StatusDetail = detail;
                p.FinishedUtc = _now();
                p.StorageWarning = null;
                p.FromDisk = false;
                if (status == Status.Desconocido) GroupAbort.PreserveInterrupted(p, detail);
            }
            // La evidencia de un resultado incierto debe sobrevivir a un cierre de Revit: nunca se borra al terminar.
            try
            {
                if (p.Reconciliation == null) DeleteFile(p);
                else Persist(p);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lock (_lock)
                    p.StorageWarning = (p.Reconciliation == null
                        ? "no se pudo retirar la intención de escritura del disco (" + ex.Message + "); el documento sigue bloqueado hasta reconciliar."
                        : "no se pudo guardar la evidencia en disco (" + ex.Message + "); no es durable y el documento sigue bloqueado.");
                return false;
            }
        }

        /// <summary>Planes con evidencia pendiente de reconciliar en un documento.</summary>
        public List<Plan> Unreconciled(string docKey)
        {
            lock (_lock)
                return _plans.Values
                    .Where(p => p.Reconciliation != null && p.Status != PlanStatus.Reconciliado && string.Equals(p.DocKey, docKey, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.FinishedUtc ?? p.CreatedUtc).ThenBy(p => p.Id, StringComparer.Ordinal)
                    .ToList();
        }

        /// <summary>
        /// ¿Se puede escribir? Lee siempre el disco, así que ve lo que dejó otro proceso o un cierre de Revit.
        /// También cuenta la evidencia que sólo está en la memoria de este proceso. Una duda de lectura bloquea.
        /// </summary>
        public DocWriteBlock CheckWritable(string docKey)
        {
            var block = new DocWriteBlock { DocKey = docKey };
            lock (_lock)
                foreach (var p in _plans.Values)
                    if (!p.FromDisk && p.Reconciliation != null && p.Status != PlanStatus.Reconciliado && string.Equals(p.DocKey, docKey, StringComparison.OrdinalIgnoreCase))
                        block.Plans.Add(p.Id);
            try
            {
                var dir = DocDir(docKey);
                if (!Directory.Exists(dir)) return block;
                foreach (var f in Directory.GetFiles(dir, "P-*.json"))
                {
                    Plan p;
                    try { p = Plan.FromJson(ReadShared(f)); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { block.Errors.Add(Path.GetFileName(f) + ": " + ex.Message); continue; }
                    catch (Exception) { block.CorruptFiles.Add(Path.GetFileName(f)); continue; }
                    if (p.Status == PlanStatus.Reconciliado || p.Id == null) { if (p.Id == null) block.CorruptFiles.Add(Path.GetFileName(f)); continue; }
                    if ((p.Reconciliation != null || p.Status == PlanStatus.EnProceso || p.Status == Status.Desconocido) && !block.Plans.Contains(p.Id)) block.Plans.Add(p.Id);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { block.Errors.Add(ex.Message); }
            return block;
        }

        /// <summary>
        /// Última barrera antes de tocar el modelo: comprueba el documento y deja en disco la intención de escritura, bajo un bloqueo entre
        /// procesos. Si Revit muere después, el archivo mantiene el documento bloqueado. Lanza sin haber cambiado nada si no puede garantizarlo.
        /// </summary>
        public void BeginWrite(Plan p)
        {
            WithDocLock(p.DocKey, () =>
            {
                var block = CheckWritable(p.DocKey);
                if (block.Blocked) throw new DocWriteBlockedException(block);
                WriteSnapshot(p, "intención de escritura sin resultado registrado: el proceso pudo terminar durante la operación");
            });
        }

        /// <summary>Punto de control tras cada lote: conserva los ids creados. Lanza PlanStoreException si no quedó en disco.</summary>
        public void Checkpoint(Plan p) => WriteSnapshot(p, "interrumpido tras el lote " + p.AppliedBatches + " de " + p.TotalBatches);

        private void WriteSnapshot(Plan p, string reason)
        {
            string text;
            lock (_lock)
            {
                var status = p.Status;
                var evidence = p.Reconciliation;
                try
                {
                    p.Status = PlanStatus.EnProceso;
                    p.Reconciliation = null;
                    GroupAbort.PreserveInterrupted(p, reason);
                    text = Json.Write(p);
                }
                finally { p.Status = status; p.Reconciliation = evidence; }
            }
            try { WriteFile(Path.Combine(DocDir(p.DocKey), p.Id + ".json"), text); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new PlanStoreException("no se pudo guardar la intención de escritura del plan " + p.Id + ": " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Libera un plan sólo con un veredicto verificado. Primero archiva evidencia y veredicto en reconciliados\ y después retira el archivo
        /// activo: si algo falla, el documento sigue bloqueado. Un veredicto no verificable queda anotado y no libera nada.
        /// </summary>
        public bool Reconcile(Plan p, ReconcileCheck check, string author)
        {
            var released = false;
            WithDocLock(p.DocKey, () =>
            {
                var record = new Dictionary<string, object>
                {
                    ["utc"] = _now().ToString("o", CultureInfo.InvariantCulture), ["autor"] = author, ["resultado"] = check.ToJson(),
                };
                if (!check.Verified && check.Verdict != ReconcileCheck.ManualHuman)
                {
                    lock (_lock)
                    {
                        if (p.Reconciliation == null) GroupAbort.PreserveInterrupted(p, "sin evidencia previa");
                        if (!(p.Reconciliation.TryGetValue("intentos", out var a) && a is List<object> attempts)) p.Reconciliation["intentos"] = attempts = new List<object>();
                        attempts.Add(record);
                    }
                    try { Persist(p); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { lock (_lock) p.StorageWarning = "no se pudo anotar el intento: " + ex.Message; }
                    return;
                }
                var archive = new Dictionary<string, object> { ["registro"] = record, ["plan"] = Json.Parse(Json.Write(p)) };
                var stamp = _now().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                try
                {
                    WriteFile(Path.Combine(DocDir(p.DocKey), "reconciliados", p.Id + "-" + stamp + ".json"), Json.Write(archive));
                    DeleteFile(p);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new PlanStoreException("no se pudo registrar la reconciliación de " + p.Id + "; el documento sigue bloqueado: " + ex.Message, ex);
                }
                lock (_lock)
                {
                    p.Status = PlanStatus.Reconciliado;
                    p.StatusDetail = check.Verdict + " por " + author;
                    p.FinishedUtc = _now();
                    p.StorageWarning = null;
                    p.FromDisk = false;
                    _plans[p.Id] = p;
                }
                released = true;
            });
            return released;
        }

        /// <summary>Aparta un archivo de evidencia dañado a cuarentena\ con un registro humano. No lo borra. Sólo para la vía manual con Revit cerrado.</summary>
        public void QuarantineCorrupt(string docDir, string fileName, string author, string justification)
        {
            if (string.IsNullOrWhiteSpace(justification)) throw new ArgumentException("falta la justificación");
            if (fileName != Path.GetFileName(fileName) || !fileName.StartsWith("P-", StringComparison.Ordinal)) throw new ArgumentException("archivo no válido: " + fileName);
            var src = Path.Combine(docDir, fileName);
            var dest = Path.Combine(docDir, "cuarentena");
            Directory.CreateDirectory(dest);
            var stamp = _now().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            WriteFile(Path.Combine(dest, fileName + "." + stamp + ".registro.json"), Json.Write(new Dictionary<string, object>
            {
                ["utc"] = _now().ToString("o", CultureInfo.InvariantCulture), ["autor"] = author, ["justificacion"] = justification, ["archivo"] = fileName,
                ["sha256"] = Home.FileSha256(src),
            }));
            File.Move(src, Path.Combine(dest, fileName + "." + stamp));
        }

        /// <summary>Bloqueo entre procesos por documento. Si no se obtiene, no se escribe.</summary>
        private void WithDocLock(string docKey, Action body)
        {
            FileStream handle = null;
            try
            {
                var dir = DocDir(docKey);
                Directory.CreateDirectory(dir);
                var limit = DateTime.UtcNow + LockWait;
                while (handle == null)
                {
                    try { handle = new FileStream(Path.Combine(dir, ".bloqueo"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) when (DateTime.UtcNow < limit) { System.Threading.Thread.Sleep(25); }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new PlanStoreException("no se pudo obtener el bloqueo del documento: " + ex.Message, ex);
            }
            using (handle) body();
        }

        private static string ReadShared(string path)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(f, Encoding.UTF8)) return r.ReadToEnd();
                }
                catch (IOException) when (attempt < 4) { System.Threading.Thread.Sleep(20 * attempt); }
            }
        }

        /// <summary>
        /// Último valor que dejará la cola para (elemento, campo), si algún plan retenido lo toca.
        /// Así un plan nuevo se encadena sobre los retenidos en vez de chocar con ellos al aplicar.
        /// </summary>
        public bool TryHeldAfter(string docKey, long id, string key, out string after, out string planId)
        {
            after = null; planId = null;
            foreach (var p in Held(docKey))
                foreach (var e in p.Entries)
                    if (e.Id == id && string.Equals(e.Key, key, StringComparison.Ordinal))
                    {
                        after = e.After;
                        planId = p.Id;
                    }
            return planId != null;
        }

        /// <summary>Planes de una sesión, los más recientes primero.</summary>
        public List<Plan> ForSession(string sessionId, int max = 20)
        {
            lock (_lock)
                return _plans.Values.Where(p => p.SessionId == sessionId)
                    .OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id, StringComparer.Ordinal)
                    .Take(max).ToList();
        }

        /// <summary>Descarta todos los retenidos de un documento. Devuelve cuántos.</summary>
        public int DiscardHeld(string docKey, string detail)
        {
            var list = Held(docKey);
            foreach (var p in list) Finish(p, PlanStatus.Descartado, detail);
            return list.Count;
        }

        /// <summary>Carga los retenidos guardados en disco de un documento (por ejemplo, tras reiniciar Revit).</summary>
        public int LoadPersisted(string docKey)
        {
            var dir = DocDir(docKey);
            if (!Directory.Exists(dir)) return 0;
            var n = 0;
            foreach (var f in Directory.GetFiles(dir, "P-*.json"))
            {
                try
                {
                    var p = Plan.FromJson(ReadShared(f));
                    if (p.Status != PlanStatus.Retenido && p.Status != PlanStatus.EnProceso && p.Reconciliation == null) continue;
                    p.FromDisk = true;
                    // Una intención sin resultado nunca se convierte en éxito ni vuelve a listo.
                    if (p.Status == PlanStatus.EnProceso)
                    {
                        p.Status = Status.Desconocido;
                        p.StatusDetail = "la operación empezó y no registró resultado; reconcilie antes de escribir";
                        GroupAbort.PreserveInterrupted(p, p.StatusDetail);
                    }
                    lock (_lock)
                    {
                        if (_plans.ContainsKey(p.Id)) continue;
                        _plans[p.Id] = p;
                    }
                    n++;
                }
                catch (Exception) { /* archivo dañado: se conserva y CheckWritable bloquea el documento */ }
            }
            return n;
        }

        private void Persist(Plan p)
        {
            var dir = DocDir(p.DocKey);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, p.Id + ".json");
            WriteFile(file, Json.Write(p));
        }

        private void DeleteFile(Plan p)
        {
            var file = Path.Combine(DocDir(p.DocKey), p.Id + ".json");
            RemoveFile(file);
        }

        /// <summary>Carpeta por documento: hash corto de la ruta (las rutas de nube tienen caracteres no válidos).</summary>
        public string DocDir(string docKey)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes((docKey ?? "").ToLowerInvariant()));
                var hex = BitConverter.ToString(h, 0, 6).Replace("-", "").ToLowerInvariant();
                return Path.Combine(_queueRoot, hex);
            }
        }
    }
}
