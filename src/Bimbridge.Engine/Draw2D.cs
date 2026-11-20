// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bimbridge.Engine
{
    /// <summary>
    /// 0.8.0 (Package C): 2D geometry core shared by every drawing operation (lines, arcs, rectangles, regions,
    /// dimensions, detail components). A single implementation of validation, units and transforms, without Revit, so that no
    /// operation reinvents its logic and so it can be tested here.
    /// Spaces: MODEL (internal feet, 3D), VIEW (feet in the view plane: x to the right, y up) and PAPER (sheet feet).
    /// Paper ↔ view: view = paper × scale (scale 96 = 1/8" = 1'-0").
    /// </summary>
    public static class Draw2D
    {
        /// <summary>Revit rejects curves shorter than Application.ShortCurveTolerance (~0.00256 ft ≈ 0.78 mm). Validated before planning.</summary>
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

        /// <summary>0.9.0: a valid point has at least 2 finite coordinates (NaN/∞ used to reach Revit and fail mid-transaction).</summary>
        public static string ValidatePoint(double[] a, string label)
        {
            if (a == null || a.Length < 2) return label + ": coordinates [x, y] expected";
            if (double.IsNaN(a[0]) || double.IsNaN(a[1]) || double.IsInfinity(a[0]) || double.IsInfinity(a[1])) return label + ": non-finite coordinate";
            if (Math.Abs(a[0]) > MaxCoord || Math.Abs(a[1]) > MaxCoord) return label + ": coordinate outside Revit's range (±" + MaxCoord + " ft)";
            return null;
        }

        /// <summary>Revit warns about geometry more than ~20 miles from the origin; it is rejected earlier.</summary>
        public const double MaxCoord = 100000;
        /// <summary>Collinearity/contact tolerance (feet). Below ShortCurve: two segments closer than this touch.</summary>
        public const double Eps = 1e-7;

        /// <summary>Segments of a polyline (closed or not). Rejects short segments and consecutive repeated vertices.</summary>
        public static List<(P A, P B)> Segments(IList<double[]> pts, bool closed, out string error)
        {
            error = null;
            if (pts == null || pts.Count < 2) { error = "at least 2 points are needed"; return null; }
            if (pts.Count > MaxVertices) { error = "maximum " + MaxVertices + " vertices per operation"; return null; }
            if (closed && pts.Count < 3) { error = "a closed polyline needs at least 3 points"; return null; }
            var segs = new List<(P, P)>();
            var n = closed ? pts.Count : pts.Count - 1;
            for (var i = 0; i < pts.Count; i++) { var pe = ValidatePoint(pts[i], "point " + (i + 1)); if (pe != null) { error = pe; return null; } }
            for (var i = 0; i < n; i++)
            {
                var a = From(pts[i]); var b = From(pts[(i + 1) % pts.Count]);
                var len = (b - a).Len;
                if (len < ShortCurve) { error = "segment " + (i + 1) + " " + a + "→" + b + " is " + len.ToString("0.#####", CultureInfo.InvariantCulture) + " ft long: Revit does not accept curves that short"; return null; }
                segs.Add((a, b));
            }
            // 0.9.0: overlapping collinear segments (including the A→B→A' "spike" that doubles back on itself) produce duplicate lines
            // on top of each other: Revit creates them and then warns about overlapping lines. Rejected here, open or closed.
            for (var i = 0; i < segs.Count; i++)
                for (var j = i + 1; j < segs.Count; j++)
                    if (Classify(segs[i].Item1, segs[i].Item2, segs[j].Item1, segs[j].Item2) == Contact.Overlap)
                    { error = "segments " + (i + 1) + " and " + (j + 1) + " are collinear and overlap (duplicate lines)"; return null; }
            return segs;
        }

        /// <summary>Rectangle aligned to the view axes from two opposite corners, counterclockwise.</summary>
        public static List<double[]> Rect(double[] c0, double[] c1, out string error)
        {
            error = ValidatePoint(c0, "corner 1") ?? ValidatePoint(c1, "corner 2");
            if (error != null) return null;
            double x0 = Math.Min(c0[0], c1[0]), x1 = Math.Max(c0[0], c1[0]), y0 = Math.Min(c0[1], c1[1]), y1 = Math.Max(c0[1], c1[1]);
            if (x1 - x0 < ShortCurve || y1 - y0 < ShortCurve) { error = "the rectangle is too narrow (width and height must exceed " + ShortCurve + " ft)"; return null; }
            return new List<double[]> { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } };
        }

        /// <summary>Inscribed regular polygon (simple symbols: hexagons, triangles…).</summary>
        public static List<double[]> RegularPolygon(double[] center, double radius, int sides, double rotDeg, out string error)
        {
            error = ValidatePoint(center, "center");
            if (error != null) return null;
            if (double.IsNaN(radius) || double.IsInfinity(radius) || double.IsNaN(rotDeg) || double.IsInfinity(rotDeg)) { error = "radius and rotation must be finite"; return null; }
            if (sides < 3 || sides > 64) { error = "sides between 3 and 64"; return null; }
            if (radius <= ShortCurve) { error = "radius too small"; return null; }
            var pts = new List<double[]>();
            var r0 = rotDeg * Math.PI / 180.0;
            for (var i = 0; i < sides; i++)
            {
                var a = r0 + 2 * Math.PI * i / sides;
                pts.Add(new[] { center[0] + radius * Math.Cos(a), center[1] + radius * Math.Sin(a) });
            }
            return pts;
        }

        /// <summary>Validates an arc (center, radius, angles in degrees, counterclockwise from start to end).</summary>
        public static string ValidateArc(double radius, double startDeg, double endDeg)
        {
            if (double.IsNaN(startDeg) || double.IsNaN(endDeg) || double.IsInfinity(startDeg) || double.IsInfinity(endDeg) || double.IsInfinity(radius)) return "radius and angles must be finite";
            if (!(radius > ShortCurve)) return "radius must be greater than " + ShortCurve + " ft";
            var sweep = endDeg - startDeg;
            if (sweep <= 0 || sweep >= 360) return "the sweep (end − start) must be between 0° and 360° exclusive; for a circle use two arcs";
            var arcLen = radius * sweep * Math.PI / 180.0;
            if (arcLen < ShortCurve) return "the arc is too short for Revit";
            return null;
        }

        /// <summary>Signed area (positive = counterclockwise).</summary>
        public static double SignedArea(IList<double[]> pts)
        {
            double a2 = 0;
            for (var i = 0; i < pts.Count; i++) { var a = pts[i]; var b = pts[(i + 1) % pts.Count]; a2 += a[0] * b[1] - b[0] * a[1]; }
            return a2 / 2;
        }

        /// <summary>A filled region boundary: closed, with area and no self-intersections (Revit would reject it mid-transaction).</summary>
        public static string ValidateLoop(IList<double[]> pts)
        {
            var segs = Segments(pts, true, out var err);
            if (segs == null) return err;
            if (Math.Abs(SignedArea(pts)) < 1e-6) return "the polygon has no area";
            for (var i = 0; i < segs.Count; i++)
                for (var j = i + 1; j < segs.Count; j++)
                {
                    if (j == i + 1 || (i == 0 && j == segs.Count - 1)) continue;   // adjacent segments share a vertex (Segments already rejects their overlap)
                    // 0.9.0: a proper crossing is not the only case; a vertex that TOUCHES another segment ("8"-shaped boundary or pinched loop) is also invalid.
                    if (Classify(segs[i].A, segs[i].B, segs[j].A, segs[j].B) != Contact.None) return "the boundary crosses or touches itself (segments " + (i + 1) + " and " + (j + 1) + ")";
                }
            return null;
        }

        public enum Contact { None, Cross, Touch, Overlap }

        /// <summary>
        /// 0.9.0: robust classification of two segments: no contact, proper crossing, contact at one point (an endpoint on the other
        /// segment) or collinear overlap with length. Tolerance <see cref="Eps"/> relative to the scale of the segments.
        /// </summary>
        public static Contact Classify(P p1, P p2, P q1, P q2)
        {
            var scale = Math.Max(1.0, Math.Max(Math.Max(Math.Max(Math.Abs(p1.X), Math.Abs(p1.Y)), Math.Max(Math.Abs(p2.X), Math.Abs(p2.Y))),
                                                Math.Max(Math.Max(Math.Abs(q1.X), Math.Abs(q1.Y)), Math.Max(Math.Abs(q2.X), Math.Abs(q2.Y)))));
            var lp = (p2 - p1).Len; var lq = (q2 - q1).Len;
            var tol = Eps * scale;
            // signed distances normalized by length (= perpendicular distance)
            double d1 = Cross(q1, q2, p1) / lq, d2 = Cross(q1, q2, p2) / lq, d3 = Cross(p1, p2, q1) / lp, d4 = Cross(p1, p2, q2) / lp;
            bool z1 = Math.Abs(d1) <= tol, z2 = Math.Abs(d2) <= tol, z3 = Math.Abs(d3) <= tol, z4 = Math.Abs(d4) <= tol;
            if (z1 && z2)
            {
                // collinear: project onto the direction of p and measure the overlap
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
            // an endpoint on the other segment
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

        // ---------------- paper ↔ view ----------------

        /// <summary>A paper measurement (sheet feet) in view feet for a Revit scale (96 = 1/8").</summary>
        public static double PaperToView(double paperFeet, int scale) => paperFeet * Math.Max(1, scale);
        public static double ViewToPaper(double viewFeet, int scale) => viewFeet / Math.Max(1, scale);

        /// <summary>View point → point on the sheet, given the viewport center on the sheet and the view center (view feet).</summary>
        public static P ViewToSheet(P viewPt, P viewCenter, P viewportCenterOnSheet, int scale)
            => viewportCenterOnSheet + (viewPt - viewCenter) * (1.0 / Math.Max(1, scale));

        public static P SheetToView(P sheetPt, P viewCenter, P viewportCenterOnSheet, int scale)
            => viewCenter + (sheetPt - viewportCenterOnSheet) * Math.Max(1, scale);

        /// <summary>2D transform (rotation + translation) used by move/rotate/align on view elements.</summary>
        public static P Rotate(P p, P pivot, double deg)
        {
            var a = deg * Math.PI / 180.0; var c = Math.Cos(a); var s = Math.Sin(a);
            var d = p - pivot;
            return new P(pivot.X + d.X * c - d.Y * s, pivot.Y + d.X * s + d.Y * c);
        }

        /// <summary>Offset that aligns a box (min,max) to a reference: izquierda|derecha|centro_x|arriba|abajo|centro_y.</summary>
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
                default: throw new ArgumentException("alinear: izquierda, derecha, centro_x, arriba, abajo or centro_y");
            }
        }

        /// <summary>Distributes n boxes along an axis with constant spacing between edges (arranging annotations/symbols).</summary>
        public static List<double> Distribute(IList<(double Min, double Max)> boxes, double start, double gap)
        {
            var res = new List<double>();
            var cursor = start;
            foreach (var b in boxes) { res.Add(cursor - b.Min); cursor += (b.Max - b.Min) + gap; }
            return res;
        }
    }
}
