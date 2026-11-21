// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Workflows
{
    /// <summary>
    /// AUD-20: what a Copy/Monitor attempt expects to achieve, frozen at preflight: host, link and its revision,
    /// source elements, transform, type mapping, exclusions and category. Later verification runs against THESE
    /// expectations; if any condition has changed at verification time (the link was reloaded with other fixtures, moved, or a
    /// different mapping is requested), a conflict is returned: a new verification never silently replaces the original target.
    /// The source→copy correspondence is still inferred by type and position; freezing the source does not make it an identity.
    /// </summary>
    public sealed class CopyMonitorExpectations
    {
        public const string Schema = "bimbridge.copy_monitor_expectations/1";
        public string HostIdentity, LinkIdentity, LinkRevision, Category;
        public LinkTransform Transform = LinkTransform.Identity;
        public List<TypeMapping> Mappings = new List<TypeMapping>();
        public List<SourceFixture> Source = new List<SourceFixture>();

        private static string N(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);
        private static string V(double[] a) => a == null ? "" : string.Join(",", a.Select(N));

        private static string SourceLine(SourceFixture s) =>
            string.Join("|", s.UniqueId ?? "", s.Category ?? "", s.Family ?? "", s.Type ?? "", s.Level ?? "", N(s.X), N(s.Y), N(s.Z), N(s.RotationDeg));

        /// <summary>Fingerprint of the source elements (stable order by UniqueId): changes if any is added, removed, moved or changes type.</summary>
        public string SourceFingerprint => Home.Sha256Hex(string.Join("\n", Source.OrderBy(s => s.UniqueId, StringComparer.Ordinal).Select(SourceLine)), 32);

        public string TransformText => N(Transform.Dx) + "," + N(Transform.Dy) + "," + N(Transform.Dz) + ";" + N(Transform.RotationDeg) + ";" + V(Transform.BasisX) + ";" + V(Transform.BasisY) + ";" + V(Transform.BasisZ);

        private IEnumerable<string> MapLines(bool excluded) =>
            Mappings.Where(m => m.Excluded == excluded).Select(m => (m.Source ?? "").Trim() + (excluded ? "" : "→" + (m.Target ?? "").Trim())).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        public string MappingText => string.Join(";", MapLines(false));
        public string ExclusionText => string.Join(";", MapLines(true));

        /// <summary>Fingerprint of all expectations.</summary>
        public string Fingerprint => Home.Sha256Hex(string.Join("\n", HostIdentity ?? "", LinkIdentity ?? "", LinkRevision ?? "", (Category ?? "").ToLowerInvariant(),
            TransformText, MappingText.ToLowerInvariant(), ExclusionText.ToLowerInvariant(), SourceFingerprint), 32);

        /// <summary>
        /// Differences of <paramref name="now"/> from these frozen expectations (empty = nothing that alters the target).
        /// The link revision only counts if both are known; the source elements are always compared.
        /// </summary>
        public List<string> Diff(CopyMonitorExpectations now)
        {
            var d = new List<string>();
            if (!string.Equals(HostIdentity, now.HostIdentity, StringComparison.Ordinal)) d.Add("different host");
            if (!string.Equals(LinkIdentity, now.LinkIdentity, StringComparison.Ordinal)) d.Add("different link");
            if (!string.Equals(Category, now.Category, StringComparison.OrdinalIgnoreCase)) d.Add("category: " + Category + " → " + now.Category);
            if (TransformText != now.TransformText) d.Add("link transform changed (the link was moved, rotated or mirrored)");
            if (!string.Equals(MappingText, now.MappingText, StringComparison.OrdinalIgnoreCase)) d.Add("different type mapping: «" + MappingText + "» → «" + now.MappingText + "»");
            if (!string.Equals(ExclusionText, now.ExclusionText, StringComparison.OrdinalIgnoreCase)) d.Add("different exclusions: «" + ExclusionText + "» → «" + now.ExclusionText + "»");
            if (SourceFingerprint != now.SourceFingerprint)
            {
                var a = Source.ToDictionary(s => s.UniqueId ?? "", SourceLine, StringComparer.Ordinal);
                var b = now.Source.GroupBy(s => s.UniqueId ?? "").ToDictionary(g => g.Key, g => SourceLine(g.First()), StringComparer.Ordinal);
                var added = b.Keys.Count(k => !a.ContainsKey(k)); var removed = a.Keys.Count(k => !b.ContainsKey(k));
                var changed = a.Count(kv => b.TryGetValue(kv.Key, out var l) && l != kv.Value);
                d.Add("source elements changed: +" + added + " −" + removed + " modified " + changed);
            }
            else if (!string.IsNullOrEmpty(LinkRevision) && !string.IsNullOrEmpty(now.LinkRevision) && LinkRevision != now.LinkRevision)
                d.Add("the link has a different saved revision (its fixtures of this category did not change)");
            return d;
        }

        /// <summary>Does the difference alter the target? A new link revision without fixture changes does not: it is reported but does not block.</summary>
        public static bool Blocking(string difference) => !difference.StartsWith("the link has a different saved revision", StringComparison.Ordinal);

        public string ToText() => Json.Write(new Dictionary<string, object>
        {
            ["esquema"] = Schema, ["anfitrion"] = HostIdentity, ["vinculo"] = LinkIdentity, ["revision_vinculo"] = LinkRevision, ["categoria"] = Category,
            ["transformacion"] = new Dictionary<string, object>
            {
                ["dx"] = Transform.Dx, ["dy"] = Transform.Dy, ["dz"] = Transform.Dz, ["rot"] = Transform.RotationDeg,
                ["bx"] = Vec(Transform.BasisX), ["by"] = Vec(Transform.BasisY), ["bz"] = Vec(Transform.BasisZ),
            },
            ["mapeo"] = Mappings.Select(m => (object)new Dictionary<string, object> { ["origen"] = m.Source, ["destino"] = m.Target, ["excluido"] = m.Excluded }).ToList(),
            ["origen"] = Source.OrderBy(s => s.UniqueId, StringComparer.Ordinal).Select(s => (object)new List<object> { s.UniqueId, s.Category, s.Family, s.Type, s.Level, s.X, s.Y, s.Z, s.RotationDeg }).ToList(),
            ["huella_origen"] = SourceFingerprint, ["huella"] = Fingerprint,
        });

        private static object Vec(double[] a) => a == null ? null : (object)a.Select(x => (object)x).ToList();
        private static double[] Arr(object o) => o is List<object> l ? l.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray() : null;
        private static double D(object o) => Convert.ToDouble(o, CultureInfo.InvariantCulture);

        /// <summary>Reads saved expectations. Throws InvalidDataException if they do not match their own fingerprint.</summary>
        public static CopyMonitorExpectations FromText(string text)
        {
            try
            {
                var d = Json.ParseObject(text);
                if (Arg.GetString(d, "esquema") != Schema) throw new System.IO.InvalidDataException("expectations with unknown schema");
                var t = (Dictionary<string, object>)d["transformacion"];
                var e = new CopyMonitorExpectations
                {
                    HostIdentity = Arg.GetString(d, "anfitrion"), LinkIdentity = Arg.GetString(d, "vinculo"), LinkRevision = Arg.GetString(d, "revision_vinculo"), Category = Arg.GetString(d, "categoria"),
                    Transform = new LinkTransform { Dx = D(t["dx"]), Dy = D(t["dy"]), Dz = D(t["dz"]), RotationDeg = D(t["rot"]), BasisX = Arr(t["bx"]), BasisY = Arr(t["by"]), BasisZ = Arr(t["bz"]) },
                    Mappings = ((List<object>)d["mapeo"]).Cast<Dictionary<string, object>>().Select(m => new TypeMapping { Source = Arg.GetString(m, "origen"), Target = Arg.GetString(m, "destino"), Excluded = Arg.GetBool(m, "excluido") == true }).ToList(),
                    Source = ((List<object>)d["origen"]).Cast<List<object>>().Select(r => new SourceFixture
                    {
                        UniqueId = (string)r[0], Category = (string)r[1], Family = (string)r[2], Type = (string)r[3], Level = (string)r[4], X = D(r[5]), Y = D(r[6]), Z = D(r[7]), RotationDeg = D(r[8]),
                    }).ToList(),
                };
                if (e.Fingerprint != Arg.GetString(d, "huella")) throw new System.IO.InvalidDataException("the saved expectations do not match their fingerprint");
                return e;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is KeyNotFoundException || ex is ArgumentException || ex is InvalidOperationException)
            {
                throw new System.IO.InvalidDataException("unreadable expectations: " + ex.GetType().Name);
            }
        }
    }
}
