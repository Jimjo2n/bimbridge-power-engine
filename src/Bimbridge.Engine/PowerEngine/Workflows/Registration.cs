// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BimBridge.PowerEngine.Workflows
{
    /// <summary>Un ancla: el mismo punto en la página (pt, espacio visible) y en el modelo (pies internos).</summary>
    public sealed class Anchor
    {
        public string Name;
        public double Px, Py;
        public double Mx, My;
        public Anchor() { }
        public Anchor(string name, double px, double py, double mx, double my) { Name = name; Px = px; Py = py; Mx = mx; My = my; }
    }

    public sealed class RegistrationOptions
    {
        /// <summary>"semejanza" (escala uniforme + giro + traslación; lo normal en un plano a escala) o "afin" (6 parámetros).</summary>
        public string Mode = "semejanza";
        /// <summary>Residuo máximo aceptable por ancla, en pies (por defecto 3").</summary>
        public double TolFt = 0.25;
        /// <summary>Escala de Revit esperada (96 = 1/8"=1'-0") leída del cajetín; null = no se contrasta.</summary>
        public double? ExpectedRevitScale;
        public double ScaleTolerance = 0.02;
        /// <summary>Unidad de usuario del PDF (pt de página → pt reales).</summary>
        public double UserUnit = 1.0;
        public bool AllowReflection;
        /// <summary>Transformación del vínculo (modelo del vínculo → anfitrión), 3×4; null = el plano se registra contra el anfitrión.</summary>
        public double[] LinkTransform;
    }

    public sealed class AnchorResidual
    {
        public string Name; public double ResidualFt; public bool Used; public bool Outlier;
        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        { ["ancla"] = Name, ["residuo_ft"] = Math.Round(ResidualFt, 5), ["usada"] = Used, ["invalida"] = Outlier };
    }

    public sealed class RegistrationResult
    {
        public string Estado = "ok", Motivo;
        /// <summary>Página (pt, visible) → modelo (pies) como 3×4 (z se conserva). Incluye el vínculo si se dio.</summary>
        public double[] M;
        public double FtPerPt, RotationDeg, Tx, Ty, RevitScale;
        public bool Reflected;
        public double RmsFt, MaxFt;
        public List<AnchorResidual> Anchors = new List<AnchorResidual>();
        public List<string> Warnings = new List<string>();
        public bool Ok => Estado == "ok";

        public (double X, double Y) Map(double px, double py) => (M[0] * px + M[1] * py + M[3], M[4] * px + M[5] * py + M[7]);

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["estado"] = Estado, ["motivo"] = Motivo, ["matriz"] = M?.Select(v => (object)Math.Round(v, 12)).ToList(),
            ["pies_por_pt"] = Math.Round(FtPerPt, 9), ["escala_revit"] = Math.Round(RevitScale, 3), ["giro_deg"] = Math.Round(RotationDeg, 4),
            ["traslacion_ft"] = new List<object> { Math.Round(Tx, 5), Math.Round(Ty, 5) }, ["reflejado"] = Reflected,
            ["rms_ft"] = Math.Round(RmsFt, 5), ["max_ft"] = Math.Round(MaxFt, 5), ["anclas"] = Anchors.Select(a => (object)a.ToJson()).ToList(),
            ["avisos"] = Warnings.Cast<object>().ToList(),
        };
    }

    /// <summary>
    /// Registro plano–modelo. Calcula, por mínimos cuadrados, la transformación de puntos de página (pt, espacio visible del PDF) a
    /// coordenadas del modelo (pies internos), mide el residuo de cada ancla, descarta anclas inválidas (una a una, la peor, mientras
    /// queden más de las mínimas), comprueba la escala contra la del cajetín y compone con la transformación del vínculo.
    /// Regla: un punto de página nunca se usa como coordenada de Revit sin pasar por aquí.
    /// </summary>
    public static class Registration
    {
        public const double PtPerInch = 72.0;

        /// <summary>Pies de modelo por punto de papel a una escala de Revit (96 → 1 pt = 96/864 ft).</summary>
        public static double FtPerPtAtScale(double revitScale, double userUnit = 1.0) => userUnit * revitScale / (PtPerInch * 12.0);

        public static RegistrationResult Solve(IList<Anchor> anchors, RegistrationOptions o = null)
        {
            o = o ?? new RegistrationOptions();
            var affine = string.Equals(o.Mode, "afin", StringComparison.OrdinalIgnoreCase);
            var min = affine ? 3 : 2;
            var res = new RegistrationResult();
            var list = (anchors ?? new List<Anchor>()).Where(a => a != null).ToList();
            foreach (var a in list)
                if (!Finite(a.Px, a.Py, a.Mx, a.My)) return Fail(res, "ancla_invalida", "el ancla " + a.Name + " tiene coordenadas no finitas");
            var dupNames = list.GroupBy(a => a.Name ?? "").Where(g => g.Count() > 1 && g.Key.Length > 0).Select(g => g.Key).ToList();
            if (dupNames.Count > 0) return Fail(res, "ancla_invalida", "anclas repetidas: " + string.Join(", ", dupNames));
            if (list.Count < min) return Fail(res, "anclas_insuficientes", "hacen falta al menos " + min + " anclas (" + o.Mode + "); hay " + list.Count);

            var used = new List<Anchor>(list);
            var outliers = new HashSet<Anchor>();
            double[] m = null;
            bool refl = false;
            while (true)
            {
                string err;
                m = affine ? FitAffine(used, out err) : FitSimilarity(used, o.AllowReflection, out refl, out err);
                if (m == null) return Fail(res, "degenerado", err);
                var worst = used.OrderByDescending(a => Residual(m, a)).First();
                var wr = Residual(m, worst);
                // con el mínimo exacto de anclas el residuo es 0 por construcción: no se puede detectar un ancla mala
                if (wr <= o.TolFt || used.Count <= min + 1) break;
                used.Remove(worst); outliers.Add(worst);
            }
            res.Reflected = refl;
            foreach (var a in list)
                res.Anchors.Add(new AnchorResidual { Name = a.Name, ResidualFt = Residual(m, a), Used = used.Contains(a), Outlier = outliers.Contains(a) });
            var usedRes = res.Anchors.Where(a => a.Used).Select(a => a.ResidualFt).ToList();
            res.RmsFt = Math.Sqrt(usedRes.Sum(r => r * r) / usedRes.Count);
            res.MaxFt = usedRes.Max();
            var sx = Math.Sqrt(m[0] * m[0] + m[4] * m[4]); var sy = Math.Sqrt(m[1] * m[1] + m[5] * m[5]);
            res.FtPerPt = (sx + sy) / 2.0;
            res.RevitScale = res.FtPerPt * PtPerInch * 12.0 / o.UserUnit;
            res.RotationDeg = Math.Atan2(m[4], m[0]) * 180.0 / Math.PI;
            res.Tx = m[3]; res.Ty = m[7];
            if (affine && Math.Abs(sx - sy) / Math.Max(sx, sy) > 0.01) res.Warnings.Add("escala distinta en x e y (" + (sx / sy).ToString("0.####", CultureInfo.InvariantCulture) + "): plano deformado o anclas mal emparejadas");
            if (used.Count == min) res.Warnings.Add("sólo " + min + " anclas: sin redundancia, un ancla mala no se detectaría");
            if (outliers.Count > 0) res.Warnings.Add("anclas inválidas descartadas: " + string.Join(", ", outliers.Select(a => a.Name)));
            if (refl) res.Warnings.Add("la mejor solución es un reflejo: la página o el modelo están en espejo");

            if (res.MaxFt > o.TolFt) Fail(res, "residuo_excesivo", "residuo máximo " + res.MaxFt.ToString("0.###", CultureInfo.InvariantCulture) + " ft > " + o.TolFt.ToString("0.###", CultureInfo.InvariantCulture) + " ft con " + used.Count + " anclas");
            else if (o.ExpectedRevitScale.HasValue && Math.Abs(res.RevitScale / o.ExpectedRevitScale.Value - 1) > o.ScaleTolerance)
                Fail(res, "escala_incoherente", "escala medida 1:" + res.RevitScale.ToString("0.##", CultureInfo.InvariantCulture) + " frente a 1:" + o.ExpectedRevitScale.Value.ToString("0.##", CultureInfo.InvariantCulture) + " del cajetín: anclas mal emparejadas o lámina no a escala");
            res.M = o.LinkTransform != null ? BimBridge.PowerEngine.GeometryKernels.Compose(o.LinkTransform, m) : m;
            return res;
        }

        /// <summary>Verificación con anclas que NO se usaron para calcular (control independiente).</summary>
        public static List<AnchorResidual> Check(RegistrationResult r, IEnumerable<Anchor> control) =>
            control.Select(a => new AnchorResidual { Name = a.Name, ResidualFt = Residual(r.M, a), Used = false }).ToList();

        /// <summary>Empareja intersecciones de ejes de la página y del modelo por nombre ("A/1"). Sólo nombres presentes en ambos.</summary>
        public static List<Anchor> AnchorsByName(IDictionary<string, (double X, double Y)> page, IDictionary<string, (double X, double Y)> model) =>
            page.Keys.Where(model.ContainsKey).OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => new Anchor(k, page[k].X, page[k].Y, model[k].X, model[k].Y)).ToList();

        /// <summary>Intersecciones con nombre de ejes del modelo (cada eje: nombre y segmento en pies). Nombre "LETRA/NÚMERO".</summary>
        public static Dictionary<string, (double X, double Y)> GridIntersections(IEnumerable<(string Name, double X0, double Y0, double X1, double Y1)> grids, double minAngleDeg = 20)
        {
            var g = grids.ToList();
            var r = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
            for (var i = 0; i < g.Count; i++)
                for (var j = i + 1; j < g.Count; j++)
                {
                    double d1x = g[i].X1 - g[i].X0, d1y = g[i].Y1 - g[i].Y0, d2x = g[j].X1 - g[j].X0, d2y = g[j].Y1 - g[j].Y0;
                    var den = d1x * d2y - d1y * d2x;
                    var n1 = Math.Sqrt(d1x * d1x + d1y * d1y); var n2 = Math.Sqrt(d2x * d2x + d2y * d2y);
                    if (Math.Abs(den) < Math.Sin(minAngleDeg * Math.PI / 180) * n1 * n2) continue;
                    var t = ((g[j].X0 - g[i].X0) * d2y - (g[j].Y0 - g[i].Y0) * d2x) / den;
                    var names = new[] { g[i].Name, g[j].Name }.OrderBy(n => n.Length > 0 && char.IsLetter(n[0]) ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal).ToArray();
                    r[names[0] + "/" + names[1]] = (g[i].X0 + t * d1x, g[i].Y0 + t * d1y);
                }
            return r;
        }

        // ---------------- ajustes ----------------

        private static double[] FitSimilarity(List<Anchor> a, bool allowReflection, out bool reflected, out string err)
        {
            reflected = false;
            var m = Similarity(a, false, out err);
            if (m == null || !allowReflection) return m;
            var mr = Similarity(a, true, out _);
            if (mr != null && a.Sum(x => Residual(mr, x)) < a.Sum(x => Residual(m, x)) * 0.5) { reflected = true; return mr; }
            return m;
        }

        /// <summary>Umeyama 2D: escala uniforme, giro y traslación de mínimos cuadrados (forma cerrada).</summary>
        private static double[] Similarity(List<Anchor> a, bool reflect, out string err)
        {
            err = null;
            var n = a.Count;
            double pcx = a.Average(x => x.Px), pcy = a.Average(x => reflect ? -x.Py : x.Py), mcx = a.Average(x => x.Mx), mcy = a.Average(x => x.My);
            double sa = 0, sb = 0, sp = 0, sm = 0;
            foreach (var x in a)
            {
                double dx = x.Px - pcx, dy = (reflect ? -x.Py : x.Py) - pcy, ex = x.Mx - mcx, ey = x.My - mcy;
                sa += dx * ex + dy * ey; sb += dx * ey - dy * ex; sp += dx * dx + dy * dy; sm += ex * ex + ey * ey;
            }
            if (sp < 1e-6 * n || sm < 1e-9 * n) { err = "las anclas coinciden (sin separación en la página o en el modelo): no definen escala ni giro"; return null; }
            double c = sa / sp, s = sb / sp;
            var f = reflect ? -1.0 : 1.0;
            // x' = c·px − s·(f·py) + tx ; y' = s·px + c·(f·py) + ty
            var tx = mcx - (c * pcx - s * pcy); var ty = mcy - (s * pcx + c * pcy);
            return new[] { c, -s * f, 0, tx, s, c * f, 0, ty, 0, 0, 1, 0 };
        }

        private static double[] FitAffine(List<Anchor> a, out string err)
        {
            err = null;
            // normales: [Σxx Σxy Σx; Σxy Σyy Σy; Σx Σy n]·[a b c] = [Σx·X Σy·X ΣX]
            double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, n = a.Count, sxX = 0, syX = 0, sX = 0, sxY = 0, syY = 0, sY = 0;
            foreach (var p in a)
            {
                sxx += p.Px * p.Px; sxy += p.Px * p.Py; syy += p.Py * p.Py; sx += p.Px; sy += p.Py;
                sxX += p.Px * p.Mx; syX += p.Py * p.Mx; sX += p.Mx; sxY += p.Px * p.My; syY += p.Py * p.My; sY += p.My;
            }
            var A = new[,] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
            var det = Det3(A);
            var scale = Math.Max(1, sxx + syy);
            if (Math.Abs(det) < 1e-9 * scale * scale * n) { err = "anclas alineadas: un ajuste afín necesita 3 anclas no colineales"; return null; }
            var r1 = Solve3(A, new[] { sxX, syX, sX }); var r2 = Solve3(A, new[] { sxY, syY, sY });
            return new[] { r1[0], r1[1], 0, r1[2], r2[0], r2[1], 0, r2[2], 0, 0, 1, 0 };
        }

        private static double Det3(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        private static double[] Solve3(double[,] m, double[] b)
        {
            var d = Det3(m);
            var r = new double[3];
            for (var k = 0; k < 3; k++)
            {
                var c = (double[,])m.Clone();
                for (var i = 0; i < 3; i++) c[i, k] = b[i];
                r[k] = Det3(c) / d;
            }
            return r;
        }

        public static double Residual(double[] m, Anchor a)
        {
            var x = m[0] * a.Px + m[1] * a.Py + m[3]; var y = m[4] * a.Px + m[5] * a.Py + m[7];
            return Math.Sqrt((x - a.Mx) * (x - a.Mx) + (y - a.My) * (y - a.My));
        }

        private static bool Finite(params double[] v) => v.All(x => !double.IsNaN(x) && !double.IsInfinity(x));

        private static RegistrationResult Fail(RegistrationResult r, string estado, string motivo) { r.Estado = estado; r.Motivo = motivo; return r; }
    }
}
