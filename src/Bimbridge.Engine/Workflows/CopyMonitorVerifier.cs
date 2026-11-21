// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bimbridge.Engine.Workflows
{
    /// <summary>Fixture in the source link (link coordinates).</summary>
    public sealed class SourceFixture
    {
        public string UniqueId;
        public string Category;
        public string Family, Type;
        public string Level;
        public double X, Y, Z;
        public double RotationDeg;
    }

    /// <summary>Host fixture after the copy (host coordinates).</summary>
    public sealed class HostFixture
    {
        public long Id;
        public string Category;
        public string Family, Type;
        public string Level;
        public double X, Y, Z;
        public double RotationDeg;
        /// <summary>Element.IsMonitoringLinkElement() read from the element.</summary>
        public bool Monitoring;
        /// <summary>GetMonitoredLinkElementIds(): ids of monitored link INSTANCES (not of the source fixture).</summary>
        public List<long> MonitoredLinkInstances = new List<long>();
    }

    /// <summary>
    /// AUD-11: full link transform (RevitLinkInstance.GetTotalTransform(): BasisX/Y/Z + Origin). Supports rotation,
    /// translation and reflection. If the bases are not given they are derived from <see cref="RotationDeg"/> (plan rotation).
    /// Only orthonormal transforms with BasisZ = ±Z (non-tilted link) are represented: the rest are REJECTED, because the
    /// plan orientation of a fixture has no unique equivalent.
    /// </summary>
    public sealed class LinkTransform
    {
        public double Dx, Dy, Dz, RotationDeg;
        public double[] BasisX, BasisY, BasisZ;
        public static LinkTransform Identity => new LinkTransform();

        private double[] Bx => BasisX ?? new[] { Math.Cos(RotationDeg * Math.PI / 180.0), Math.Sin(RotationDeg * Math.PI / 180.0), 0.0 };
        private double[] By => BasisY ?? new[] { -Math.Sin(RotationDeg * Math.PI / 180.0), Math.Cos(RotationDeg * Math.PI / 180.0), 0.0 };
        private double[] Bz => BasisZ ?? new[] { 0.0, 0.0, 1.0 };

        public (double X, double Y, double Z) Apply(double x, double y, double z)
        {
            double[] a = Bx, b = By, c = Bz;
            return (a[0] * x + b[0] * y + c[0] * z + Dx, a[1] * x + b[1] * y + c[1] * z + Dy, a[2] * x + b[2] * y + c[2] * z + Dz);
        }

        /// <summary>Determinant &lt; 0: the link is mirrored.</summary>
        public bool Mirrored
        {
            get
            {
                double[] a = Bx, b = By, c = Bz;
                return a[0] * (b[1] * c[2] - b[2] * c[1]) - a[1] * (b[0] * c[2] - b[2] * c[0]) + a[2] * (b[0] * c[1] - b[1] * c[0]) < 0;
            }
        }

        /// <summary>null if it can be represented; otherwise, the reason.</summary>
        public string Unsupported()
        {
            double[] a = Bx, b = By, c = Bz;
            foreach (var v in new[] { a, b, c, new[] { Dx, Dy, Dz } })
                if (v == null || v.Length != 3 || v.Any(d => double.IsNaN(d) || double.IsInfinity(d))) return "transform with non-finite values";
            double Dot(double[] u, double[] w) => u[0] * w[0] + u[1] * w[1] + u[2] * w[2];
            const double eps = 1e-6;
            if (Math.Abs(Dot(a, a) - 1) > eps || Math.Abs(Dot(b, b) - 1) > eps || Math.Abs(Dot(c, c) - 1) > eps) return "transform with scale (not orthonormal)";
            if (Math.Abs(Dot(a, b)) > eps || Math.Abs(Dot(a, c)) > eps || Math.Abs(Dot(b, c)) > eps) return "transform with shear (not orthogonal)";
            if (Math.Abs(Math.Abs(c[2]) - 1) > eps) return "tilted link (BasisZ not vertical): the plan orientation cannot be compared";
            return null;
        }

        /// <summary>Plan angle (degrees) that an element rotated <paramref name="deg"/> in the link will have in the host.</summary>
        public double RotateAngle(double deg)
        {
            var r = deg * Math.PI / 180.0;
            double[] a = Bx, b = By;
            var hx = a[0] * Math.Cos(r) + b[0] * Math.Sin(r);
            var hy = a[1] * Math.Cos(r) + b[1] * Math.Sin(r);
            return Math.Atan2(hy, hx) * 180.0 / Math.PI;
        }
    }

    /// <summary>Type mapping rule (Coordination Settings): source "Family: Type" → target "Family: Type", or excluded.</summary>
    public sealed class TypeMapping
    {
        public string Source;
        public string Target;     // null = copy the original type
        public bool Excluded;     // "Don't copy this type"
    }

    /// <summary>
    /// 0.9.1 (Block F, #24 CM-04): source→host verification of a monitored copy of fixtures. It does not invent an id
    /// correspondence: it pairs each source fixture with at most one copy by expected type and position after the transform (proof
    /// level "inferred"), and requires the monitoring relationship of each copy. An unmonitored copy counts as a failure.
    /// </summary>
    public static class CopyMonitorVerifier
    {
        public const double DefaultTolerance = 0.01;      // feet (~3 mm)
        public const double DefaultAngleTolerance = 0.5;  // degrees

        public static string Key(string fam, string type) => (fam ?? "").Trim() + ": " + (type ?? "").Trim();

        public static Dictionary<string, object> Verify(IList<SourceFixture> source, IList<HostFixture> hostBefore, IList<HostFixture> hostAfter,
                                                        LinkTransform tf, IList<TypeMapping> mappings, long linkInstanceId,
                                                        double tol = DefaultTolerance, double angTol = DefaultAngleTolerance)
        {
            tf = tf ?? LinkTransform.Identity;
            var unsupported = tf.Unsupported();
            if (unsupported != null)
                return new Dictionary<string, object> { ["result"] = "not_verifiable", ["reason"] = unsupported, ["origin"] = (long)(source?.Count ?? 0) };
            var maps = (mappings ?? new List<TypeMapping>()).ToDictionary(m => m.Source, StringComparer.OrdinalIgnoreCase);
            var before = new HashSet<long>((hostBefore ?? new List<HostFixture>()).Select(h => h.Id));
            var created = (hostAfter ?? new List<HostFixture>()).Where(h => !before.Contains(h.Id)).ToList();
            var free = new List<HostFixture>(created);

            var matched = new List<object>();
            var missing = new List<object>();
            var wrongType = new List<object>();
            var unmonitored = new List<object>();
            var misplaced = new List<object>();
            var excluded = new List<object>();
            var expected = 0;

            foreach (var s in source)
            {
                var sk = Key(s.Family, s.Type);
                maps.TryGetValue(sk, out var map);
                if (map != null && map.Excluded) { excluded.Add(s.UniqueId + " (" + sk + ": excluded by mapping)"); continue; }
                expected++;
                var want = map?.Target ?? sk;
                var p = tf.Apply(s.X, s.Y, s.Z);
                var wantRot = Norm(tf.RotateAngle(s.RotationDeg));
                double D(HostFixture h) => Math.Sqrt((h.X - p.X) * (h.X - p.X) + (h.Y - p.Y) * (h.Y - p.Y) + (h.Z - p.Z) * (h.Z - p.Z));

                var near = free.Where(h => D(h) <= tol).OrderBy(D).ToList();
                if (near.Count == 0)
                {
                    // Is there a copy of the right type nearby but displaced or rotated? It is reported as misplaced, not as missing.
                    var close = free.Where(h => string.Equals(Key(h.Family, h.Type), want, StringComparison.OrdinalIgnoreCase) && D(h) <= Math.Max(1.0, tol * 100)).OrderBy(D).FirstOrDefault();
                    if (close != null) { misplaced.Add(s.UniqueId + " → " + close.Id + " at " + D(close).ToString("0.###", CultureInfo.InvariantCulture) + " ft"); free.Remove(close); }
                    else missing.Add(s.UniqueId + " (" + sk + ")");
                    continue;
                }
                var h0 = near.FirstOrDefault(h => string.Equals(Key(h.Family, h.Type), want, StringComparison.OrdinalIgnoreCase)) ?? near[0];
                free.Remove(h0);
                var hk = Key(h0.Family, h0.Type);
                if (!string.Equals(hk, want, StringComparison.OrdinalIgnoreCase)) { wrongType.Add(s.UniqueId + " → " + h0.Id + ": " + hk + ", expected " + want); continue; }
                if (Math.Abs(Norm(h0.RotationDeg - wantRot)) > angTol) { misplaced.Add(s.UniqueId + " → " + h0.Id + ": rotated " + h0.RotationDeg.ToString("0.#", CultureInfo.InvariantCulture) + "°, expected " + wantRot.ToString("0.#", CultureInfo.InvariantCulture) + "°"); continue; }
                if (!h0.Monitoring || (linkInstanceId > 0 && !h0.MonitoredLinkInstances.Contains(linkInstanceId))) { unmonitored.Add(s.UniqueId + " → " + h0.Id); continue; }
                matched.Add(s.UniqueId + " → " + h0.Id);
            }

            // Leftover copies at the same position as an already matched one = duplicates; the rest, unexpected.
            var duplicates = new List<object>();
            var unexpected = new List<object>();
            foreach (var h in free)
            {
                var dupOf = source.Any(s => { var p = tf.Apply(s.X, s.Y, s.Z); return Math.Abs(h.X - p.X) <= tol && Math.Abs(h.Y - p.Y) <= tol && Math.Abs(h.Z - p.Z) <= tol; });
                (dupOf ? duplicates : unexpected).Add(h.Id + " (" + Key(h.Family, h.Type) + ")");
            }

            var passed = matched.Count == expected && missing.Count == 0 && wrongType.Count == 0 && unmonitored.Count == 0 && misplaced.Count == 0 && duplicates.Count == 0 && unexpected.Count == 0;
            var bySource = source.GroupBy(s => Key(s.Family, s.Type)).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => (object)(long)g.Count());
            var byHost = created.GroupBy(h => Key(h.Family, h.Type)).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => (object)(long)g.Count());
            Dictionary<string, object> Bucket(List<object> l) => new Dictionary<string, object> { ["n"] = (long)l.Count, ["examples"] = l.Take(20).ToList() };
            return new Dictionary<string, object>
            {
                ["result"] = passed ? "verified" : "failed",
                ["correspondence_test_level"] = "inferred",   // matched by type and position, not by source id
                // AUD-08: each dimension separately. This verifier does NOT observe the command, the end of the workflow or the save.
                ["dimensions_list"] = new Dictionary<string, object>
                {
                    ["geometric_consistency"] = (long)(matched.Count + unmonitored.Count),
                    ["link_supervision"] = (long)matched.Count,
                    ["individual_origin_correspondence"] = "not_demonstrated: inferred by type and position",
                    ["command_started"] = "fuera_del_verificador (workflow receipt)",
                    ["workflow_finished"] = "fuera_del_verificador (workflow receipt)",
                    ["save_confirmed"] = "fuera_del_verificador (workflow receipt)",
                },
                ["mirrored_link"] = tf.Mirrored,
                ["origin"] = (long)source.Count, ["expected_items"] = (long)expected, ["created"] = (long)created.Count,
                ["origin_by_type"] = bySource, ["created_by_type"] = byHost,
                ["correct"] = Bucket(matched), ["missing"] = Bucket(missing), ["wrong_type"] = Bucket(wrongType),
                ["unsupervised"] = Bucket(unmonitored), ["misplaced"] = Bucket(misplaced), ["duplicates"] = Bucket(duplicates),
                ["unexpected"] = Bucket(unexpected), ["excluded_items"] = Bucket(excluded),
            };
        }

        private static double Norm(double deg)
        {
            var d = deg % 360.0;
            if (d > 180) d -= 360;
            if (d <= -180) d += 360;
            return d;
        }
    }
}
