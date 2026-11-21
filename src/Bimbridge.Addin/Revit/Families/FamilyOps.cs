// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Families
{
    /// <summary>Error carrying a protocol status (no_soportado, invalido, necesita_usuario…).</summary>
    public sealed class OpFail : Exception
    {
        public readonly string State;
        public OpFail(string status, string message) : base(message) { State = status; }
    }

    /// <summary>
    /// MEP families and connectors (report section 7.1; R008–R010, R015, R016). Everything runs on the Revit thread.
    /// - info / caras: reads the RFA (FamilyManager, connectors, planar faces with a stable reference) or a family loaded in the project.
    /// - abrir: EditFamily from the project; reuses the RFA if it is already open. Each document has its own transaction: there is no
    ///   global transaction spanning project and family.
    /// - conector: creates or reconciles by SEMANTIC KEY (stored in the connector description) and verifies face, normal, radius, system and flow.
    ///   Repeating it does not duplicate: if one already exists with that key, only what differs is corrected.
    /// - param: value, formula, create and associate family parameters.
    /// - cargar: LoadFamily with IFamilyLoadOptions and revalidation from the project.
    /// Nothing is inferred: radius and diameter must be explicit; flow, if not specified, is left untouched and reported as pending.
    /// </summary>
    public static class FamilyOps
    {
        private const double MmPerFt = 304.8;
        private const double Tol = 1e-6;

        // ---------------- utilities ----------------

        private static double Mm(double ft) => Math.Round(ft * MmPerFt, 4);
        private static List<object> P(XYZ p) => p == null ? null : new List<object> { Mm(p.X), Mm(p.Y), Mm(p.Z) };
        private static List<object> D(XYZ v) => v == null ? null : new List<object> { Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6) };

        public static Document RequireFamily(Document doc, string what)
        {
            if (doc == null) throw new OpFail(Status.Invalid, "no document");
            if (!doc.IsFamilyDocument)
                throw new OpFail(Status.NotSupported, what + " works on a family document (RFA) and " + DocRegistry.Instance.IdOf(doc) + " (" + doc.Title +
                                                     ") is a project. Open the family with: fam open family=\"Name\" (or doc=dN if it is already open)");
            return doc;
        }

        public static Document RequireProject(Document doc, string what)
        {
            if (doc == null) throw new OpFail(Status.Invalid, "no document");
            if (doc.IsFamilyDocument)
                throw new OpFail(Status.NotSupported, what + " works on a project and " + DocRegistry.Instance.IdOf(doc) + " (" + doc.Title + ") is a family; pass the project's doc=dN");
            return doc;
        }

        /// <summary>"6.5mm", "6,5 mm", "0.5in", "1/2\"", "0.02ft", "2 m" → feet. No unit → null (no guessing).</summary>
        public static double? LengthFeet(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim().ToLowerInvariant().Replace(',', '.').Replace(" ", "");
            string[] units = { "mm", "cm", "m", "in", "\"", "ft", "'" };
            foreach (var u in units)
            {
                if (!t.EndsWith(u, StringComparison.Ordinal)) continue;
                var num = t.Substring(0, t.Length - u.Length);
                double v;
                var slash = num.IndexOf('/');
                if (slash > 0)
                {
                    if (!double.TryParse(num.Substring(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ||
                        !double.TryParse(num.Substring(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || b == 0) return null;
                    v = a / b;
                }
                else if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
                switch (u)
                {
                    case "mm": return v / MmPerFt;
                    case "cm": return v * 10 / MmPerFt;
                    case "m": return v * 1000 / MmPerFt;
                    case "in": case "\"": return v / 12.0;
                    default: return v;
                }
            }
            return null;
        }

        // ---------------- faces ----------------

        public sealed class FaceInfo
        {
            public long ElementId;
            public int Index;
            public PlanarFace Face;
            public Reference Ref;
            public XYZ Center;
            public XYZ Normal;
            public string Label => ElementId.ToString(CultureInfo.InvariantCulture) + ":" + Index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Planar faces of the family's own geometry (forms, free-form and imported solids), in stable order.</summary>
        public static List<FaceInfo> Faces(Document doc, long onlyElement = 0)
        {
            var list = new List<FaceInfo>();
            var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };
            IEnumerable<Element> elems = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e is GenericForm || e is FreeFormElement || e is ImportInstance);
            if (onlyElement != 0) elems = elems.Where(e => Ids.ToLong(e.Id) == onlyElement);
            foreach (var e in elems.OrderBy(e => Ids.ToLong(e.Id)))
            {
                GeometryElement geo;
                try { geo = e.get_Geometry(opt); } catch (Exception) { continue; }
                if (geo == null) continue;
                var idx = 0;
                foreach (var obj in geo)
                {
                    if (!(obj is Solid s) || s.Volume <= 0) continue;
                    foreach (Face f in s.Faces)
                    {
                        if (!(f is PlanarFace pf)) continue;
                        idx++;
                        if (pf.Reference == null) continue;
                        XYZ center;
                        try
                        {
                            var bb = pf.GetBoundingBox();
                            center = pf.Evaluate(new UV((bb.Min.U + bb.Max.U) / 2, (bb.Min.V + bb.Max.V) / 2));
                        }
                        catch (Exception) { center = pf.Origin; }
                        list.Add(new FaceInfo { ElementId = Ids.ToLong(e.Id), Index = idx, Face = pf, Ref = pf.Reference, Center = center, Normal = pf.FaceNormal });
                    }
                }
            }
            return list;
        }

        public static FaceInfo FindFace(Document doc, string label)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new OpFail(Status.Invalid, "missing face=element:n (see fam faces)");
            var parts = label.Trim().Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], out var eid) && int.TryParse(parts[1], out var n))
            {
                var f = Faces(doc, eid).FirstOrDefault(x => x.Index == n);
                if (f == null) throw new OpFail(Status.Invalid, "face " + label + " does not exist (fam faces lists the planar faces with a reference)");
                return f;
            }
            // Revit stable reference
            try
            {
                var r = Reference.ParseFromStableRepresentation(doc, label.Trim());
                var e = doc.GetElement(r.ElementId);
                var pf = e?.GetGeometryObjectFromReference(r) as PlanarFace;
                if (pf == null) throw new OpFail(Status.Invalid, "the reference is not a planar face");
                return new FaceInfo { ElementId = Ids.ToLong(r.ElementId), Index = 0, Face = pf, Ref = r, Center = pf.Origin, Normal = pf.FaceNormal };
            }
            catch (OpFail) { throw; }
            catch (Exception ex) { throw new OpFail(Status.Invalid, "face '" + label + "' not understood: use element:n from fam faces (" + ex.Message + ")"); }
        }

        private static bool OnFace(FaceInfo f, XYZ p, double tolFt)
        {
            var d = (p - f.Face.Origin).DotProduct(f.Normal);
            if (Math.Abs(d) > tolFt) return false;
            try { var proj = f.Face.Project(p); return proj != null && proj.Distance <= tolFt; } catch (Exception) { return true; }
        }

        public static Dictionary<string, object> FacesInfo(Document doc, IDictionary<string, object> a)
        {
            RequireFamily(doc, "fam faces");
            var only = Arg.GetLong(a, "elem") ?? 0;
            var normal = (Arg.GetString(a, "normal") ?? "").Trim().ToLowerInvariant();
            var faces = Faces(doc, only);
            XYZ want = null;
            switch (normal)
            {
                case "+x": want = XYZ.BasisX; break; case "-x": want = -XYZ.BasisX; break;
                case "+y": want = XYZ.BasisY; break; case "-y": want = -XYZ.BasisY; break;
                case "+z": want = XYZ.BasisZ; break; case "-z": want = -XYZ.BasisZ; break;
            }
            if (want != null) faces = faces.Where(f => f.Normal.DotProduct(want) > 0.999).ToList();
            var rows = new List<object>();
            foreach (var f in faces.Take(300))
            {
                double areaCm2 = 0;
                try { areaCm2 = Math.Round(f.Face.Area * 929.0304, 2); } catch (Exception) { }
                rows.Add(new object[] { f.Label, P(f.Center), D(f.Normal), areaCm2, Connectors(doc).Count(c => OnFace(f, c.Origin, 0.5 / MmPerFt)) });
            }
            return new Dictionary<string, object>
            {
                ["cols"] = new List<object> { "face", "center_mm", "normal", "area_cm2", "connectors" },
                ["rows"] = rows,
                ["total"] = (long)faces.Count,
                ["note"] = "face=element:n for fam connector. Center and normal in family coordinates.",
            };
        }

        // ---------------- connectors ----------------

        public static List<ConnectorElement> Connectors(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>().OrderBy(c => Ids.ToLong(c.Id)).ToList();

        private static Parameter Bip(Element e, BuiltInParameter b)
        {
            try { return e.get_Parameter(b); } catch (Exception) { return null; }
        }

        public static string Description(ConnectorElement c) => Bip(c, BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString() ?? "";

        public static string FlowName(int v)
        {
            switch (v)
            {
                case (int)FlowDirectionType.In: return "in";
                case (int)FlowDirectionType.Out: return "out";
                case (int)FlowDirectionType.Bidirectional: return "bidirectional";
                default: return v.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static int? FlowValue(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "in":  return (int)FlowDirectionType.In;
                case "out":  return (int)FlowDirectionType.Out;
                case "bidirectional":  case "both": return (int)FlowDirectionType.Bidirectional;
                case "": return null;
                default: throw new OpFail(Status.Invalid, "workflow must be in, out or bidirectional");
            }
        }

        private static Parameter FlowParam(ConnectorElement c) =>
            c.Domain == Domain.DomainHvac ? Bip(c, BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM) : Bip(c, BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM);

        public static Dictionary<string, object> DescribeConnector(Document doc, ConnectorElement c, List<FaceInfo> faces)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Ids.ToLong(c.Id),
                ["key"] = Description(c),
                ["domain"] = c.Domain.ToString(),
            };
            try { d["system"] = c.SystemClassification.ToString(); } catch (Exception) { }
            try { d["origin_mm"] = P(c.Origin); } catch (Exception) { }
            try { d["direction"] = D(c.CoordinateSystem.BasisZ); } catch (Exception) { }
            try { d["shape"] = c.Shape.ToString(); } catch (Exception) { }
            var r = Bip(c, BuiltInParameter.CONNECTOR_RADIUS);
            if (r != null && r.HasValue) d["radius_mm"] = Mm(r.AsDouble());
            var di = Bip(c, BuiltInParameter.CONNECTOR_DIAMETER);
            if (di != null && di.HasValue) d["diameter_mm"] = Mm(di.AsDouble());
            var w = Bip(c, BuiltInParameter.CONNECTOR_WIDTH);
            if (w != null && w.HasValue && w.AsDouble() > 0) d["width_mm"] = Mm(w.AsDouble());
            var h = Bip(c, BuiltInParameter.CONNECTOR_HEIGHT);
            if (h != null && h.HasValue && h.AsDouble() > 0) d["height_mm"] = Mm(h.AsDouble());
            var dim = Bip(c, BuiltInParameter.FAMILY_ROUNDCONNECTOR_DIMENSIONTYPE);
            if (dim != null && dim.HasValue) d["dimension"] = dim.AsValueString() ?? dim.AsInteger().ToString(CultureInfo.InvariantCulture);
            var flow = FlowParam(c);
            if (flow != null && flow.HasValue) d["workflow"] = FlowName(flow.AsInteger());
            try { d["primary"] = c.IsPrimary; } catch (Exception) { }
            // Associations with family parameters (if a parameter controls the radius, change it there, not on the connector).
            var fm = doc.FamilyManager;
            var assoc = new Dictionary<string, object>();
            foreach (var b in new[] { BuiltInParameter.CONNECTOR_RADIUS, BuiltInParameter.CONNECTOR_DIAMETER, BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM, BuiltInParameter.RBS_CONNECTOR_DESCRIPTION })
            {
                var p = Bip(c, b);
                if (p == null) continue;
                try { var fp = fm.GetAssociatedFamilyParameter(p); if (fp != null) assoc[b.ToString()] = fp.Definition.Name; } catch (Exception) { }
            }
            if (assoc.Count > 0) d["associated_to"] = assoc;
            if (faces != null)
            {
                try { var f = faces.FirstOrDefault(x => OnFace(x, c.Origin, 0.5 / MmPerFt)); if (f != null) d["face"] = f.Label; } catch (Exception) { }
            }
            return d;
        }

        // ---------------- info ----------------

        public static Dictionary<string, object> Info(UIApplication app, Document doc, IDictionary<string, object> a)
        {
            if (!doc.IsFamilyDocument) return ProjectFamilyInfo(doc, a);
            var fam = doc.OwnerFamily;
            var fm = doc.FamilyManager;
            var d = new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc),
                ["family"] = doc.Title,
            };
            try { d["category"] = fam?.FamilyCategory?.Name; } catch (Exception) { }
            try { d["placement"] = fam?.FamilyPlacementType.ToString(); } catch (Exception) { }
            var current = fm.CurrentType;
            d["current_type"] = current?.Name;
            d["types"] = fm.Types.Cast<FamilyType>().Select(t => (object)t.Name).ToList();
            var prows = new List<object>();
            foreach (var fp in fm.GetParameters())
            {
                string val = null;
                try { if (current != null && current.HasValue(fp)) val = fp.StorageType == StorageType.String ? current.AsString(fp) : current.AsValueString(fp); } catch (Exception) { }
                string spec = null;
                try { spec = fp.Definition.GetDataType()?.TypeId; if (spec != null && spec.StartsWith("autodesk.spec", StringComparison.Ordinal)) spec = spec.Split(':')[0].Split('.').Last(); } catch (Exception) { }
                prows.Add(new object[]
                {
                    fp.Definition.Name, fp.IsInstance ? "instance" : "type", fp.IsShared ? fp.GUID.ToString() : "", spec ?? "",
                    fp.Formula ?? "", val ?? "", (long)(fp.AssociatedParameters?.Size ?? 0),
                });
            }
            d["parameters_cols"] = new List<object> { "name", "scope", "guid", "data_type", "formula", "value", "associated" };
            d["parameters"] = prows;
            var faces = Faces(doc);
            d["connectors"] = Connectors(doc).Select(c => (object)DescribeConnector(doc, c, faces)).ToList();
            d["planar_faces"] = (long)faces.Count;
            return d;
        }

        public static Family FindFamily(Document doc, IDictionary<string, object> a)
        {
            var id = Arg.GetLong(a, "id");
            if (id.HasValue)
            {
                var e = doc.GetElement(Ids.ToId(id.Value));
                if (e is Family f) return f;
                if (e is FamilySymbol fs) return fs.Family;
                if (e is FamilyInstance fi) return fi.Symbol?.Family;
                throw new OpFail(Status.Invalid, "id " + id + " is not a family, family type or family instance");
            }
            var name = Arg.GetString(a, "family") ?? Arg.GetString(a, "name");
            if (string.IsNullOrWhiteSpace(name)) throw new OpFail(Status.Invalid, "specify family=\"Name\" or id=");
            var matches = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => DocRegistry.TitleMatches(f.Name, name)).ToList();
            if (matches.Count == 0)
            {
                var similar = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                    .Where(f => f.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).Select(f => f.Name).Take(8).ToList();
                throw new OpFail(Status.Invalid, "no loaded family named '" + name + "'" + (similar.Count > 0 ? "; similar: " + string.Join(", ", similar) : ""));
            }
            return matches[0];
        }

        private static Dictionary<string, object> ProjectFamilyInfo(Document doc, IDictionary<string, object> a)
        {
            var fam = FindFamily(doc, a);
            var d = new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc),
                ["family"] = fam.Name,
                ["id"] = Ids.ToLong(fam.Id),
            };
            try { d["category"] = fam.FamilyCategory?.Name; } catch (Exception) { }
            try { d["editable"] = fam.IsEditable; d["in_place"] = fam.IsInPlace; } catch (Exception) { }
            try { d["placement"] = fam.FamilyPlacementType.ToString(); } catch (Exception) { }
            var types = new List<object>();
            foreach (var sid in fam.GetFamilySymbolIds())
            {
                var sym = doc.GetElement(sid) as FamilySymbol;
                if (sym == null) continue;
                var insts = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                    .Cast<FamilyInstance>().Where(fi => fi.Symbol.Id == sid).ToList();
                var t = new Dictionary<string, object> { ["type"] = sym.Name, ["id"] = Ids.ToLong(sid), ["instances"] = (long)insts.Count };
                var sample = insts.FirstOrDefault();
                if (sample?.MEPModel?.ConnectorManager != null)
                {
                    var conns = new List<object>();
                    foreach (Connector c in sample.MEPModel.ConnectorManager.Connectors)
                    {
                        var cd = new Dictionary<string, object> { ["domain"] = c.Domain.ToString() };
                        try { cd["origin_mm"] = P(c.Origin); } catch (Exception) { }
                        try { cd["direction"] = D(c.CoordinateSystem.BasisZ); } catch (Exception) { }
                        try { cd["shape"] = c.Shape.ToString(); } catch (Exception) { }
                        try { if (c.Shape == ConnectorProfileType.Round) cd["radius_mm"] = Mm(c.Radius); } catch (Exception) { }
                        try { cd["workflow"] = FlowName((int)c.Direction); } catch (Exception) { }
                        try
                        {
                            var system = c.Domain switch
                            {
                                Domain.DomainPiping => c.PipeSystemType.ToString(),
                                Domain.DomainHvac => c.DuctSystemType.ToString(),
                                Domain.DomainElectrical => c.ElectricalSystemType.ToString(),
                                _ => null,
                            };
                            if (system != null) cd["system"] = system;
                        }
                        catch (Exception) { }
                        try { cd["key"] = c.Description; } catch (Exception) { }
                        conns.Add(cd);
                    }
                    t["instance_connectors"] = conns;
                    t["sample_instance"] = Ids.ToLong(sample.Id);
                }
                types.Add(t);
            }
            d["types"] = types;
            if (!types.OfType<Dictionary<string, object>>().Any(t => t.ContainsKey("instance_connectors")))
                d["note"] = "without placed instances, connectors cannot be read from the project; open it with fam open to read them in the RFA";
            return d;
        }

        // ---------------- open ----------------

        public static Dictionary<string, object> Open(UIApplication app, Document project, IDictionary<string, object> a)
        {
            RequireProject(project, "fam open");
            var fam = FindFamily(project, a);
            if (fam.IsInPlace) throw new OpFail(Status.NotSupported, "'" + fam.Name + "' is an in-place family: it cannot be opened with EditFamily");
            if (!fam.IsEditable) throw new OpFail(Status.NotSupported, "'" + fam.Name + "' is not editable (system or protected family)");
            var forceNew = Arg.GetBool(a, "new") ?? false;
            var projectKey = DocumentTracker.KeyOf(project);
            var skipped = new List<string>();
            if (!forceNew)
            {
                foreach (Document d in app.Application.Documents)
                {
                    if (!d.IsFamilyDocument || d.IsLinked) continue;
                    if (!DocRegistry.TitleMatches(d.Title, fam.Name)) continue;
                    // Provenance (0.6.0): only an RFA opened from THIS project and THIS family (UniqueId) is reused. An RFA with the same
                    // title opened from another project or by hand is not reused: loading it could bring in another version of the family.
                    var key = DocumentTracker.KeyOf(d);
                    if (key != null && Provenance.TryGetValue(key, out var prov) && prov.Project == projectKey && prov.FamilyUid == fam.UniqueId)
                    {
                        DocRegistry.Instance.Refresh(app);
                        return new Dictionary<string, object>
                        {
                            ["doc"] = DocRegistry.Instance.IdOf(d), ["title"] = d.Title, ["reused"] = true, ["from"] = DocRegistry.Instance.IdOf(project),
                            ["provenance"] = "previously opened from this project and this family",
                            ["note"] = "it was already open; new=yes opens another copy from the project (discards unloaded changes in the open one if it is loaded afterwards)",
                        };
                    }
                    skipped.Add(DocRegistry.Instance.IdOf(d) + " (" + d.Title + "): unknown provenance or another project");
                }
            }
            var famDoc = project.EditFamily(fam);
            var famKey = DocumentTracker.KeyOf(famDoc);
            if (famKey != null) Provenance[famKey] = new FamilyProvenance { Project = projectKey, FamilyUid = fam.UniqueId };
            DocRegistry.Instance.Refresh(app);
            var res = new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(famDoc), ["title"] = famDoc.Title, ["reused"] = false, ["from"] = DocRegistry.Instance.IdOf(project),
                ["next"] = "fam info doc=" + DocRegistry.Instance.IdOf(famDoc) + "; changes in the RFA do not reach the project until fam load",
            };
            if (skipped.Count > 0) res["not_reused"] = skipped;
            return res;
        }

        private sealed class FamilyProvenance { public string Project; public string FamilyUid; }

        /// <summary>RFAs opened by fam abrir: family document key → origin project and family. Lives only while Revit is open.</summary>
        private static readonly Dictionary<string, FamilyProvenance> Provenance = new Dictionary<string, FamilyProvenance>(StringComparer.Ordinal);

        // ---------------- connector ----------------

        public sealed class ConnectorRequest
        {
            public string Key;
            public string Kind = "pipe";
            public string System;
            public string Face;
            public double? RadiusFt;
            public double? DiameterFt;
            public double? WidthFt;
            public double? HeightFt;
            public int? Flow;
            public string DimType;
            public bool Adopt = true;
            public bool IgnoreAssociation;

            public static ConnectorRequest Parse(IDictionary<string, object> a)
            {
                var r = new ConnectorRequest
                {
                    Key = Arg.GetString(a, "connector_key") ?? Arg.GetString(a, "name") ?? Arg.GetString(a, "function"),
                    Kind = (Arg.GetString(a, "type") ?? "pipe").Trim().ToLowerInvariant(),
                    System = Arg.GetString(a, "system"),
                    Face = Arg.GetString(a, "face"),
                    Flow = FlowValue(Arg.GetString(a, "workflow")),
                    DimType = Arg.GetString(a, "dimension"),
                    Adopt = Arg.GetBool(a, "adopt") ?? true,
                };
                if (string.IsNullOrWhiteSpace(r.Key)) throw new OpFail(Status.Invalid, "missing name= (semantic connector key, e.g. cold, hot, drain): it is used to recognize the connector on repeat");
                double? Len(string k)
                {
                    var s = Arg.GetString(a, k);
                    if (s == null) return null;
                    var v = LengthFeet(s);
                    if (v == null) throw new OpFail(Status.Invalid, k + "='" + s + "' needs an explicit unit (mm, cm, m, in, ft)");
                    if (v <= 0) throw new OpFail(Status.Invalid, k + " must be greater than 0");
                    return v;
                }
                r.RadiusFt = Len("radius");
                r.DiameterFt = Len("diameter");
                r.WidthFt = Len("width");
                r.HeightFt = Len("height");
                if (r.RadiusFt.HasValue && r.DiameterFt.HasValue) throw new OpFail(Status.Invalid, "give radius or diameter, not both");
                return r;
            }
        }

        private static MEPSystemClassification Classification(string kind, string system, out PipeSystemType pipe, out DuctSystemType duct, out ElectricalSystemType elec)
        {
            pipe = PipeSystemType.UndefinedSystemType; duct = DuctSystemType.UndefinedSystemType; elec = ElectricalSystemType.UndefinedSystemType;
            var s = (system ?? "").Trim();
            switch (s.ToLowerInvariant())
            {
                case "cold": case "cold_water": s = "DomesticColdWater"; break;
                case "hot": case "hot_water": s = "DomesticHotWater"; break;
                case "sanitary": case "drain": s = "Sanitary"; break;
                case "vent": s = "Vent"; break;
            }
            switch (kind)
            {
                case "pipe":
                    if (!Enum.TryParse(s, true, out pipe) || s.Length == 0)
                        throw new OpFail(Status.Invalid, "pipe system: " + string.Join(", ", Enum.GetNames(typeof(PipeSystemType))) + " (or cold, hot, sanitary)");
                    return (MEPSystemClassification)Enum.Parse(typeof(MEPSystemClassification), pipe.ToString());
                case "duct":
                    if (!Enum.TryParse(s, true, out duct) || s.Length == 0)
                        throw new OpFail(Status.Invalid, "duct system: " + string.Join(", ", Enum.GetNames(typeof(DuctSystemType))));
                    return (MEPSystemClassification)Enum.Parse(typeof(MEPSystemClassification), duct.ToString());
                case "electrical":
                    if (!Enum.TryParse(s, true, out elec) || s.Length == 0)
                        throw new OpFail(Status.Invalid, "electrical system: " + string.Join(", ", Enum.GetNames(typeof(ElectricalSystemType))));
                    MEPSystemClassification ec;
                    return Enum.TryParse(elec.ToString(), true, out ec) ? ec : MEPSystemClassification.PowerCircuit;
                case "conduit": return MEPSystemClassification.UndefinedSystemClassification;
                case "tray": return MEPSystemClassification.UndefinedSystemClassification;
                default: throw new OpFail(Status.Invalid, "connector type: pipe, duct, electrical, conduit or tray");
            }
        }

        /// <summary>
        /// Creates or reconciles a connector. Returns what was done, the final state read back from Revit and the differences from the request.
        /// Must run inside a transaction opened by the caller.
        /// </summary>
        public static Dictionary<string, object> UpsertConnector(Document doc, ConnectorRequest q, List<string> changes, List<string> diffs, List<string> pending)
        {
            RequireFamily(doc, "fam connector");
            var cls = Classification(q.Kind, q.System, out var pipe, out var duct, out var elec);
            var faces = Faces(doc);
            FaceInfo face = q.Face != null ? FindFace(doc, q.Face) : null;
            var all = Connectors(doc);

            // 1) By semantic key (connector description)
            var byKey = all.Where(c => string.Equals(Description(c), q.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byKey.Count > 1)
                throw new OpFail(Status.NeedsUser, "there are " + byKey.Count + " connectors with key '" + q.Key + "' (ids " + string.Join(",", byKey.Select(c => Ids.ToLong(c.Id))) +
                                                         "): pre-existing duplicates. Decide which to keep and delete the other (plan delete or exec) before reconciling");
            ConnectorElement ce = byKey.FirstOrDefault();
            var how = ce != null ? "existing (by key)" : null;

            // 2) Adopt a keyless one of the same system on the same face
            if (ce == null && q.Adopt && face != null)
            {
                var cands = all.Where(c => Description(c).Length == 0 && OnFace(face, c.Origin, 0.5 / MmPerFt)).ToList();
                try { cands = cands.Where(c => cls == MEPSystemClassification.UndefinedSystemClassification || c.SystemClassification == cls).ToList(); } catch (Exception) { }
                if (cands.Count > 1)
                    throw new OpFail(Status.NeedsUser, "there are " + cands.Count + " keyless connectors of the same system on face " + face.Label + ": pick one with exec or delete the extra one");
                if (cands.Count == 1) { ce = cands[0]; how = "adopted (was on face " + face.Label + " without a key)"; }
            }

            // 3) Create
            if (ce == null)
            {
                if (face == null) throw new OpFail(Status.Invalid, "connector '" + q.Key + "' does not exist: to create it specify face=element:n (fam faces)");
                if (q.Kind == "pipe" && !q.RadiusFt.HasValue && !q.DiameterFt.HasValue)
                    throw new OpFail(Status.Invalid, "to create a pipe connector specify radius= or diameter= with a unit (e.g. radius=6.5mm); none is assumed");
                switch (q.Kind)
                {
                    case "pipe": ce = ConnectorElement.CreatePipeConnector(doc, pipe, face.Ref); break;
                    case "duct": ce = ConnectorElement.CreateDuctConnector(doc, duct, (q.WidthFt.HasValue ? ConnectorProfileType.Rectangular : ConnectorProfileType.Round), face.Ref); break;
                    case "electrical": ce = ConnectorElement.CreateElectricalConnector(doc, elec, face.Ref); break;
                    case "conduit": ce = ConnectorElement.CreateConduitConnector(doc, face.Ref); break;
                    case "tray": ce = ConnectorElement.CreateCableTrayConnector(doc, face.Ref); break;
                }
                how = "created on face " + face.Label;
                changes.Add("created");
            }

            // Key
            var desc = Bip(ce, BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);
            if (desc != null && !string.Equals(desc.AsString(), q.Key, StringComparison.Ordinal))
            {
                if (desc.IsReadOnly) pending.Add("the connector description is read-only: the key could not be saved");
                else { desc.Set(q.Key); changes.Add("key → " + q.Key); }
            }

            // System
            if (cls != MEPSystemClassification.UndefinedSystemClassification)
            {
                try
                {
                    if (ce.SystemClassification != cls)
                    {
                        if (!ce.IsSystemClassificationValid(cls)) diffs.Add("system " + cls + " is not valid for this connector");
                        else { ce.SystemClassification = cls; changes.Add("system → " + cls); }
                    }
                }
                catch (Exception ex) { diffs.Add("system: " + ex.Message); }
            }

            // Size
            var fm = doc.FamilyManager;
            void SetLen(BuiltInParameter b, double ft, string label)
            {
                var p = Bip(ce, b);
                if (p == null) { diffs.Add(label + ": the connector does not have that parameter"); return; }
                FamilyParameter assoc = null;
                try { assoc = fm.GetAssociatedFamilyParameter(p); } catch (Exception) { }
                if (assoc != null)
                {
                    var cur = p.AsDouble();
                    if (Math.Abs(cur - ft) > Tol)
                        pending.Add(label + " is controlled by family parameter '" + assoc.Definition.Name + "' (" + Mm(cur) + " mm): change it with fam param name=\"" +
                                    assoc.Definition.Name + "\" value=" + Mm(ft) + "mm; the existing association is kept");
                    return;
                }
                if (Math.Abs(p.AsDouble() - ft) <= Tol) return;
                if (p.IsReadOnly) { diffs.Add(label + " is read-only on this connector"); return; }
                p.Set(ft);
                changes.Add(label + " → " + Mm(ft) + " mm");
            }
            if (q.RadiusFt.HasValue) SetLen(BuiltInParameter.CONNECTOR_RADIUS, q.RadiusFt.Value, "radius");
            if (q.DiameterFt.HasValue) SetLen(BuiltInParameter.CONNECTOR_DIAMETER, q.DiameterFt.Value, "diameter");
            if (q.WidthFt.HasValue) SetLen(BuiltInParameter.CONNECTOR_WIDTH, q.WidthFt.Value, "width");
            if (q.HeightFt.HasValue) SetLen(BuiltInParameter.CONNECTOR_HEIGHT, q.HeightFt.Value, "height");

            // Round connector dimension type (radius or diameter in the UI): each value is tried and the one Revit shows as requested is kept.
            if (!string.IsNullOrWhiteSpace(q.DimType))
            {
                var want = q.DimType.Trim().ToLowerInvariant().StartsWith("diam") ? "diam" : "radi";
                var p = Bip(ce, BuiltInParameter.FAMILY_ROUNDCONNECTOR_DIMENSIONTYPE);
                if (p == null || p.IsReadOnly) diffs.Add("dimension: the connector does not allow choosing radius/diameter");
                else
                {
                    string Shown() => (p.AsValueString() ?? "").ToLowerInvariant();
                    if (!Shown().Contains(want))
                    {
                        var original = p.AsInteger();
                        var ok = false;
                        foreach (var v in new[] { 0, 1, 2 })
                        {
                            try { p.Set(v); } catch (Exception) { continue; }
                            if (Shown().Contains(want)) { ok = true; changes.Add("dimension type → " + p.AsValueString()); break; }
                        }
                        if (!ok) { try { p.Set(original); } catch (Exception) { } diffs.Add("dimension: value '" + q.DimType + "' not found (Revit shows '" + p.AsValueString() + "')"); }
                    }
                }
            }

            // Flow: only if requested
            var flow = FlowParam(ce);
            if (q.Flow.HasValue)
            {
                if (flow == null || flow.IsReadOnly) diffs.Add("workflow: the connector has no editable flow parameter");
                else
                {
                    FamilyParameter assoc = null;
                    try { assoc = fm.GetAssociatedFamilyParameter(flow); } catch (Exception) { }
                    if (assoc != null) pending.Add("flow is controlled by family parameter '" + assoc.Definition.Name + "'");
                    else if (flow.AsInteger() != q.Flow.Value) { flow.Set(q.Flow.Value); changes.Add("flow → " + FlowName(q.Flow.Value)); }
                }
            }
            else if (flow != null && flow.HasValue)
                pending.Add("flow not specified: stays " + FlowName(flow.AsInteger()) + " (Revit value, not confirmed by the reference)");

            doc.Regenerate();

            // Verification: read back from Revit, do not trust what was written.
            var final = DescribeConnector(doc, ce, faces);
            if (face != null)
            {
                bool on = false; double dot = 0;
                try { on = OnFace(face, ce.Origin, 0.5 / MmPerFt); dot = ce.CoordinateSystem.BasisZ.DotProduct(face.Normal); } catch (Exception) { }
                if (!on) diffs.Add("the connector origin is not on face " + face.Label);
                if (dot < 0.999) diffs.Add("the connector direction does not match the normal of face " + face.Label + " (dot product " + Math.Round(dot, 4) + ")");
            }
            var rp = Bip(ce, BuiltInParameter.CONNECTOR_RADIUS);
            if (q.RadiusFt.HasValue && rp != null && Math.Abs(rp.AsDouble() - q.RadiusFt.Value) > Tol && !pending.Any(x => x.StartsWith("radius")))
                diffs.Add("radius ended up at " + Mm(rp.AsDouble()) + " mm, " + Mm(q.RadiusFt.Value) + " mm was requested");
            if (q.Flow.HasValue && flow != null && flow.AsInteger() != q.Flow.Value && !pending.Any(x => x.StartsWith("flow")))
                diffs.Add("flow ended up " + FlowName(flow.AsInteger()));
            try { if (cls != MEPSystemClassification.UndefinedSystemClassification && ce.SystemClassification != cls) diffs.Add("system ended up " + ce.SystemClassification); } catch (Exception) { }

            // Duplicates after the operation
            var after = Connectors(doc);
            var sameKey = after.Count(c => string.Equals(Description(c), q.Key, StringComparison.OrdinalIgnoreCase));
            if (sameKey > 1) diffs.Add("there are " + sameKey + " connectors with key '" + q.Key + "'");
            if (face != null)
            {
                var onFace = after.Where(c => OnFace(face, c.Origin, 0.5 / MmPerFt)).ToList();
                if (onFace.Count > 1) diffs.Add("there are " + onFace.Count + " connectors on face " + face.Label + " (ids " + string.Join(",", onFace.Select(c => Ids.ToLong(c.Id))) + ")");
            }

            return new Dictionary<string, object>
            {
                ["connector"] = final,
                ["as"] = how,
                ["connectors_total"] = (long)after.Count,
            };
        }

        // ---------------- parameters ----------------

        private static ForgeTypeId Spec(string dataType)
        {
            switch ((dataType ?? "length").Trim().ToLowerInvariant())
            {
                case "length": return SpecTypeId.Length;
                case "pipe_size":  return SpecTypeId.PipeSize;
                case "text": return SpecTypeId.String.Text;
                case "integer": return SpecTypeId.Int.Integer;
                case "number":  return SpecTypeId.Number;
                case "yes_no":  return SpecTypeId.Boolean.YesNo;
                case "angle": case "ángulo": return SpecTypeId.Angle;
                case "pipe_flow": return SpecTypeId.Flow;
                default: throw new OpFail(Status.Invalid, "data_type: length, pipe_size, text, integer, number, yes_no, angle, pipe_flow");
            }
        }

        public static Dictionary<string, object> Param(Document doc, IDictionary<string, object> a, List<string> changes, List<string> diffs)
        {
            RequireFamily(doc, "fam param");
            var fm = doc.FamilyManager;
            var name = Arg.GetString(a, "name");
            if (string.IsNullOrWhiteSpace(name)) throw new OpFail(Status.Invalid, "missing name= of the family parameter");
            var fp = fm.get_Parameter(name);
            var create = Arg.GetString(a, "create");
            if (fp == null && create == null) throw new OpFail(Status.Invalid, "family parameter '" + name + "' does not exist (fam info lists them); to create it: create=type|instance data_type=length");
            if (fp == null)
            {
                var inst = create.Trim().ToLowerInvariant().StartsWith("inst");
                fp = fm.AddParameter(name, GroupTypeId.General, Spec(Arg.GetString(a, "data_type")), inst);
                changes.Add("created parameter " + name + " (" + (inst ? "instance" : "type") + ")");
            }

            // Types to touch
            var types = new List<FamilyType>();
            var typeName = Arg.GetString(a, "type");
            if (Arg.GetBool(a, "all_types") ?? false) types.AddRange(fm.Types.Cast<FamilyType>());
            else if (typeName != null)
            {
                var t = fm.Types.Cast<FamilyType>().FirstOrDefault(x => string.Equals(x.Name, typeName, StringComparison.OrdinalIgnoreCase));
                if (t == null) throw new OpFail(Status.Invalid, "type '" + typeName + "' does not exist. Types: " + string.Join(", ", fm.Types.Cast<FamilyType>().Select(x => x.Name)));
                types.Add(t);
            }
            else if (fm.CurrentType != null) types.Add(fm.CurrentType);

            var formula = Arg.GetString(a, "formula");
            if (formula != null)
            {
                if (fp.Formula != formula) { fm.SetFormula(fp, formula.Length == 0 ? null : formula); changes.Add("formula → " + (formula.Length == 0 ? "(none)" : formula)); }
            }

            var value = Arg.GetString(a, "value");
            var original = fm.CurrentType;
            var values = new List<object>();
            if (value != null)
            {
                if (!string.IsNullOrEmpty(fp.Formula)) throw new OpFail(Status.Invalid, "'" + name + "' has a formula (" + fp.Formula + "): its value comes from it; change the formula or remove it with formula=\"\"");
                foreach (var t in types)
                {
                    if (fm.CurrentType == null || fm.CurrentType.Name != t.Name) fm.CurrentType = t;
                    switch (fp.StorageType)
                    {
                        case StorageType.Double:
                            var ft = LengthFeet(value);
                            double v;
                            if (ft.HasValue) v = ft.Value;
                            else if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                                throw new OpFail(Status.Invalid, "value '" + value + "' is not a number; lengths need a unit (6.5mm)");
                            else
                            {
                                string specId = null;
                                try { specId = fp.Definition.GetDataType()?.TypeId; } catch (Exception) { }
                                if (specId != null && (specId.Contains(".length") || specId.Contains("pipesize") || specId.Contains("ductsize")))
                                    throw new OpFail(Status.Invalid, "'" + name + "' is a length: give the value with a unit (e.g. 6.5mm) so feet are not confused with mm");
                            }
                            fm.Set(fp, v);
                            break;
                        case StorageType.Integer:
                            var low = value.Trim().ToLowerInvariant();
                            var iv = low == "yes" || low == "yes" || low == "true" ? 1 : low == "no" || low == "false" ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
                            fm.Set(fp, iv);
                            break;
                        case StorageType.String: fm.Set(fp, value); break;
                        default: throw new OpFail(Status.NotSupported, "'" + name + "' stores a " + fp.StorageType + ": use exec for that case");
                    }
                    var shown = fp.StorageType == StorageType.String ? fm.CurrentType.AsString(fp) : fm.CurrentType.AsValueString(fp);
                    values.Add(new object[] { t.Name, shown });
                    changes.Add(t.Name + ": " + name + " = " + shown);
                }
                if (original != null && fm.CurrentType?.Name != original.Name) fm.CurrentType = original;
            }

            var associate = Arg.GetString(a, "associate");
            if (associate != null)
            {
                var parts = associate.Split(new[] { ':' }, 2);
                if (parts.Length != 2 || !long.TryParse(parts[0], out var eid)) throw new OpFail(Status.Invalid, "associate=element:PARAMETER (e.g. 12345:CONNECTOR_RADIUS)");
                var e = doc.GetElement(Ids.ToId(eid)) ?? throw new OpFail(Status.Invalid, "element does not exist: " + eid);
                Parameter ep = null;
                if (Enum.TryParse(parts[1], out BuiltInParameter bip)) ep = Bip(e, bip);
                if (ep == null) ep = e.LookupParameter(parts[1]);
                if (ep == null) throw new OpFail(Status.Invalid, "element " + eid + " does not have parameter " + parts[1]);
                if (!fm.CanElementParameterBeAssociated(ep)) throw new OpFail(Status.NotSupported, parts[1] + " of " + eid + " cannot be associated with a family parameter");
                var cur = fm.GetAssociatedFamilyParameter(ep);
                if (cur == null || cur.Id != fp.Id)
                {
                    fm.AssociateElementParameterToFamilyParameter(ep, fp);
                    changes.Add("associated " + eid + ":" + parts[1] + " → " + name);
                }
            }

            doc.Regenerate();
            var d = new Dictionary<string, object>
            {
                ["parameter"] = name,
                ["scope"] = fp.IsInstance ? "instance" : "type",
                ["formula"] = fp.Formula ?? "",
                ["associated"] = (long)(fp.AssociatedParameters?.Size ?? 0),
            };
            if (values.Count > 0) d["values"] = values;
            return d;
        }

        // ---------------- load ----------------

        internal sealed class LoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwriteValues;
            public LoadOptions(bool overwriteValues) { _overwriteValues = overwriteValues; }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwriteValues;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwriteValues;
                return true;
            }
        }

        public static Dictionary<string, object> Load(UIApplication app, Document famDoc, IDictionary<string, object> a, List<string> diffs)
        {
            RequireFamily(famDoc, "fam load");
            var target = Arg.GetString(a, "into");
            if (string.IsNullOrWhiteSpace(target)) throw new OpFail(Status.Invalid, "missing en=dN (project to load the family into; docs lists the open ones)");
            var info = DocRegistry.Instance.Find(target, out var err);
            if (info == null) throw new OpFail(Status.TargetUnavailable, err);
            var project = DocRegistry.Instance.Resolve(app, info.Id) ?? throw new OpFail(Status.TargetUnavailable, "project " + target + " is no longer open");
            RequireProject(project, "fam load en=");
            var mode = (Arg.GetString(a, "overwrite") ?? "no").Trim().ToLowerInvariant();
            if (mode != "no" && mode != "values") throw new OpFail(Status.Invalid, "overwrite=no (keeps the project's parameter values) or values");
            var connectors = Connectors(famDoc);
            var famConnectors = connectors.Count;
            var verifyPorts = Arg.GetBool(a, "verify_ports") == true;
            var sourceType = verifyPorts ? famDoc.FamilyManager.CurrentType?.Name : null;
            var expected = verifyPorts ? connectors.Select(c => DescribeConnector(famDoc, c, null)).ToList() : null;
            var fam = famDoc.LoadFamily(project, new LoadOptions(mode == "values"));
            if (fam == null) throw new OpFail(Status.Error, "Revit did not load the family (LoadFamily returned null)");
            var check = ProjectFamilyInfo(project, new Dictionary<string, object> { ["id"] = Ids.ToLong(fam.Id) });
            var checkedPorts = false;
            foreach (var t in (check["types"] as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                t.TryGetValue("instance_connectors", out var c);
                if (verifyPorts && famConnectors > 0 && string.Equals(sourceType, Arg.GetString(t, "type"), StringComparison.OrdinalIgnoreCase))
                {
                    checkedPorts = true;
                    diffs.AddRange(FamilyPortAudit.Compare(expected, c as List<object>).Select(d => "type " + t["type"] + ": " + d));
                }
                else if (c is List<object> cl && cl.Count != famConnectors)
                    diffs.Add("type " + t["type"] + ": the instance shows " + cl.Count + " connectors and the RFA has " + famConnectors);
            }
            if (verifyPorts && famConnectors > 0 && !checkedPorts)
                diffs.Add("select a type in the RFA that is available in the project to compare its ports");
            check["loaded_in"] = DocRegistry.Instance.IdOf(project);
            check["overwrite"] = mode;
            check["rfa_connectors"] = (long)famConnectors;
            return check;
        }
    }
}
