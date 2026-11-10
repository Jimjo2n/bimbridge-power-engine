// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Qc
{
    /// <summary>
    /// QC contra un manifiesto de referencia (§7.4). Cruza lo esperado (de los planos: posición, tipo permitido, nivel, grupo, etiqueta,
    /// montaje, fuente) con lo que hay en el modelo, UNO A UNO:
    /// - un elemento del modelo sólo puede justificar una referencia (nunca se cuentan totales);
    /// - proximidad sola no es identidad: si dos candidatos quedan casi igual de cerca, la referencia queda "ambigua" y NO se aprueba;
    /// - faltantes, sobrantes y casi duplicados (dos elementos del mismo tipo casi en el mismo sitio) se informan siempre;
    /// - separa la cobertura de interpretación del plano (campos no confirmados en el manifiesto) de la comprobación del modelo.
    /// Unidades: mm y grados. Sin API de Revit: se prueba sin Revit y el add-in sólo junta los datos.
    /// </summary>
    public static class ManifestQc
    {
        public sealed class Expected
        {
            public string Ref;
            public double X, Y;
            public double? Z;
            public double? RotDeg;
            public string Level;
            /// <summary>Tipos permitidos ("Familia: Tipo" o sólo "Tipo"); vacío = cualquiera.</summary>
            public List<string> Types = new List<string>();
            /// <summary>true = exactamente una etiqueta; false = ninguna; null = no se comprueba.</summary>
            public bool? Tag;
            public string Group;
            public string Mounting;
            public string Source;
            /// <summary>Campos que la lectura del plano no pudo confirmar (p. ej. "altura"): no se aprueban como comprobados.</summary>
            public List<string> Unconfirmed = new List<string>();
        }

        public sealed class Actual
        {
            public long Id;
            public string Uid;
            public double X, Y, Z;
            public double? RotDeg;
            public string Level;
            public string Family;
            public string Type;
            public int Tags;
            public string Group;
            public string Mounting;
            public string FullType => string.IsNullOrEmpty(Family) ? Type : Family + ": " + Type;
        }

        public sealed class Options
        {
            public double TolXY = 150;
            public double TolZ = 25;
            public double TolRot = 2;
            /// <summary>Simetría del símbolo (180 = una regleta igual girada 180°; 90 = cuadrada; 0 = sin simetría).</summary>
            public double Symmetry = 0;
            /// <summary>Si el segundo candidato está a menos de este margen (mm) del primero, la referencia es ambigua.</summary>
            public double AmbiguityMargin = 50;
            public double NearDuplicate = 100;
            public bool SameLevel = true;
        }

        public sealed class Finding
        {
            public string Code;
            public string Ref;
            public long? Id;
            public string Detail;
            public object[] Row() => new object[] { Code, Ref ?? "", Id.HasValue ? (object)Id.Value : "", Detail ?? "" };
        }

        public sealed class Result
        {
            public int Expected, Model, Approved, WithDeviation, Ambiguous, Missing, Extra, NearDuplicates, Unconfirmed;
            public readonly List<Finding> Findings = new List<Finding>();
            /// <summary>ref → id del modelo emparejado (sólo emparejamientos únicos, aprobados o con desviación).</summary>
            public readonly Dictionary<string, long> Pairs = new Dictionary<string, long>(StringComparer.Ordinal);
            public bool Passed => Approved == Expected && Extra == 0 && NearDuplicates == 0 && Ambiguous == 0 && Missing == 0;

            public Dictionary<string, object> ToJson(int maxFindings = 50) => new Dictionary<string, object>
            {
                ["aprobado"] = Passed,
                ["esperados"] = (long)Expected, ["modelo"] = (long)Model, ["correctos"] = (long)Approved, ["con_desviacion"] = (long)WithDeviation,
                ["ambiguos"] = (long)Ambiguous, ["faltantes"] = (long)Missing, ["sobrantes"] = (long)Extra, ["casi_duplicados"] = (long)NearDuplicates,
                ["campos_no_confirmados"] = (long)Unconfirmed,
                ["por_codigo"] = Findings.GroupBy(f => f.Code).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["cols"] = new List<object> { "codigo", "ref", "id", "detalle" },
                ["rows"] = Findings.Take(maxFindings).Select(f => (object)f.Row().ToList()).ToList(),
                ["rows_total"] = (long)Findings.Count,
                ["desviaciones"] = Findings.Where(f => f.Code != "no_confirmado").Take(maxFindings).Select(f => (object)(f.Code + " " + (f.Ref ?? "") + (f.Id.HasValue ? " #" + f.Id : "") + (f.Detail != null ? ": " + f.Detail : ""))).ToList(),
            };
        }

        private static double D2(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        /// <summary>Diferencia de giro en [0, 180], reducida por la simetría del símbolo (180 = igual girado media vuelta).</summary>
        public static double RotDiff(double a, double b, double symmetry)
        {
            var d = Math.Abs(((a - b) % 360 + 540) % 360 - 180);
            if (symmetry > 0)
            {
                d %= symmetry;
                d = Math.Min(d, symmetry - d);
            }
            return d;
        }

        private static bool TypeAllowed(Expected e, Actual a)
        {
            if (e.Types == null || e.Types.Count == 0) return true;
            return e.Types.Any(t => string.Equals(t, a.FullType, StringComparison.OrdinalIgnoreCase) || string.Equals(t, a.Type, StringComparison.OrdinalIgnoreCase));
        }

        private static bool LevelOk(Expected e, Actual a) => string.IsNullOrEmpty(e.Level) || string.Equals(e.Level, a.Level, StringComparison.OrdinalIgnoreCase);

        public static Result Run(IList<Expected> expected, IList<Actual> model, Options o = null)
        {
            o = o ?? new Options();
            var r = new Result { Expected = expected.Count, Model = model.Count };

            // 1) Candidatos dentro de tolerancia en planta (y mismo nivel si se exige).
            var pairs = new List<Tuple<int, int, double>>();
            var candE = new Dictionary<int, List<Tuple<int, double>>>();
            var candA = new Dictionary<int, List<Tuple<int, double>>>();
            for (var i = 0; i < expected.Count; i++)
                for (var j = 0; j < model.Count; j++)
                {
                    if (o.SameLevel && !LevelOk(expected[i], model[j])) continue;
                    var d = D2(expected[i].X, expected[i].Y, model[j].X, model[j].Y);
                    if (d > o.TolXY) continue;
                    pairs.Add(Tuple.Create(i, j, d));
                    (candE.TryGetValue(i, out var le) ? le : candE[i] = new List<Tuple<int, double>>()).Add(Tuple.Create(j, d));
                    (candA.TryGetValue(j, out var la) ? la : candA[j] = new List<Tuple<int, double>>()).Add(Tuple.Create(i, d));
                }

            // 2) Emparejamiento uno a uno por distancia creciente (cada elemento justifica una sola referencia).
            var eTaken = new int?[expected.Count];
            var aTaken = new int?[model.Count];
            foreach (var p in pairs.OrderBy(p => p.Item3).ThenBy(p => p.Item1).ThenBy(p => p.Item2))
            {
                if (eTaken[p.Item1].HasValue || aTaken[p.Item2].HasValue) continue;
                eTaken[p.Item1] = p.Item2;
                aTaken[p.Item2] = p.Item1;
            }

            // 3) Cada referencia: ambigua, faltante, o emparejada con sus comprobaciones.
            for (var i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                foreach (var f in e.Unconfirmed ?? new List<string>())
                {
                    r.Unconfirmed++;
                    r.Findings.Add(new Finding { Code = "no_confirmado", Ref = e.Ref, Detail = f + " sin confirmar en la fuente" + (e.Source != null ? " (" + e.Source + ")" : "") });
                }
                if (!eTaken[i].HasValue)
                {
                    r.Missing++;
                    var near = model.Select((a, j) => new { a, j, d = D2(e.X, e.Y, a.X, a.Y) }).OrderBy(x => x.d).FirstOrDefault();
                    r.Findings.Add(new Finding
                    {
                        Code = "faltante", Ref = e.Ref,
                        Detail = "no hay elemento a ≤" + F(o.TolXY) + " mm" + (o.SameLevel && e.Level != null ? " en " + e.Level : "") +
                                 (near != null ? "; el más cercano es #" + near.a.Id + " a " + F(near.d) + " mm" + (aTaken[near.j].HasValue ? " (ya asignado a " + expected[aTaken[near.j].Value].Ref + ")" : "") : ""),
                    });
                    continue;
                }
                var j0 = eTaken[i].Value;
                var a0 = model[j0];
                var d0 = D2(e.X, e.Y, a0.X, a0.Y);
                // ¿Otro candidato casi igual de cerca (para esta referencia), u otra referencia casi igual de cerca de este elemento?
                // Un rival sólo cuenta si no tiene claramente otra pareja mejor (si la tiene, no hay duda).
                double MatchDistE(int ei) => eTaken[ei].HasValue ? D2(expected[ei].X, expected[ei].Y, model[eTaken[ei].Value].X, model[eTaken[ei].Value].Y) : double.MaxValue;
                double MatchDistA(int aj) => aTaken[aj].HasValue ? D2(expected[aTaken[aj].Value].X, expected[aTaken[aj].Value].Y, model[aj].X, model[aj].Y) : double.MaxValue;
                var rivalA = candE[i].Where(c => c.Item1 != j0 && c.Item2 - d0 < o.AmbiguityMargin && !(MatchDistA(c.Item1) + o.AmbiguityMargin <= c.Item2)).ToList();
                var rivalE = candA[j0].Where(c => c.Item1 != i && Math.Abs(c.Item2 - d0) < o.AmbiguityMargin && !(MatchDistE(c.Item1) + o.AmbiguityMargin <= c.Item2)).ToList();
                if (rivalA.Count > 0 || rivalE.Count > 0)
                {
                    r.Ambiguous++;
                    r.Findings.Add(new Finding
                    {
                        Code = "ambiguo", Ref = e.Ref, Id = a0.Id,
                        Detail = rivalA.Count > 0
                            ? "candidatos #" + a0.Id + " (" + F(d0) + " mm) y " + string.Join(", ", rivalA.Select(c => "#" + model[c.Item1].Id + " (" + F(c.Item2) + " mm)")) + ": la cercanía no prueba cuál es"
                            : "#" + a0.Id + " está casi igual de cerca de " + string.Join(", ", rivalE.Select(c => expected[c.Item1].Ref)) + ": no se aprueba por proximidad",
                    });
                    continue;
                }
                var devs = new List<Finding>();
                if (d0 > o.TolXY * 0.5) devs.Add(new Finding { Code = "posicion", Ref = e.Ref, Id = a0.Id, Detail = "desplazada " + F(d0) + " mm (tolerancia " + F(o.TolXY) + ")" });
                if (e.Z.HasValue && Math.Abs(a0.Z - e.Z.Value) > o.TolZ)
                    devs.Add(new Finding { Code = "altura", Ref = e.Ref, Id = a0.Id, Detail = "z " + F(a0.Z) + " mm, esperada " + F(e.Z.Value) + " (±" + F(o.TolZ) + ")" });
                if (e.RotDeg.HasValue && a0.RotDeg.HasValue && RotDiff(a0.RotDeg.Value, e.RotDeg.Value, o.Symmetry) > o.TolRot)
                    devs.Add(new Finding { Code = "orientacion", Ref = e.Ref, Id = a0.Id, Detail = "giro " + F(a0.RotDeg.Value) + "°, esperado " + F(e.RotDeg.Value) + "°" });
                if (!TypeAllowed(e, a0))
                    devs.Add(new Finding { Code = "tipo", Ref = e.Ref, Id = a0.Id, Detail = a0.FullType + " no está entre " + string.Join(" | ", e.Types) });
                if (!o.SameLevel && !LevelOk(e, a0))
                    devs.Add(new Finding { Code = "nivel", Ref = e.Ref, Id = a0.Id, Detail = a0.Level + ", esperado " + e.Level });
                if (e.Tag == true && a0.Tags == 0) devs.Add(new Finding { Code = "sin_etiqueta", Ref = e.Ref, Id = a0.Id });
                if (e.Tag == true && a0.Tags > 1) devs.Add(new Finding { Code = "etiqueta_duplicada", Ref = e.Ref, Id = a0.Id, Detail = a0.Tags + " etiquetas" });
                if (e.Tag == false && a0.Tags > 0) devs.Add(new Finding { Code = "etiqueta_sobra", Ref = e.Ref, Id = a0.Id, Detail = a0.Tags + " etiquetas y la regla dice ninguna" });
                if (!string.IsNullOrEmpty(e.Group) && !string.Equals(e.Group, a0.Group, StringComparison.OrdinalIgnoreCase))
                    devs.Add(new Finding { Code = "grupo", Ref = e.Ref, Id = a0.Id, Detail = "en " + (a0.Group ?? "(sin grupo)") + ", esperado " + e.Group });
                if (!string.IsNullOrEmpty(e.Mounting) && !string.IsNullOrEmpty(a0.Mounting) && !string.Equals(e.Mounting, a0.Mounting, StringComparison.OrdinalIgnoreCase))
                    devs.Add(new Finding { Code = "montaje", Ref = e.Ref, Id = a0.Id, Detail = a0.Mounting + ", esperado " + e.Mounting });
                r.Pairs[e.Ref] = a0.Id;
                if (devs.Count == 0) r.Approved++;
                else { r.WithDeviation++; r.Findings.AddRange(devs); }
            }

            // 4) Sobrantes: elementos del alcance que no justifican ninguna referencia.
            for (var j = 0; j < model.Count; j++)
            {
                if (aTaken[j].HasValue) continue;
                r.Extra++;
                var a = model[j];
                var near = expected.Select((e, i) => new { e, d = D2(e.X, e.Y, a.X, a.Y) }).OrderBy(x => x.d).FirstOrDefault();
                r.Findings.Add(new Finding
                {
                    Code = "sobrante", Id = a.Id,
                    Detail = a.FullType + " en " + (a.Level ?? "?") + (near != null ? "; referencia más cercana " + near.e.Ref + " a " + F(near.d) + " mm" : ""),
                });
            }

            // 5) Casi duplicados: mismo tipo casi en el mismo sitio (dos luminarias apiladas), emparejadas o no.
            var grid = new Dictionary<Tuple<string, long, long>, List<int>>();
            var cell = Math.Max(1, o.NearDuplicate);
            for (var j = 0; j < model.Count; j++)
            {
                var k = Tuple.Create(model[j].Level ?? "", (long)Math.Floor(model[j].X / cell), (long)Math.Floor(model[j].Y / cell));
                (grid.TryGetValue(k, out var l) ? l : grid[k] = new List<int>()).Add(j);
            }
            var seen = new HashSet<Tuple<int, int>>();
            for (var j = 0; j < model.Count; j++)
            {
                var a = model[j];
                var cx = (long)Math.Floor(a.X / cell);
                var cy = (long)Math.Floor(a.Y / cell);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        if (!grid.TryGetValue(Tuple.Create(a.Level ?? "", cx + dx, cy + dy), out var l)) continue;
                        foreach (var k in l)
                        {
                            if (k <= j) continue;
                            var b = model[k];
                            if (!string.Equals(a.FullType, b.FullType, StringComparison.OrdinalIgnoreCase)) continue;
                            var d3 = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
                            if (d3 > o.NearDuplicate || !seen.Add(Tuple.Create(j, k))) continue;
                            r.NearDuplicates++;
                            r.Findings.Add(new Finding { Code = "casi_duplicado", Id = a.Id, Detail = "#" + a.Id + " y #" + b.Id + " (" + a.FullType + ") a " + F(d3) + " mm" });
                        }
                    }
            }
            return r;
        }

        private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        // ---------------- manifiesto desde JSON ----------------

        /// <summary>
        /// Lee un manifiesto: {"tolerancias":{xy,z,giro,simetria,ambiguedad,duplicado,mismo_nivel},"elementos":[{ref,x,y,z,giro,nivel,tipos,
        /// etiqueta,grupo,montaje,fuente,no_confirmado}]} (mm y grados).
        /// </summary>
        public static List<Expected> Parse(Dictionary<string, object> json, out Options options)
        {
            options = new Options();
            if (json.TryGetValue("tolerancias", out var tv) && tv is Dictionary<string, object> t)
            {
                options.TolXY = Num(t, "xy") ?? options.TolXY;
                options.TolZ = Num(t, "z") ?? options.TolZ;
                options.TolRot = Num(t, "giro") ?? options.TolRot;
                options.Symmetry = Num(t, "simetria") ?? options.Symmetry;
                options.AmbiguityMargin = Num(t, "ambiguedad") ?? options.AmbiguityMargin;
                options.NearDuplicate = Num(t, "duplicado") ?? options.NearDuplicate;
                options.SameLevel = Arg.GetBool(t, "mismo_nivel") ?? options.SameLevel;
            }
            var list = new List<Expected>();
            if (!(json.TryGetValue("elementos", out var ev) && ev is List<object> el)) throw new ArgumentException("el manifiesto necesita 'elementos': [...]");
            var n = 0;
            foreach (var o in el.OfType<Dictionary<string, object>>())
            {
                n++;
                var x = Num(o, "x");
                var y = Num(o, "y");
                if (!x.HasValue || !y.HasValue) throw new ArgumentException("el elemento " + n + " del manifiesto no tiene x/y (mm)");
                var tipos = o.TryGetValue("tipos", out var tp) && tp is List<object> tl ? tl.Select(s => Convert.ToString(s, CultureInfo.InvariantCulture)).ToList()
                    : Arg.GetString(o, "tipo") is string one ? new List<string> { one } : new List<string>();
                list.Add(new Expected
                {
                    Ref = Arg.GetString(o, "ref") ?? ("#" + n), X = x.Value, Y = y.Value, Z = Num(o, "z"), RotDeg = Num(o, "giro"),
                    Level = Arg.GetString(o, "nivel"), Types = tipos, Tag = Arg.GetBool(o, "etiqueta"), Group = Arg.GetString(o, "grupo"),
                    Mounting = Arg.GetString(o, "montaje"), Source = Arg.GetString(o, "fuente"), Unconfirmed = Arg.GetStringList(o, "no_confirmado"),
                });
            }
            var dup = list.GroupBy(e => e.Ref).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException("la referencia '" + dup.Key + "' aparece dos veces en el manifiesto");
            return list;
        }

        private static double? Num(IDictionary<string, object> o, string k)
        {
            if (!o.TryGetValue(k, out var v) || v == null) return null;
            if (v is double d) return d;
            if (v is long l) return l;
            if (v is int i) return i;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : (double?)null;
        }
    }

    /// <summary>
    /// Transformación afín 3D (rotación/espejo + traslación) para llevar coordenadas de un vínculo o de un grupo al anfitrión sin la API
    /// de Revit. Columnas BasisX, BasisY, BasisZ y Origin como en Autodesk.Revit.DB.Transform.
    /// </summary>
    public sealed class Xform
    {
        public double[] X = { 1, 0, 0 }, Y = { 0, 1, 0 }, Z = { 0, 0, 1 }, O = { 0, 0, 0 };

        public static Xform Identity => new Xform();

        public static Xform RotZ(double deg, double ox = 0, double oy = 0, double oz = 0)
        {
            var r = deg * Math.PI / 180;
            var c = Math.Cos(r);
            var s = Math.Sin(r);
            return new Xform { X = new[] { c, s, 0 }, Y = new[] { -s, c, 0 }, Z = new[] { 0.0, 0, 1 }, O = new[] { ox, oy, oz } };
        }

        public static Xform MirrorX(double ox = 0) => new Xform { X = new[] { -1.0, 0, 0 }, O = new[] { 2 * ox, 0.0, 0 } };

        public double[] Apply(double x, double y, double z) => new[]
        {
            O[0] + X[0] * x + Y[0] * y + Z[0] * z,
            O[1] + X[1] * x + Y[1] * y + Z[1] * z,
            O[2] + X[2] * x + Y[2] * y + Z[2] * z,
        };

        /// <summary>Composición: primero this y después <paramref name="after"/> (p. ej. instancia en el vínculo → vínculo en el anfitrión).</summary>
        public Xform Then(Xform after)
        {
            var nx = after.Dir(X);
            var ny = after.Dir(Y);
            var nz = after.Dir(Z);
            var no = after.Apply(O[0], O[1], O[2]);
            return new Xform { X = nx, Y = ny, Z = nz, O = no };
        }

        private double[] Dir(double[] v) => new[]
        {
            X[0] * v[0] + Y[0] * v[1] + Z[0] * v[2],
            X[1] * v[0] + Y[1] * v[1] + Z[1] * v[2],
            X[2] * v[0] + Y[2] * v[1] + Z[2] * v[2],
        };

        public double Determinant => X[0] * (Y[1] * Z[2] - Z[1] * Y[2]) - Y[0] * (X[1] * Z[2] - Z[1] * X[2]) + Z[0] * (X[1] * Y[2] - Y[1] * X[2]);

        /// <summary>Espejo = determinante negativo (la orientación de la familia se invierte).</summary>
        public bool Mirrored => Determinant < 0;

        /// <summary>Giro en planta del eje X transformado (grados, 0–360).</summary>
        public double RotationDeg => ((Math.Atan2(X[1], X[0]) * 180 / Math.PI) % 360 + 360) % 360;

        public Xform Inverse()
        {
            // Rígida (ortonormal, con o sin espejo): la inversa de la parte lineal es su transpuesta.
            var t = new Xform { X = new[] { X[0], Y[0], Z[0] }, Y = new[] { X[1], Y[1], Z[1] }, Z = new[] { X[2], Y[2], Z[2] } };
            var o = t.Dir(O);
            t.O = new[] { -o[0], -o[1], -o[2] };
            return t;
        }
    }
}
