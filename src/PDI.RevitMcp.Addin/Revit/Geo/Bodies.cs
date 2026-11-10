// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Engine.Geo;

namespace PDI.RevitMcp.Addin.Revit.Geo
{
    /// <summary>
    /// Geometría real de un elemento: sólidos (recorriendo instancias anidadas de familia) y su triangulación orientada
    /// hacia afuera, en coordenadas del anfitrión. Sólo lee: no abre transacciones ni vistas.
    /// </summary>
    internal static class Bodies
    {
        /// <summary>Volumen mínimo (pies³) para considerar un sólido: descarta cáscaras abiertas y restos de 0 volumen.</summary>
        public const double MinSolidVolume = 1e-7;

        /// <summary>Nivel de detalle de la triangulación de caras curvas (las planas son exactas con cualquier valor).</summary>
        public const double TriangulationDetail = 0.5;

        public static Options NewOptions() => new Options
        {
            DetailLevel = ViewDetailLevel.Fine,
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
        };

        /// <summary>
        /// Junta los sólidos con volumen de un GeometryElement. Las instancias de familia ya vienen en coordenadas del modelo
        /// (GetInstanceGeometry). <paramref name="skipStyles"/>: subcategorías a excluir (p. ej. "Light Source").
        /// </summary>
        public static void CollectSolids(GeometryElement ge, List<Solid> into, Document doc, ICollection<string> skipStyles, ISet<string> parts = null, int depth = 0)
        {
            if (ge == null || depth > 8) return;
            foreach (var obj in ge)
            {
                switch (obj)
                {
                    case Solid s:
                        if (s.Faces.Size == 0) break;
                        double vol;
                        try { vol = s.Volume; } catch (Exception) { vol = 0; }
                        if (vol < MinSolidVolume) break;
                        string style = null;
                        if ((parts != null || (skipStyles != null && skipStyles.Count > 0)) && doc != null) style = StyleName(doc, s.GraphicsStyleId);
                        if (style != null && skipStyles != null && skipStyles.Count > 0 && IsSkipped(style, skipStyles)) break;
                        parts?.Add(style ?? NoStyle);
                        into.Add(s);
                        break;
                    case GeometryInstance gi:
                        GeometryElement inner;
                        try { inner = gi.GetInstanceGeometry(); } catch (Exception) { inner = null; }
                        CollectSolids(inner, into, doc, skipStyles, parts, depth + 1);
                        break;
                    case GeometryElement nested:
                        CollectSolids(nested, into, doc, skipStyles, parts, depth + 1);
                        break;
                }
            }
        }

        /// <summary>Nombre para sólidos sin subcategoría propia (heredan la de la categoría del elemento).</summary>
        public const string NoStyle = "(categoría)";

