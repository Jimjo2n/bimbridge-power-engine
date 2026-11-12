// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Workflows
{
    /// <summary>
    /// AUD-20: lo que un intento de Copy/Monitor espera conseguir, congelado en el preflight: anfitrión, vínculo y su revisión,
    /// elementos de origen, transformación, mapeo de tipos, exclusiones y categoría. La verificación posterior se hace contra ESTAS
    /// expectativas; si al verificar alguna condición ha cambiado (el vínculo se recargó con otros aparatos, se movió, o se pide otro
    /// mapeo), se devuelve conflicto: una verificación nueva nunca sustituye en silencio el objetivo original.
    /// La correspondencia origen→copia sigue siendo inferida por tipo y posición; congelar el origen no la convierte en identidad.
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

        /// <summary>Huella de los elementos de origen (orden estable por UniqueId): cambia si se añade, quita, mueve o cambia de tipo alguno.</summary>
        public string SourceFingerprint => Home.Sha256Hex(string.Join("\n", Source.OrderBy(s => s.UniqueId, StringComparer.Ordinal).Select(SourceLine)), 32);

        public string TransformText => N(Transform.Dx) + "," + N(Transform.Dy) + "," + N(Transform.Dz) + ";" + N(Transform.RotationDeg) + ";" + V(Transform.BasisX) + ";" + V(Transform.BasisY) + ";" + V(Transform.BasisZ);

        private IEnumerable<string> MapLines(bool excluded) =>
            Mappings.Where(m => m.Excluded == excluded).Select(m => (m.Source ?? "").Trim() + (excluded ? "" : "→" + (m.Target ?? "").Trim())).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        public string MappingText => string.Join(";", MapLines(false));
        public string ExclusionText => string.Join(";", MapLines(true));

        /// <summary>Huella de todas las expectativas.</summary>
        public string Fingerprint => Home.Sha256Hex(string.Join("\n", HostIdentity ?? "", LinkIdentity ?? "", LinkRevision ?? "", (Category ?? "").ToLowerInvariant(),
            TransformText, MappingText.ToLowerInvariant(), ExclusionText.ToLowerInvariant(), SourceFingerprint), 32);

        /// <summary>
        /// Diferencias de <paramref name="now"/> respecto a estas expectativas congeladas (vacío = nada que altere el objetivo).
        /// La revisión del vínculo sólo cuenta si ambas se conocen; los elementos de origen se comparan siempre.
        /// </summary>
        public List<string> Diff(CopyMonitorExpectations now)
        {
            var d = new List<string>();
            if (!string.Equals(HostIdentity, now.HostIdentity, StringComparison.Ordinal)) d.Add("anfitrión distinto");
            if (!string.Equals(LinkIdentity, now.LinkIdentity, StringComparison.Ordinal)) d.Add("vínculo distinto");
            if (!string.Equals(Category, now.Category, StringComparison.OrdinalIgnoreCase)) d.Add("categoría: " + Category + " → " + now.Category);
            if (TransformText != now.TransformText) d.Add("transformación del vínculo cambiada (el vínculo se movió, giró o reflejó)");
            if (!string.Equals(MappingText, now.MappingText, StringComparison.OrdinalIgnoreCase)) d.Add("mapeo de tipos distinto: «" + MappingText + "» → «" + now.MappingText + "»");
            if (!string.Equals(ExclusionText, now.ExclusionText, StringComparison.OrdinalIgnoreCase)) d.Add("exclusiones distintas: «" + ExclusionText + "» → «" + now.ExclusionText + "»");
            if (SourceFingerprint != now.SourceFingerprint)
            {
                var a = Source.ToDictionary(s => s.UniqueId ?? "", SourceLine, StringComparer.Ordinal);
                var b = now.Source.GroupBy(s => s.UniqueId ?? "").ToDictionary(g => g.Key, g => SourceLine(g.First()), StringComparer.Ordinal);
                var added = b.Keys.Count(k => !a.ContainsKey(k)); var removed = a.Keys.Count(k => !b.ContainsKey(k));
                var changed = a.Count(kv => b.TryGetValue(kv.Key, out var l) && l != kv.Value);
                d.Add("elementos de origen cambiados: +" + added + " −" + removed + " modificados " + changed);
            }
            else if (!string.IsNullOrEmpty(LinkRevision) && !string.IsNullOrEmpty(now.LinkRevision) && LinkRevision != now.LinkRevision)
                d.Add("el vínculo tiene otra revisión guardada (sus aparatos de esta categoría no cambiaron)");
            return d;
        }

        /// <summary>¿La diferencia altera el objetivo? Una revisión nueva del vínculo sin cambios en los aparatos no lo altera: se informa pero no bloquea.</summary>
        public static bool Blocking(string difference) => !difference.StartsWith("el vínculo tiene otra revisión", StringComparison.Ordinal);

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

        /// <summary>Lee expectativas guardadas. Lanza InvalidDataException si no cuadran con su propia huella.</summary>
        public static CopyMonitorExpectations FromText(string text)
        {
            try
            {
                var d = Json.ParseObject(text);
                if (Arg.GetString(d, "esquema") != Schema) throw new System.IO.InvalidDataException("expectativas con esquema desconocido");
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
                if (e.Fingerprint != Arg.GetString(d, "huella")) throw new System.IO.InvalidDataException("las expectativas guardadas no coinciden con su huella");
                return e;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is KeyNotFoundException || ex is ArgumentException || ex is InvalidOperationException)
            {
                throw new System.IO.InvalidDataException("expectativas ilegibles: " + ex.GetType().Name);
            }
        }
    }
}
