// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PDI.RevitMcp.Engine.Workflows
{
    /// <summary>Aparato del vínculo de origen (coordenadas del vínculo).</summary>
    public sealed class SourceFixture
    {
        public string UniqueId;
        public string Category;
        public string Family, Type;
        public string Level;
        public double X, Y, Z;
        public double RotationDeg;
    }

    /// <summary>Aparato del anfitrión tras la copia (coordenadas del anfitrión).</summary>
    public sealed class HostFixture
    {
        public long Id;
        public string Category;
        public string Family, Type;
        public string Level;
        public double X, Y, Z;
        public double RotationDeg;
        /// <summary>Element.IsMonitoringLinkElement() leído del elemento.</summary>
        public bool Monitoring;
        /// <summary>GetMonitoredLinkElementIds(): ids de INSTANCIAS de vínculo supervisadas (no del aparato de origen).</summary>
        public List<long> MonitoredLinkInstances = new List<long>();
    }

    /// <summary>
    /// AUD-11: transformación completa del vínculo (RevitLinkInstance.GetTotalTransform(): BasisX/Y/Z + Origin). Admite rotación,
    /// traslación y reflexión. Si no se dan las bases se derivan de <see cref="RotationDeg"/> (rotación en planta).
    /// Sólo se representan transformaciones ortonormales con BasisZ = ±Z (vínculo no inclinado): el resto se RECHAZA, porque la
    /// orientación en planta de un aparato no tiene un equivalente único.
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

        /// <summary>Determinante &lt; 0: el vínculo está reflejado (espejo).</summary>
        public bool Mirrored
        {
            get
            {
                double[] a = Bx, b = By, c = Bz;
                return a[0] * (b[1] * c[2] - b[2] * c[1]) - a[1] * (b[0] * c[2] - b[2] * c[0]) + a[2] * (b[0] * c[1] - b[1] * c[0]) < 0;
            }
        }

        /// <summary>null si se puede representar; si no, el motivo.</summary>
        public string Unsupported()
        {
            double[] a = Bx, b = By, c = Bz;
            foreach (var v in new[] { a, b, c, new[] { Dx, Dy, Dz } })
                if (v == null || v.Length != 3 || v.Any(d => double.IsNaN(d) || double.IsInfinity(d))) return "transformación con valores no finitos";
            double Dot(double[] u, double[] w) => u[0] * w[0] + u[1] * w[1] + u[2] * w[2];
            const double eps = 1e-6;
            if (Math.Abs(Dot(a, a) - 1) > eps || Math.Abs(Dot(b, b) - 1) > eps || Math.Abs(Dot(c, c) - 1) > eps) return "transformación con escala (no ortonormal)";
            if (Math.Abs(Dot(a, b)) > eps || Math.Abs(Dot(a, c)) > eps || Math.Abs(Dot(b, c)) > eps) return "transformación con cizalla (no ortogonal)";
            if (Math.Abs(Math.Abs(c[2]) - 1) > eps) return "vínculo inclinado (BasisZ no vertical): la orientación en planta no se puede comparar";
            return null;
        }

        /// <summary>Ángulo en planta (grados) que tendrá en el anfitrión un elemento girado <paramref name="deg"/> en el vínculo.</summary>
        public double RotateAngle(double deg)
        {
            var r = deg * Math.PI / 180.0;
            double[] a = Bx, b = By;
            var hx = a[0] * Math.Cos(r) + b[0] * Math.Sin(r);
            var hy = a[1] * Math.Cos(r) + b[1] * Math.Sin(r);
            return Math.Atan2(hy, hx) * 180.0 / Math.PI;
        }
    }

    /// <summary>Regla de mapeo de tipos (Coordination Settings): origen "Familia: Tipo" → destino "Familia: Tipo", o excluido.</summary>
    public sealed class TypeMapping
    {
        public string Source;
        public string Target;     // null = copiar tipo original
        public bool Excluded;     // "No copiar este tipo"
    }

    /// <summary>
    /// 0.9.1 (Bloque F, #24 CM-04): verificación origen→anfitrión de una copia supervisada de aparatos. No inventa una correspondencia
    /// de ids: empareja cada aparato de origen con a lo sumo una copia por tipo esperado y posición tras la transformación (nivel de
    /// prueba "inferred"), y exige la relación de supervisión de cada copia. Una copia sin supervisión cuenta como fallo.
    /// </summary>
    public static class CopyMonitorVerifier
    {
        public const double DefaultTolerance = 0.01;      // pies (~3 mm)
        public const double DefaultAngleTolerance = 0.5;  // grados

        public static string Key(string fam, string type) => (fam ?? "").Trim() + ": " + (type ?? "").Trim();

        public static Dictionary<string, object> Verify(IList<SourceFixture> source, IList<HostFixture> hostBefore, IList<HostFixture> hostAfter,
                                                        LinkTransform tf, IList<TypeMapping> mappings, long linkInstanceId,
                                                        double tol = DefaultTolerance, double angTol = DefaultAngleTolerance)
        {
            tf = tf ?? LinkTransform.Identity;
            var unsupported = tf.Unsupported();
            if (unsupported != null)
                return new Dictionary<string, object> { ["resultado"] = "no_verificable", ["motivo"] = unsupported, ["origen"] = (long)(source?.Count ?? 0) };
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
                if (map != null && map.Excluded) { excluded.Add(s.UniqueId + " (" + sk + ": excluido por mapeo)"); continue; }
                expected++;
                var want = map?.Target ?? sk;
                var p = tf.Apply(s.X, s.Y, s.Z);
                var wantRot = Norm(tf.RotateAngle(s.RotationDeg));
                double D(HostFixture h) => Math.Sqrt((h.X - p.X) * (h.X - p.X) + (h.Y - p.Y) * (h.Y - p.Y) + (h.Z - p.Z) * (h.Z - p.Z));

                var near = free.Where(h => D(h) <= tol).OrderBy(D).ToList();
                if (near.Count == 0)
                {
                    // ¿Hay una copia del tipo correcto cerca pero desplazada o girada? Se informa como mal colocada, no como ausente.
                    var close = free.Where(h => string.Equals(Key(h.Family, h.Type), want, StringComparison.OrdinalIgnoreCase) && D(h) <= Math.Max(1.0, tol * 100)).OrderBy(D).FirstOrDefault();
                    if (close != null) { misplaced.Add(s.UniqueId + " → " + close.Id + " a " + D(close).ToString("0.###", CultureInfo.InvariantCulture) + " ft"); free.Remove(close); }
                    else missing.Add(s.UniqueId + " (" + sk + ")");
                    continue;
                }
                var h0 = near.FirstOrDefault(h => string.Equals(Key(h.Family, h.Type), want, StringComparison.OrdinalIgnoreCase)) ?? near[0];
                free.Remove(h0);
                var hk = Key(h0.Family, h0.Type);
                if (!string.Equals(hk, want, StringComparison.OrdinalIgnoreCase)) { wrongType.Add(s.UniqueId + " → " + h0.Id + ": " + hk + ", se esperaba " + want); continue; }
                if (Math.Abs(Norm(h0.RotationDeg - wantRot)) > angTol) { misplaced.Add(s.UniqueId + " → " + h0.Id + ": girado " + h0.RotationDeg.ToString("0.#", CultureInfo.InvariantCulture) + "°, se esperaba " + wantRot.ToString("0.#", CultureInfo.InvariantCulture) + "°"); continue; }
                if (!h0.Monitoring || (linkInstanceId > 0 && !h0.MonitoredLinkInstances.Contains(linkInstanceId))) { unmonitored.Add(s.UniqueId + " → " + h0.Id); continue; }
                matched.Add(s.UniqueId + " → " + h0.Id);
            }

            // Copias sobrantes en la misma posición que una ya emparejada = duplicados; el resto, inesperadas.
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
            Dictionary<string, object> Bucket(List<object> l) => new Dictionary<string, object> { ["n"] = (long)l.Count, ["ejemplos"] = l.Take(20).ToList() };
            return new Dictionary<string, object>
            {
                ["resultado"] = passed ? "verificado" : "fallido",
                ["nivel_prueba_correspondencia"] = "inferred",   // emparejado por tipo y posición, no por id de origen
                // AUD-08: cada dimensión por separado. Este verificador NO observa el comando, el final del workflow ni el guardado.
                ["dimensiones"] = new Dictionary<string, object>
                {
                    ["consistencia_geometrica"] = (long)(matched.Count + unmonitored.Count),
                    ["supervision_al_vinculo"] = (long)matched.Count,
                    ["correspondencia_individual_origen"] = "no_demostrada: inferida por tipo y posición",
                    ["comando_iniciado"] = "fuera_del_verificador (recibo del flujo)",
                    ["workflow_finalizado"] = "fuera_del_verificador (recibo del flujo)",
                    ["guardado_confirmado"] = "fuera_del_verificador (recibo del flujo)",
                },
                ["vinculo_reflejado"] = tf.Mirrored,
                ["origen"] = (long)source.Count, ["esperados"] = (long)expected, ["creados"] = (long)created.Count,
                ["origen_por_tipo"] = bySource, ["creados_por_tipo"] = byHost,
                ["correctos"] = Bucket(matched), ["ausentes"] = Bucket(missing), ["tipo_incorrecto"] = Bucket(wrongType),
                ["sin_supervision"] = Bucket(unmonitored), ["mal_colocados"] = Bucket(misplaced), ["duplicados"] = Bucket(duplicates),
                ["inesperados"] = Bucket(unexpected), ["excluidos"] = Bucket(excluded),
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
