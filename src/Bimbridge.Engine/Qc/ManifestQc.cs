// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Qc
{
    /// <summary>
    /// QC against a reference manifest (§7.4). Matches what is expected (from the drawings: position, allowed type, level, group, tag,
    /// mounting, source) with what is in the model, ONE TO ONE:
    /// - a model element can justify only one reference (totals are never counted);
    /// - proximity alone is not identity: if two candidates are almost equally close, the reference is "ambiguous" and is NOT approved;
    /// - missing, extra and near-duplicate elements (two elements of the same type almost in the same place) are always reported;
    /// - keeps the drawing-interpretation coverage (fields not confirmed in the manifest) separate from the model check.
    /// Units: mm and degrees. No Revit API: tested without Revit, and the add-in only gathers the data.
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
            /// <summary>Allowed types ("Family: Type" or just "Type"); empty = any.</summary>
            public List<string> Types = new List<string>();
            /// <summary>true = exactly one tag; false = none; null = not checked.</summary>
            public bool? Tag;
            public string Group;
            public string Mounting;
            public string Source;
            /// <summary>Fields the drawing reading could not confirm (e.g. "height"): not approved as checked.</summary>
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
            /// <summary>Symbol symmetry (180 = a strip light that looks the same rotated 180°; 90 = square; 0 = no symmetry).</summary>
            public double Symmetry = 0;
            /// <summary>If the second candidate is within this margin (mm) of the first, the reference is ambiguous.</summary>
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
            /// <summary>ref → matched model id (unique matches only, approved or with deviation).</summary>
            public readonly Dictionary<string, long> Pairs = new Dictionary<string, long>(StringComparer.Ordinal);
            public bool Passed => Approved == Expected && Extra == 0 && NearDuplicates == 0 && Ambiguous == 0 && Missing == 0;

            public Dictionary<string, object> ToJson(int maxFindings = 50) => new Dictionary<string, object>
            {
                ["approved"] = Passed,
                ["expected_items"] = (long)Expected, ["model"] = (long)Model, ["correct"] = (long)Approved, ["with_deviation"] = (long)WithDeviation,
                ["ambiguous_items"] = (long)Ambiguous, ["missing"] = (long)Missing, ["leftovers"] = (long)Extra, ["near_duplicates"] = (long)NearDuplicates,
                ["unconfirmed_fields"] = (long)Unconfirmed,
                ["by_code"] = Findings.GroupBy(f => f.Code).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["cols"] = new List<object> { "code", "ref", "id", "detail" },
                ["rows"] = Findings.Take(maxFindings).Select(f => (object)f.Row().ToList()).ToList(),
                ["rows_total"] = (long)Findings.Count,
                ["deviations"] = Findings.Where(f => f.Code != "not_committed").Take(maxFindings).Select(f => (object)(f.Code + " " + (f.Ref ?? "") + (f.Id.HasValue ? " #" + f.Id : "") + (f.Detail != null ? ": " + f.Detail : ""))).ToList(),
            };
        }

        private static double D2(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        /// <summary>Rotation difference in [0, 180], reduced by the symbol symmetry (180 = same when rotated half a turn).</summary>
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

            // 1) Candidates within tolerance in plan (and on the same level if required).
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

            // 2) One-to-one matching by increasing distance (each element justifies only one reference).
            var eTaken = new int?[expected.Count];
            var aTaken = new int?[model.Count];
            foreach (var p in pairs.OrderBy(p => p.Item3).ThenBy(p => p.Item1).ThenBy(p => p.Item2))
            {
                if (eTaken[p.Item1].HasValue || aTaken[p.Item2].HasValue) continue;
                eTaken[p.Item1] = p.Item2;
                aTaken[p.Item2] = p.Item1;
            }

            // 3) Each reference: ambiguous, missing, or matched with its checks.
            for (var i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                foreach (var f in e.Unconfirmed ?? new List<string>())
                {
                    r.Unconfirmed++;
                    r.Findings.Add(new Finding { Code = "not_committed", Ref = e.Ref, Detail = f + " not confirmed in the source" + (e.Source != null ? " (" + e.Source + ")" : "") });
                }
                if (!eTaken[i].HasValue)
                {
                    r.Missing++;
                    var near = model.Select((a, j) => new { a, j, d = D2(e.X, e.Y, a.X, a.Y) }).OrderBy(x => x.d).FirstOrDefault();
                    r.Findings.Add(new Finding
                    {
                        Code = "missing", Ref = e.Ref,
                        Detail = "no element within ≤" + F(o.TolXY) + " mm" + (o.SameLevel && e.Level != null ? " on " + e.Level : "") +
                                 (near != null ? "; the closest is #" + near.a.Id + " at " + F(near.d) + " mm" + (aTaken[near.j].HasValue ? " (already assigned to " + expected[aTaken[near.j].Value].Ref + ")" : "") : ""),
                    });
                    continue;
                }
                var j0 = eTaken[i].Value;
                var a0 = model[j0];
                var d0 = D2(e.X, e.Y, a0.X, a0.Y);
                // Another candidate almost equally close (for this reference), or another reference almost equally close to this element?
                // A rival counts only if it does not clearly have a better match of its own (if it does, there is no doubt).
                double MatchDistE(int ei) => eTaken[ei].HasValue ? D2(expected[ei].X, expected[ei].Y, model[eTaken[ei].Value].X, model[eTaken[ei].Value].Y) : double.MaxValue;
                double MatchDistA(int aj) => aTaken[aj].HasValue ? D2(expected[aTaken[aj].Value].X, expected[aTaken[aj].Value].Y, model[aj].X, model[aj].Y) : double.MaxValue;
                var rivalA = candE[i].Where(c => c.Item1 != j0 && c.Item2 - d0 < o.AmbiguityMargin && !(MatchDistA(c.Item1) + o.AmbiguityMargin <= c.Item2)).ToList();
                var rivalE = candA[j0].Where(c => c.Item1 != i && Math.Abs(c.Item2 - d0) < o.AmbiguityMargin && !(MatchDistE(c.Item1) + o.AmbiguityMargin <= c.Item2)).ToList();
                if (rivalA.Count > 0 || rivalE.Count > 0)
                {
                    r.Ambiguous++;
                    r.Findings.Add(new Finding
                    {
                        Code = "ambiguous", Ref = e.Ref, Id = a0.Id,
                        Detail = rivalA.Count > 0
                            ? "candidates #" + a0.Id + " (" + F(d0) + " mm) and " + string.Join(", ", rivalA.Select(c => "#" + model[c.Item1].Id + " (" + F(c.Item2) + " mm)")) + ": proximity does not prove which one it is"
                            : "#" + a0.Id + " is almost equally close to " + string.Join(", ", rivalE.Select(c => expected[c.Item1].Ref)) + ": not approved on proximity",
                    });
                    continue;
                }
                var devs = new List<Finding>();
                if (d0 > o.TolXY * 0.5) devs.Add(new Finding { Code = "position", Ref = e.Ref, Id = a0.Id, Detail = "displaced " + F(d0) + " mm (tolerance " + F(o.TolXY) + ")" });
                if (e.Z.HasValue && Math.Abs(a0.Z - e.Z.Value) > o.TolZ)
                    devs.Add(new Finding { Code = "height", Ref = e.Ref, Id = a0.Id, Detail = "z " + F(a0.Z) + " mm, expected " + F(e.Z.Value) + " (±" + F(o.TolZ) + ")" });
                if (e.RotDeg.HasValue && a0.RotDeg.HasValue && RotDiff(a0.RotDeg.Value, e.RotDeg.Value, o.Symmetry) > o.TolRot)
                    devs.Add(new Finding { Code = "orientation", Ref = e.Ref, Id = a0.Id, Detail = "rotation " + F(a0.RotDeg.Value) + "°, expected " + F(e.RotDeg.Value) + "°" });
                if (!TypeAllowed(e, a0))
                    devs.Add(new Finding { Code = "type", Ref = e.Ref, Id = a0.Id, Detail = a0.FullType + " is not among " + string.Join(" | ", e.Types) });
                if (!o.SameLevel && !LevelOk(e, a0))
                    devs.Add(new Finding { Code = "level", Ref = e.Ref, Id = a0.Id, Detail = a0.Level + ", expected " + e.Level });
                if (e.Tag == true && a0.Tags == 0) devs.Add(new Finding { Code = "no_tag", Ref = e.Ref, Id = a0.Id });
                if (e.Tag == true && a0.Tags > 1) devs.Add(new Finding { Code = "duplicate_tag", Ref = e.Ref, Id = a0.Id, Detail = a0.Tags + " tags" });
                if (e.Tag == false && a0.Tags > 0) devs.Add(new Finding { Code = "extra_tag", Ref = e.Ref, Id = a0.Id, Detail = a0.Tags + " tags and the rule says none" });
                if (!string.IsNullOrEmpty(e.Group) && !string.Equals(e.Group, a0.Group, StringComparison.OrdinalIgnoreCase))
                    devs.Add(new Finding { Code = "group", Ref = e.Ref, Id = a0.Id, Detail = "in " + (a0.Group ?? "(no group)") + ", expected " + e.Group });
                if (!string.IsNullOrEmpty(e.Mounting) && !string.IsNullOrEmpty(a0.Mounting) && !string.Equals(e.Mounting, a0.Mounting, StringComparison.OrdinalIgnoreCase))
                    devs.Add(new Finding { Code = "mounting", Ref = e.Ref, Id = a0.Id, Detail = a0.Mounting + ", expected " + e.Mounting });
                r.Pairs[e.Ref] = a0.Id;
                if (devs.Count == 0) r.Approved++;
                else { r.WithDeviation++; r.Findings.AddRange(devs); }
            }

            // 4) Extras: in-scope elements that justify no reference.
            for (var j = 0; j < model.Count; j++)
            {
                if (aTaken[j].HasValue) continue;
                r.Extra++;
                var a = model[j];
                var near = expected.Select((e, i) => new { e, d = D2(e.X, e.Y, a.X, a.Y) }).OrderBy(x => x.d).FirstOrDefault();
                r.Findings.Add(new Finding
                {
                    Code = "leftover", Id = a.Id,
                    Detail = a.FullType + " on " + (a.Level ?? "?") + (near != null ? "; closest reference " + near.e.Ref + " at " + F(near.d) + " mm" : ""),
                });
            }

            // 5) Near duplicates: same type almost in the same place (two stacked fixtures), matched or not.
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
                            r.Findings.Add(new Finding { Code = "near_duplicate", Id = a.Id, Detail = "#" + a.Id + " and #" + b.Id + " (" + a.FullType + ") at " + F(d3) + " mm" });
                        }
                    }
            }
            return r;
        }

        private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        // ---------------- manifest from JSON ----------------

        /// <summary>
        /// Reads a manifest: {"tolerances":{xy,z,giro,simetria,ambiguedad,duplicado,mismo_nivel},"elements":[{ref,x,y,z,giro,nivel,tipos,
        /// etiqueta,grupo,montaje,fuente,no_confirmado}]} (mm and degrees).
        /// </summary>
        public static List<Expected> Parse(Dictionary<string, object> json, out Options options)
        {
            options = new Options();
            if (json.TryGetValue("tolerances", out var tv) && tv is Dictionary<string, object> t)
            {
                options.TolXY = Num(t, "xy") ?? options.TolXY;
                options.TolZ = Num(t, "z") ?? options.TolZ;
                options.TolRot = Num(t, "rotation") ?? options.TolRot;
                options.Symmetry = Num(t, "symmetry") ?? options.Symmetry;
                options.AmbiguityMargin = Num(t, "ambiguity") ?? options.AmbiguityMargin;
                options.NearDuplicate = Num(t, "duplicate") ?? options.NearDuplicate;
                options.SameLevel = Arg.GetBool(t, "same_level") ?? options.SameLevel;
            }
            var list = new List<Expected>();
            if (!(json.TryGetValue("elements", out var ev) && ev is List<object> el)) throw new ArgumentException("the manifest needs 'elements': [...]");
            var n = 0;
            foreach (var o in el.OfType<Dictionary<string, object>>())
            {
                n++;
                var x = Num(o, "x");
                var y = Num(o, "y");
                if (!x.HasValue || !y.HasValue) throw new ArgumentException("manifest element " + n + " has no x/y (mm)");
                var types = o.TryGetValue("types", out var tp) && tp is List<object> tl ? tl.Select(s => Convert.ToString(s, CultureInfo.InvariantCulture)).ToList()
                    : Arg.GetString(o, "type") is string one ? new List<string> { one } : new List<string>();
                list.Add(new Expected
                {
                    Ref = Arg.GetString(o, "ref") ?? ("#" + n), X = x.Value, Y = y.Value, Z = Num(o, "z"), RotDeg = Num(o, "rotation"),
                    Level = Arg.GetString(o, "level"), Types = types, Tag = Arg.GetBool(o, "tag"), Group = Arg.GetString(o, "group"),
                    Mounting = Arg.GetString(o, "mounting"), Source = Arg.GetString(o, "source"), Unconfirmed = Arg.GetStringList(o, "not_committed"),
                });
            }
            var dup = list.GroupBy(e => e.Ref).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException("reference '" + dup.Key + "' appears twice in the manifest");
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
    /// 3D affine transform (rotation/mirror + translation) to bring coordinates from a link or a group into the host without the Revit
    /// API. Columns BasisX, BasisY, BasisZ and Origin as in Autodesk.Revit.DB.Transform.
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

        /// <summary>Composition: first this, then <paramref name="after"/> (e.g. instance in the link → link in the host).</summary>
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

        /// <summary>Mirror = negative determinant (the family orientation is flipped).</summary>
        public bool Mirrored => Determinant < 0;

        /// <summary>Plan rotation of the transformed X axis (degrees, 0–360).</summary>
        public double RotationDeg => ((Math.Atan2(X[1], X[0]) * 180 / Math.PI) % 360 + 360) % 360;

        public Xform Inverse()
        {
            // Rigid (orthonormal, with or without mirror): the inverse of the linear part is its transpose.
            var t = new Xform { X = new[] { X[0], Y[0], Z[0] }, Y = new[] { X[1], Y[1], Z[1] }, Z = new[] { X[2], Y[2], Z[2] } };
            var o = t.Dir(O);
            t.O = new[] { -o[0], -o[1], -o[2] };
            return t;
        }
    }
}
