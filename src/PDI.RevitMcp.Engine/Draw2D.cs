// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// 0.8.0 (Paquete C): núcleo geométrico 2D compartido por todas las operaciones de dibujo (líneas, arcos, rectángulos, regiones,
    /// cotas, componentes de detalle). Una sola implementación de validación, unidades y transformaciones, sin Revit, para que cada
    /// operación no reinvente su lógica y para poder probarla aquí.
    /// Espacios: MODELO (pies internos, 3D), VISTA (pies en el plano de la vista: x a la derecha, y hacia arriba) y PAPEL (pies de lámina).
    /// Papel ↔ vista: vista = papel × escala (escala 96 = 1/8" = 1'-0").
    /// </summary>
    public static class Draw2D
    {
        /// <summary>Revit rechaza curvas más cortas que Application.ShortCurveTolerance (~0.00256 ft ≈ 0.78 mm). Se valida antes de planificar.</summary>
        public const double ShortCurve = 0.0026;
        public const int MaxVertices = 2000;

        public struct P
        {
            public double X, Y;
            public P(double x, double y) { X = x; Y = y; }
            public static P operator -(P a, P b) => new P(a.X - b.X, a.Y - b.Y);
            public static P operator +(P a, P b) => new P(a.X + b.X, a.Y + b.Y);
            public static P operator *(P a, double k) => new P(a.X * k, a.Y * k);
            public double Len => Math.Sqrt(X * X + Y * Y);
            public override string ToString() => "(" + X.ToString("0.####", CultureInfo.InvariantCulture) + ", " + Y.ToString("0.####", CultureInfo.InvariantCulture) + ")";
        }

        public static P From(double[] a) => new P(a[0], a[1]);

        /// <summary>0.9.0: un punto válido tiene al menos 2 coordenadas finitas (NaN/∞ llegaban a Revit y fallaban a mitad de transacción).</summary>
        public static string ValidatePoint(double[] a, string label)
        {
            if (a == null || a.Length < 2) return label + ": se esperan coordenadas [x, y]";
            if (double.IsNaN(a[0]) || double.IsNaN(a[1]) || double.IsInfinity(a[0]) || double.IsInfinity(a[1])) return label + ": coordenada no finita";
            if (Math.Abs(a[0]) > MaxCoord || Math.Abs(a[1]) > MaxCoord) return label + ": coordenada fuera del rango de Revit (±" + MaxCoord + " ft)";
            return null;
        }

        /// <summary>Revit avisa con geometría a más de ~20 millas del origen; se rechaza antes.</summary>
        public const double MaxCoord = 100000;
        /// <summary>Tolerancia de colinealidad/contacto (pies). Por debajo de ShortCurve: dos tramos a menos de esto se tocan.</summary>
        public const double Eps = 1e-7;

        /// <summary>Segmentos de una polilínea (cerrada o no). Rechaza tramos cortos y vértices repetidos consecutivos.</summary>
        public static List<(P A, P B)> Segments(IList<double[]> pts, bool closed, out string error)
        {
            error = null;
            if (pts == null || pts.Count < 2) { error = "se necesitan al menos 2 puntos"; return null; }
            if (pts.Count > MaxVertices) { error = "máximo " + MaxVertices + " vértices por operación"; return null; }
            if (closed && pts.Count < 3) { error = "una polilínea cerrada necesita al menos 3 puntos"; return null; }
            var segs = new List<(P, P)>();
            var n = closed ? pts.Count : pts.Count - 1;
            for (var i = 0; i < pts.Count; i++) { var pe = ValidatePoint(pts[i], "punto " + (i + 1)); if (pe != null) { error = pe; return null; } }
            for (var i = 0; i < n; i++)
            {
                var a = From(pts[i]); var b = From(pts[(i + 1) % pts.Count]);
                var len = (b - a).Len;
                if (len < ShortCurve) { error = "el tramo " + (i + 1) + " " + a + "→" + b + " mide " + len.ToString("0.#####", CultureInfo.InvariantCulture) + " ft: Revit no admite curvas tan cortas"; return null; }
                segs.Add((a, b));
            }
            // 0.9.0: tramos colineales superpuestos (también el "pico" A→B→A' que vuelve sobre sí mismo) producen líneas duplicadas
            // encima unas de otras: Revit las crea y luego avisa de líneas superpuestas. Se rechazan aquí, abierta o cerrada.
            for (var i = 0; i < segs.Count; i++)
                for (var j = i + 1; j < segs.Count; j++)
                    if (Classify(segs[i].Item1, segs[i].Item2, segs[j].Item1, segs[j].Item2) == Contact.Overlap)
                    { error = "los tramos " + (i + 1) + " y " + (j + 1) + " son colineales y se superponen (líneas duplicadas)"; return null; }
            return segs;
        }

        /// <summary>Rectángulo alineado a los ejes de la vista a partir de dos esquinas opuestas, en sentido antihorario.</summary>
        public static List<double[]> Rect(double[] c0, double[] c1, out string error)
        {
            error = ValidatePoint(c0, "esquina 1") ?? ValidatePoint(c1, "esquina 2");
            if (error != null) return null;
            double x0 = Math.Min(c0[0], c1[0]), x1 = Math.Max(c0[0], c1[0]), y0 = Math.Min(c0[1], c1[1]), y1 = Math.Max(c0[1], c1[1]);
            if (x1 - x0 < ShortCurve || y1 - y0 < ShortCurve) { error = "el rectángulo es demasiado estrecho (ancho y alto deben superar " + ShortCurve + " ft)"; return null; }
            return new List<double[]> { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } };
        }

        /// <summary>Polígono regular inscrito (símbolos simples: hexágonos, triángulos…).</summary>
        public static List<double[]> RegularPolygon(double[] center, double radius, int sides, double rotDeg, out string error)
        {
            error = ValidatePoint(center, "centro");
            if (error != null) return null;
            if (double.IsNaN(radius) || double.IsInfinity(radius) || double.IsNaN(rotDeg) || double.IsInfinity(rotDeg)) { error = "radio y rotación deben ser finitos"; return null; }
            if (sides < 3 || sides > 64) { error = "lados entre 3 y 64"; return null; }
            if (radius <= ShortCurve) { error = "radio demasiado pequeño"; return null; }
            var pts = new List<double[]>();
            var r0 = rotDeg * Math.PI / 180.0;
            for (var i = 0; i < sides; i++)
            {
                var a = r0 + 2 * Math.PI * i / sides;
                pts.Add(new[] { center[0] + radius * Math.Cos(a), center[1] + radius * Math.Sin(a) });
            }
            return pts;
        }

        /// <summary>Valida un arco (centro, radio, ángulos en grados, antihorario de inicio a fin).</summary>
        public static string ValidateArc(double radius, double startDeg, double endDeg)
        {
            if (double.IsNaN(startDeg) || double.IsNaN(endDeg) || double.IsInfinity(startDeg) || double.IsInfinity(endDeg) || double.IsInfinity(radius)) return "radio y ángulos deben ser finitos";
            if (!(radius > ShortCurve)) return "radio debe ser mayor que " + ShortCurve + " ft";
            var sweep = endDeg - startDeg;
            if (sweep <= 0 || sweep >= 360) return "el barrido (fin − inicio) debe estar entre 0° y 360° exclusivos; para un círculo use dos arcos";
            var arcLen = radius * sweep * Math.PI / 180.0;
            if (arcLen < ShortCurve) return "el arco es demasiado corto para Revit";
            return null;
        }

        /// <summary>Área con signo (positiva = antihorario).</summary>
        public static double SignedArea(IList<double[]> pts)
        {
            double a2 = 0;
            for (var i = 0; i < pts.Count; i++) { var a = pts[i]; var b = pts[(i + 1) % pts.Count]; a2 += a[0] * b[1] - b[0] * a[1]; }
            return a2 / 2;
        }

        /// <summary>Un contorno de región rellena: cerrado, con área y sin autointersecciones (Revit lo rechazaría a mitad de transacción).</summary>
        public static string ValidateLoop(IList<double[]> pts)
        {
            var segs = Segments(pts, true, out var err);
            if (segs == null) return err;
            if (Math.Abs(SignedArea(pts)) < 1e-6) return "el polígono no tiene área";
            for (var i = 0; i < segs.Count; i++)
                for (var j = i + 1; j < segs.Count; j++)
                {
                    if (j == i + 1 || (i == 0 && j == segs.Count - 1)) continue;   // adyacentes comparten vértice (su superposición ya la rechaza Segments)
                    // 0.9.0: no basta el cruce propio; un vértice que TOCA otro tramo (contorno en "8" o bucle pellizcado) también es inválido.
                    if (Classify(segs[i].A, segs[i].B, segs[j].A, segs[j].B) != Contact.None) return "el contorno se cruza o se toca consigo mismo (tramos " + (i + 1) + " y " + (j + 1) + ")";
                }
            return null;
        }

        public enum Contact { None, Cross, Touch, Overlap }

        /// <summary>
        /// 0.9.0: clasificación robusta de dos segmentos: sin contacto, cruce propio, contacto en un punto (extremo sobre el otro
        /// tramo) o superposición colineal con longitud. Tolerancia <see cref="Eps"/> relativa a la escala de los tramos.
        /// </summary>
        public static Contact Classify(P p1, P p2, P q1, P q2)
        {
            var scale = Math.Max(1.0, Math.Max(Math.Max(Math.Max(Math.Abs(p1.X), Math.Abs(p1.Y)), Math.Max(Math.Abs(p2.X), Math.Abs(p2.Y))),
                                                Math.Max(Math.Max(Math.Abs(q1.X), Math.Abs(q1.Y)), Math.Max(Math.Abs(q2.X), Math.Abs(q2.Y)))));
            var lp = (p2 - p1).Len; var lq = (q2 - q1).Len;
            var tol = Eps * scale;
            // distancias firmadas normalizadas por la longitud (=distancia perpendicular)
            double d1 = Cross(q1, q2, p1) / lq, d2 = Cross(q1, q2, p2) / lq, d3 = Cross(p1, p2, q1) / lp, d4 = Cross(p1, p2, q2) / lp;
            bool z1 = Math.Abs(d1) <= tol, z2 = Math.Abs(d2) <= tol, z3 = Math.Abs(d3) <= tol, z4 = Math.Abs(d4) <= tol;
            if (z1 && z2)
            {
                // colineales: proyectar sobre la dirección de p y medir el solape
                var ux = (p2.X - p1.X) / lp; var uy = (p2.Y - p1.Y) / lp;
                double a0 = 0, a1 = lp;
                double b0 = (q1.X - p1.X) * ux + (q1.Y - p1.Y) * uy, b1 = (q2.X - p1.X) * ux + (q2.Y - p1.Y) * uy;
                if (b0 > b1) { var t = b0; b0 = b1; b1 = t; }
                var overlap = Math.Min(a1, b1) - Math.Max(a0, b0);
                if (overlap > tol) return Contact.Overlap;
                if (overlap >= -tol) return Contact.Touch;
                return Contact.None;
            }
            if (((d1 > tol && d2 < -tol) || (d1 < -tol && d2 > tol)) && ((d3 > tol && d4 < -tol) || (d3 < -tol && d4 > tol))) return Contact.Cross;
            // un extremo sobre el otro tramo
            if ((z1 && OnSeg(q1, q2, p1, tol)) || (z2 && OnSeg(q1, q2, p2, tol)) || (z3 && OnSeg(p1, p2, q1, tol)) || (z4 && OnSeg(p1, p2, q2, tol))) return Contact.Touch;
            return Contact.None;
        }

        private static bool OnSeg(P a, P b, P p, double tol) =>
            p.X >= Math.Min(a.X, b.X) - tol && p.X <= Math.Max(a.X, b.X) + tol && p.Y >= Math.Min(a.Y, b.Y) - tol && p.Y <= Math.Max(a.Y, b.Y) + tol;

        private static double Cross(P o, P a, P b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        public static bool Intersect(P p1, P p2, P q1, P q2)
        {
            double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        // ---------------- papel ↔ vista ----------------

        /// <summary>Una medida de papel (pies de lámina) en pies de vista para una escala Revit (96 = 1/8").</summary>
        public static double PaperToView(double paperFeet, int scale) => paperFeet * Math.Max(1, scale);
        public static double ViewToPaper(double viewFeet, int scale) => viewFeet / Math.Max(1, scale);

        /// <summary>Punto de vista → punto en la lámina, dado el centro del viewport en la lámina y el centro de la vista (pies de vista).</summary>
        public static P ViewToSheet(P viewPt, P viewCenter, P viewportCenterOnSheet, int scale)
            => viewportCenterOnSheet + (viewPt - viewCenter) * (1.0 / Math.Max(1, scale));

        public static P SheetToView(P sheetPt, P viewCenter, P viewportCenterOnSheet, int scale)
            => viewCenter + (sheetPt - viewportCenterOnSheet) * Math.Max(1, scale);

        /// <summary>Transformación 2D (rotación + traslación) que usan move/rotate/align sobre elementos de vista.</summary>
        public static P Rotate(P p, P pivot, double deg)
        {
            var a = deg * Math.PI / 180.0; var c = Math.Cos(a); var s = Math.Sin(a);
            var d = p - pivot;
            return new P(pivot.X + d.X * c - d.Y * s, pivot.Y + d.X * s + d.Y * c);
        }

        /// <summary>Desplazamiento que alinea una caja (min,max) a una referencia: izquierda|derecha|centro_x|arriba|abajo|centro_y.</summary>
        public static P AlignDelta(P min, P max, double reference, string mode)
        {
            switch (mode)
            {
                case "izquierda": return new P(reference - min.X, 0);
                case "derecha": return new P(reference - max.X, 0);
                case "centro_x": return new P(reference - (min.X + max.X) / 2, 0);
                case "abajo": return new P(0, reference - min.Y);
                case "arriba": return new P(0, reference - max.Y);
                case "centro_y": return new P(0, reference - (min.Y + max.Y) / 2);
                default: throw new ArgumentException("alinear: izquierda, derecha, centro_x, arriba, abajo o centro_y");
            }
        }

        /// <summary>Reparte n cajas a lo largo de un eje con separación constante entre bordes (organizar anotaciones/símbolos).</summary>
        public static List<double> Distribute(IList<(double Min, double Max)> boxes, double start, double gap)
        {
            var res = new List<double>();
            var cursor = start;
            foreach (var b in boxes) { res.Add(cursor - b.Min); cursor += (b.Max - b.Min) + gap; }
            return res;
        }
    }
}
