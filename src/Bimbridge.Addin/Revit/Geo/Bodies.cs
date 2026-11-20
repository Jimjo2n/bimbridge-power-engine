// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Bimbridge.Engine.Geo;

namespace Bimbridge.Addin.Revit.Geo
{
    /// <summary>
    /// Actual geometry of an element: solids (walking nested family instances) and their outward-oriented
    /// triangulation, in host coordinates. Read-only: opens no transactions or views.
    /// </summary>
    internal static class Bodies
    {
        /// <summary>Minimum volume (ft³) for a solid to count: discards open shells and zero-volume leftovers.</summary>
        public const double MinSolidVolume = 1e-7;

        /// <summary>Triangulation detail level for curved faces (planar faces are exact at any value).</summary>
        public const double TriangulationDetail = 0.5;

        public static Options NewOptions() => new Options
        {
            DetailLevel = ViewDetailLevel.Fine,
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
        };

        /// <summary>
        /// Collects the solids with volume from a GeometryElement. Family instances already come in model coordinates
        /// (GetInstanceGeometry). <paramref name="skipStyles"/>: subcategories to exclude (e.g. "Light Source").
        /// </summary>
        public static void CollectSolids(GeometryElement ge, List<Solid> into, Document doc, ICollection<string> skipStyles, ISet<string> parts = null, int depth = 0, Action onFailure = null)
        {
            if (ge == null) return;
            if (depth > 8) { onFailure?.Invoke(); return; }
            foreach (var obj in ge)
            {
                switch (obj)
                {
                    case Solid s:
                        if (s.Faces.Size == 0) break;
                        double vol;
                        try { vol = s.Volume; } catch (Exception) { onFailure?.Invoke(); break; }
                        if (vol < MinSolidVolume) break;
                        string style = null;
                        if ((parts != null || (skipStyles != null && skipStyles.Count > 0)) && doc != null) style = StyleName(doc, s.GraphicsStyleId);
                        if (style != null && skipStyles != null && skipStyles.Count > 0 && IsSkipped(style, skipStyles)) break;
                        parts?.Add(style ?? NoStyle);
                        into.Add(s);
                        break;
                    case GeometryInstance gi:
                        GeometryElement inner;
                        try { inner = gi.GetInstanceGeometry(); } catch (Exception) { onFailure?.Invoke(); break; }
                        CollectSolids(inner, into, doc, skipStyles, parts, depth + 1, onFailure);
                        break;
                    case GeometryElement nested:
                        CollectSolids(nested, into, doc, skipStyles, parts, depth + 1, onFailure);
                        break;
                }
            }
        }

        /// <summary>Name for solids without their own subcategory (they inherit the element's category).</summary>
        public const string NoStyle = "(category)";

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

        /// <summary>Stable key of the excluded subcategories (to tell whether a cached mesh is still usable).</summary>
        public static string SkipKey(ICollection<string> skip) =>
            skip == null || skip.Count == 0 ? "" : string.Join("|", skip.Select(x => x.Trim().ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal));

        /// <summary>
        /// Mesh of an element (non-vertical faces only), in host coordinates. <paramref name="toHost"/> = link
        /// transform (null or identity for the host). Returns TriMesh.Empty if it has no solids.
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

        /// <summary>Triangulates each face and orients each triangle along the face's outward normal.</summary>
        public static void AddSolid(Solid s, TriMeshBuilder b, Transform toHost)
        {
            foreach (Face f in s.Faces)
            {
                Mesh m;
                try { m = f.Triangulate(TriangulationDetail); } catch (Exception) { continue; }
                if (m == null || m.NumTriangles == 0) continue;

                XYZ planeNormal = (f as PlanarFace)?.FaceNormal;
                // Vertical planar faces: they contribute nothing to a vertical ray; skipped without further triangulation.
                if (planeNormal != null && Math.Abs(planeNormal.Z) < TriMeshBuilder.VerticalTolerance) continue;

                var normals = planeNormal == null ? SafeNormals(m) : 0;
                var dist = normals > 0 ? m.DistributionOfNormals : DistributionOfNormals.OnePerFace;
                bool? faceFlip = null; // curved faces without mesh normals: one decision per face

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

        /// <summary>Curved face without mesh normals: the first triangle is compared with the face normal at its centroid.</summary>
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

        /// <summary>Union of the solids' boxes (tighter than the element box, which includes family annotations).</summary>
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

        /// <summary>Wall location line in plan (host coordinates) as a polyline [x0,y0,x1,y1,…] and its thickness.</summary>
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
