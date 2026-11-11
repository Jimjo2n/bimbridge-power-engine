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

    /// <summary>Transformación del vínculo en planta (rotación alrededor de Z + traslación), como la da RevitLinkInstance.GetTotalTransform().</summary>
    public sealed class LinkTransform
    {
        public double Dx, Dy, Dz, RotationDeg;
        public static LinkTransform Identity => new LinkTransform();

        public (double X, double Y, double Z) Apply(double x, double y, double z)
        {
            var r = RotationDeg * Math.PI / 180.0;
            return (x * Math.Cos(r) - y * Math.Sin(r) + Dx, x * Math.Sin(r) + y * Math.Cos(r) + Dy, z + Dz);
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
                var wantRot = Norm(s.RotationDeg + tf.RotationDeg);
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