        private static string StyleName(Document doc, ElementId styleId)
        {
            if (styleId == null || styleId == ElementId.InvalidElementId) return null;
            try { return (doc.GetElement(styleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name; }
            catch (Exception) { return null; }
        }

        private static bool IsSkipped(string name, ICollection<string> skip)
        {
            foreach (var s in skip)
                if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Clave estable de las subcategorías excluidas (para saber si una malla guardada sirve).</summary>
        public static string SkipKey(ICollection<string> skip) =>
            skip == null || skip.Count == 0 ? "" : string.Join("|", skip.Select(x => x.Trim().ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal));

        /// <summary>
        /// Malla de un elemento (sólo caras no verticales), en coordenadas del anfitrión. <paramref name="toHost"/> = transformación
        /// del vínculo (null o identidad para el anfitrión). Devuelve TriMesh.Empty si no tiene sólidos.
        /// </summary>
        public static TriMesh MeshOf(Element e, Options opt, Transform toHost, ICollection<string> skipStyles, out int solids)
        {
            solids = 0;
            if (e == null) return TriMesh.Empty;
            var ge = e.get_Geometry(opt);
            if (ge == null) return TriMesh.Empty;
            var list = new List<Solid>();
            CollectSolids(ge, list, e.Document, skipStyles);
            solids = list.Count;
            if (list.Count == 0) return TriMesh.Empty;
            var b = new TriMeshBuilder();
            var identity = toHost == null || toHost.IsIdentity;
            foreach (var s in list) AddSolid(s, b, identity ? null : toHost);
            return b.Build();
        }

        /// <summary>Triangula cada cara y orienta cada triángulo con la normal hacia afuera de la cara.</summary>
        public static void AddSolid(Solid s, TriMeshBuilder b, Transform toHost)
        {
            foreach (Face f in s.Faces)
            {
                Mesh m;
                try { m = f.Triangulate(TriangulationDetail); } catch (Exception) { continue; }
                if (m == null || m.NumTriangles == 0) continue;

                XYZ planeNormal = (f as PlanarFace)?.FaceNormal;
                // Caras planas verticales: no aportan a una vertical; se saltan sin triangular más.
                if (planeNormal != null && Math.Abs(planeNormal.Z) < TriMeshBuilder.VerticalTolerance) continue;

                var normals = planeNormal == null ? SafeNormals(m) : 0;
                var dist = normals > 0 ? m.DistributionOfNormals : DistributionOfNormals.OnePerFace;
                bool? faceFlip = null; // caras curvas sin normales en la malla: una decisión por cara

                for (var i = 0; i < m.NumTriangles; i++)
                {
                    MeshTriangle t;
                    try { t = m.get_Triangle(i); } catch (Exception) { continue; }
                    var a = t.get_Vertex(0); var c1 = t.get_Vertex(1); var c2 = t.get_Vertex(2);
                    var wn = (c1 - a).CrossProduct(c2 - a);
                    XYZ reference;
                    if (planeNormal != null) reference = planeNormal;
                    else if (normals > 0) reference = MeshNormal(m, dist, t, i);
                    else
                    {
                        if (faceFlip == null) faceFlip = CurvedFaceFlip(f, a, c1, c2, wn);
                        reference = faceFlip.Value ? wn.Negate() : wn;
                    }
                    var flip = wn.DotProduct(reference) < 0;
                    var p0 = a; var p1 = flip ? c2 : c1; var p2 = flip ? c1 : c2;
                    if (toHost != null) { p0 = toHost.OfPoint(p0); p1 = toHost.OfPoint(p1); p2 = toHost.OfPoint(p2); }
                    b.Add(p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z);
                }
            }
        }

        private static int SafeNormals(Mesh m)
        {
            try { return m.NumberOfNormals; } catch (Exception) { return 0; }
        }

        private static XYZ MeshNormal(Mesh m, DistributionOfNormals dist, MeshTriangle t, int tri)
        {
            try
            {
                switch (dist)
                {
                    case DistributionOfNormals.AtEachPoint:
                        return m.GetNormal((int)t.get_Index(0)) + m.GetNormal((int)t.get_Index(1)) + m.GetNormal((int)t.get_Index(2));
                    case DistributionOfNormals.OnEachFacet:
                        return m.GetNormal(tri);
                    default:
                        return m.GetNormal(0);
                }
            }
            catch (Exception) { return XYZ.Zero; }
        }

        /// <summary>Cara curva sin normales en la malla: se compara el primer triángulo con la normal de la cara en su centro.</summary>
        private static bool CurvedFaceFlip(Face f, XYZ a, XYZ b, XYZ c, XYZ wn)
        {
            try
            {
                var centroid = (a + b + c) / 3.0;
                var pr = f.Project(centroid);
                if (pr == null) return false;
                var n = f.ComputeNormal(pr.UVPoint);
                return wn.DotProduct(n) < 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Unión de las cajas de los sólidos (más ajustada que la caja del elemento, que incluye anotaciones de familia).</summary>
        public static bool TryBox(IList<Solid> solids, out BoundingBoxXYZ box)
        {
            box = null;
            XYZ min = null, max = null;
            foreach (var s in solids)
            {
                BoundingBoxXYZ bb;
                try { bb = s.GetBoundingBox(); } catch (Exception) { continue; }
                if (bb == null) continue;
                var t = bb.Transform ?? Transform.Identity;
                for (var i = 0; i < 8; i++)
                {
                    var p = t.OfPoint(new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X, (i & 2) == 0 ? bb.Min.Y : bb.Max.Y, (i & 4) == 0 ? bb.Min.Z : bb.Max.Z));
                    min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                    max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                }
            }
            if (min == null) return false;
            box = new BoundingBoxXYZ { Min = min, Max = max };
            return true;
        }

        /// <summary>Eje de un muro en planta (coordenadas del anfitrión) como polilínea [x0,y0,x1,y1,…] y su espesor.</summary>
        public static bool WallLine(Element e, Transform toHost, out double[] line, out double width)
        {
            line = null; width = 0;
            if (!(e is Wall w)) return false;
            try { width = w.Width; } catch (Exception) { width = 0; }
            if (!(w.Location is LocationCurve lc) || lc.Curve == null) return false;
            IList<XYZ> pts;
            try { pts = lc.Curve is Line ln ? new List<XYZ> { ln.GetEndPoint(0), ln.GetEndPoint(1) } : lc.Curve.Tessellate(); }
            catch (Exception) { return false; }
            if (pts == null || pts.Count < 2) return false;
            line = new double[pts.Count * 2];
            for (var i = 0; i < pts.Count; i++)
            {
                var p = toHost != null && !toHost.IsIdentity ? toHost.OfPoint(pts[i]) : pts[i];
                line[2 * i] = p.X;
                line[2 * i + 1] = p.Y;
            }
            return true;
        }
    }
}
