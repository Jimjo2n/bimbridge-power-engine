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
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit.Families
{
    /// <summary>Error con estado de protocolo (no_soportado, invalido, necesita_usuario…).</summary>
    public sealed class OpFail : Exception
    {
        public readonly string Estado;
        public OpFail(string estado, string message) : base(message) { Estado = estado; }
    }

    /// <summary>
    /// Familias y conectores MEP (sección 7.1 del informe; R008–R010, R015, R016). Todo corre en el hilo de Revit.
    /// - info / caras: lectura del RFA (FamilyManager, conectores, caras planas con referencia estable) o de una familia cargada en el proyecto.
    /// - abrir: EditFamily desde el proyecto; reutiliza el RFA si ya está abierto. Cada documento tiene su propia transacción: no hay una
    ///   transacción global entre proyecto y familia.
    /// - conector: crea o reconcilia por CLAVE SEMÁNTICA (se guarda en la descripción del conector) y verifica cara, normal, radio, sistema y flujo.
    ///   Repetirlo no duplica: si ya existe con esa clave, sólo corrige lo que difiere.
    /// - param: valor, fórmula, crear y asociar parámetros de familia.
    /// - cargar: LoadFamily con IFamilyLoadOptions y revalidación desde el proyecto.
    /// Nada se infiere: radio y diámetro se piden explícitos; el flujo, si no se indica, no se toca y se informa como pendiente.
    /// </summary>
    public static class FamilyOps
    {
        private const double MmPerFt = 304.8;
        private const double Tol = 1e-6;

        // ---------------- utilidades ----------------

        private static double Mm(double ft) => Math.Round(ft * MmPerFt, 4);
        private static List<object> P(XYZ p) => p == null ? null : new List<object> { Mm(p.X), Mm(p.Y), Mm(p.Z) };
        private static List<object> D(XYZ v) => v == null ? null : new List<object> { Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6) };

        public static Document RequireFamily(Document doc, string what)
        {
            if (doc == null) throw new OpFail(Status.Invalido, "sin documento");
            if (!doc.IsFamilyDocument)
                throw new OpFail(Status.NoSoportado, what + " trabaja en un documento de familia (RFA) y " + DocRegistry.Instance.IdOf(doc) + " (" + doc.Title +
                                                     ") es un proyecto. Abra la familia con: fam abrir familia=\"Nombre\" (o doc=dN si ya está abierta)");
            return doc;
        }

        public static Document RequireProject(Document doc, string what)
        {
            if (doc == null) throw new OpFail(Status.Invalido, "sin documento");
            if (doc.IsFamilyDocument)
                throw new OpFail(Status.NoSoportado, what + " trabaja en un proyecto y " + DocRegistry.Instance.IdOf(doc) + " (" + doc.Title + ") es una familia; pase doc=dN del proyecto");
            return doc;
        }

        /// <summary>"6.5mm", "6,5 mm", "0.5in", "1/2\"", "0.02ft", "2 m" → pies. Sin unidad → null (no se adivina).</summary>
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

        // ---------------- caras ----------------

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

        /// <summary>Caras planas de la geometría propia de la familia (formas, sólidos libres e importados), en orden estable.</summary>
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
            if (string.IsNullOrWhiteSpace(label)) throw new OpFail(Status.Invalido, "falta cara=elemento:n (vea fam caras)");
            var parts = label.Trim().Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], out var eid) && int.TryParse(parts[1], out var n))
            {
                var f = Faces(doc, eid).FirstOrDefault(x => x.Index == n);
                if (f == null) throw new OpFail(Status.Invalido, "no existe la cara " + label + " (fam caras lista las caras planas con referencia)");
                return f;
            }
            // Referencia estable de Revit
            try
            {
                var r = Reference.ParseFromStableRepresentation(doc, label.Trim());
                var e = doc.GetElement(r.ElementId);
                var pf = e?.GetGeometryObjectFromReference(r) as PlanarFace;
                if (pf == null) throw new OpFail(Status.Invalido, "la referencia no es una cara plana");
                return new FaceInfo { ElementId = Ids.ToLong(r.ElementId), Index = 0, Face = pf, Ref = r, Center = pf.Origin, Normal = pf.FaceNormal };
            }
            catch (OpFail) { throw; }
            catch (Exception ex) { throw new OpFail(Status.Invalido, "cara '" + label + "' no se entiende: use elemento:n de fam caras (" + ex.Message + ")"); }
        }

        private static bool OnFace(FaceInfo f, XYZ p, double tolFt)
        {
            var d = (p - f.Face.Origin).DotProduct(f.Normal);
            if (Math.Abs(d) > tolFt) return false;
            try { var proj = f.Face.Project(p); return proj != null && proj.Distance <= tolFt; } catch (Exception) { return true; }
        }

        public static Dictionary<string, object> FacesInfo(Document doc, IDictionary<string, object> a)
        {
            RequireFamily(doc, "fam caras");
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
                ["cols"] = new List<object> { "cara", "centro_mm", "normal", "area_cm2", "conectores" },
                ["rows"] = rows,
                ["total"] = (long)faces.Count,
                ["nota"] = "cara=elemento:n para fam conector. Centro y normal en coordenadas de la familia.",
            };
        }

        // ---------------- conectores ----------------

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
                case (int)FlowDirectionType.In: return "entrada";
                case (int)FlowDirectionType.Out: return "salida";
                case (int)FlowDirectionType.Bidirectional: return "bidireccional";
                default: return v.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static int? FlowValue(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "entrada": case "in": return (int)FlowDirectionType.In;
                case "salida": case "out": return (int)FlowDirectionType.Out;
                case "bidireccional": case "bidirectional": case "ambos": return (int)FlowDirectionType.Bidirectional;
                case "": return null;
                default: throw new OpFail(Status.Invalido, "flujo debe ser entrada, salida o bidireccional");
            }
        }

        private static Parameter FlowParam(ConnectorElement c) =>
            c.Domain == Domain.DomainHvac ? Bip(c, BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM) : Bip(c, BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM);

        public static Dictionary<string, object> DescribeConnector(Document doc, ConnectorElement c, List<FaceInfo> faces)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Ids.ToLong(c.Id),
                ["clave"] = Description(c),
                ["dominio"] = c.Domain.ToString(),
            };
            try { d["sistema"] = c.SystemClassification.ToString(); } catch (Exception) { }
            try { d["origen_mm"] = P(c.Origin); } catch (Exception) { }
            try { d["direccion"] = D(c.CoordinateSystem.BasisZ); } catch (Exception) { }
            try { d["forma"] = c.Shape.ToString(); } catch (Exception) { }
            var r = Bip(c, BuiltInParameter.CONNECTOR_RADIUS);
            if (r != null && r.HasValue) d["radio_mm"] = Mm(r.AsDouble());
            var di = Bip(c, BuiltInParameter.CONNECTOR_DIAMETER);
            if (di != null && di.HasValue) d["diametro_mm"] = Mm(di.AsDouble());
            var w = Bip(c, BuiltInParameter.CONNECTOR_WIDTH);
            if (w != null && w.HasValue && w.AsDouble() > 0) d["ancho_mm"] = Mm(w.AsDouble());
            var h = Bip(c, BuiltInParameter.CONNECTOR_HEIGHT);
            if (h != null && h.HasValue && h.AsDouble() > 0) d["alto_mm"] = Mm(h.AsDouble());
            var dim = Bip(c, BuiltInParameter.FAMILY_ROUNDCONNECTOR_DIMENSIONTYPE);
            if (dim != null && dim.HasValue) d["cota"] = dim.AsValueString() ?? dim.AsInteger().ToString(CultureInfo.InvariantCulture);
            var flow = FlowParam(c);
            if (flow != null && flow.HasValue) d["flujo"] = FlowName(flow.AsInteger());
            try { d["primario"] = c.IsPrimary; } catch (Exception) { }
            // Asociaciones con parámetros de familia (si el radio lo controla un parámetro, se cambia ahí, no en el conector).
            var fm = doc.FamilyManager;
            var assoc = new Dictionary<string, object>();
            foreach (var b in new[] { BuiltInParameter.CONNECTOR_RADIUS, BuiltInParameter.CONNECTOR_DIAMETER, BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM, BuiltInParameter.RBS_CONNECTOR_DESCRIPTION })
            {
                var p = Bip(c, b);
                if (p == null) continue;
                try { var fp = fm.GetAssociatedFamilyParameter(p); if (fp != null) assoc[b.ToString()] = fp.Definition.Name; } catch (Exception) { }
            }
            if (assoc.Count > 0) d["asociado_a"] = assoc;
            if (faces != null)
            {
                try { var f = faces.FirstOrDefault(x => OnFace(x, c.Origin, 0.5 / MmPerFt)); if (f != null) d["cara"] = f.Label; } catch (Exception) { }
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
                ["familia"] = doc.Title,
            };
            try { d["categoria"] = fam?.FamilyCategory?.Name; } catch (Exception) { }
            try { d["colocacion"] = fam?.FamilyPlacementType.ToString(); } catch (Exception) { }
            var current = fm.CurrentType;
            d["tipo_actual"] = current?.Name;
            d["tipos"] = fm.Types.Cast<FamilyType>().Select(t => (object)t.Name).ToList();
            var prows = new List<object>();
            foreach (var fp in fm.GetParameters())
            {
                string val = null;
                try { if (current != null && current.HasValue(fp)) val = fp.StorageType == StorageType.String ? current.AsString(fp) : current.AsValueString(fp); } catch (Exception) { }
                string spec = null;
                try { spec = fp.Definition.GetDataType()?.TypeId; if (spec != null && spec.StartsWith("autodesk.spec", StringComparison.Ordinal)) spec = spec.Split(':')[0].Split('.').Last(); } catch (Exception) { }
                prows.Add(new object[]
                {
                    fp.Definition.Name, fp.IsInstance ? "instancia" : "tipo", fp.IsShared ? fp.GUID.ToString() : "", spec ?? "",
                    fp.Formula ?? "", val ?? "", (long)(fp.AssociatedParameters?.Size ?? 0),
                });
            }
            d["parametros_cols"] = new List<object> { "nombre", "alcance", "guid", "dato", "formula", "valor", "asociados" };
            d["parametros"] = prows;
            var faces = Faces(doc);
            d["conectores"] = Connectors(doc).Select(c => (object)DescribeConnector(doc, c, faces)).ToList();
            d["caras_planas"] = (long)faces.Count;
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
                throw new OpFail(Status.Invalido, "id " + id + " no es una familia, tipo ni instancia de familia");
            }
            var name = Arg.GetString(a, "familia") ?? Arg.GetString(a, "nombre");
            if (string.IsNullOrWhiteSpace(name)) throw new OpFail(Status.Invalido, "indique familia=\"Nombre\" o id=");
            var matches = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => DocRegistry.TitleMatches(f.Name, name)).ToList();
            if (matches.Count == 0)
            {
                var similar = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                    .Where(f => f.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).Select(f => f.Name).Take(8).ToList();
                throw new OpFail(Status.Invalido, "no hay una familia cargada llamada '" + name + "'" + (similar.Count > 0 ? "; parecidas: " + string.Join(", ", similar) : ""));
            }
            return matches[0];
        }

        private static Dictionary<string, object> ProjectFamilyInfo(Document doc, IDictionary<string, object> a)
        {
            var fam = FindFamily(doc, a);
            var d = new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(doc),
                ["familia"] = fam.Name,
                ["id"] = Ids.ToLong(fam.Id),
            };
            try { d["categoria"] = fam.FamilyCategory?.Name; } catch (Exception) { }
            try { d["editable"] = fam.IsEditable; d["en_sitio"] = fam.IsInPlace; } catch (Exception) { }
            try { d["colocacion"] = fam.FamilyPlacementType.ToString(); } catch (Exception) { }
            var types = new List<object>();
            foreach (var sid in fam.GetFamilySymbolIds())
            {
                var sym = doc.GetElement(sid) as FamilySymbol;
                if (sym == null) continue;
                var insts = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                    .Cast<FamilyInstance>().Where(fi => fi.Symbol.Id == sid).ToList();
                var t = new Dictionary<string, object> { ["tipo"] = sym.Name, ["id"] = Ids.ToLong(sid), ["instancias"] = (long)insts.Count };
                var sample = insts.FirstOrDefault();
                if (sample?.MEPModel?.ConnectorManager != null)
                {
                    var conns = new List<object>();
                    foreach (Connector c in sample.MEPModel.ConnectorManager.Connectors)
                    {
                        var cd = new Dictionary<string, object> { ["dominio"] = c.Domain.ToString() };
                        try { cd["origen_mm"] = P(c.Origin); } catch (Exception) { }
                        try { cd["direccion"] = D(c.CoordinateSystem.BasisZ); } catch (Exception) { }
                        try { if (c.Shape == ConnectorProfileType.Round) cd["radio_mm"] = Mm(c.Radius); } catch (Exception) { }
                        try { cd["flujo"] = FlowName((int)c.Direction); } catch (Exception) { }
                        try { if (c.Domain == Domain.DomainPiping) cd["sistema"] = c.PipeSystemType.ToString(); } catch (Exception) { }
                        try { cd["clave"] = c.Description; } catch (Exception) { }
                        conns.Add(cd);
                    }
                    t["conectores_instancia"] = conns;
                    t["instancia_muestra"] = Ids.ToLong(sample.Id);
                }
                types.Add(t);
            }
            d["tipos"] = types;
            if (!types.OfType<Dictionary<string, object>>().Any(t => t.ContainsKey("conectores_instancia")))
                d["nota"] = "sin instancias colocadas no se pueden leer conectores desde el proyecto; ábrala con fam abrir para leerlos en el RFA";
            return d;
        }

        // ---------------- abrir ----------------

        public static Dictionary<string, object> Open(UIApplication app, Document project, IDictionary<string, object> a)
        {
            RequireProject(project, "fam abrir");
            var fam = FindFamily(project, a);
            if (fam.IsInPlace) throw new OpFail(Status.NoSoportado, "'" + fam.Name + "' es una familia en sitio: no se puede abrir con EditFamily");
            if (!fam.IsEditable) throw new OpFail(Status.NoSoportado, "'" + fam.Name + "' no es editable (familia del sistema o protegida)");
            var forceNew = Arg.GetBool(a, "nuevo") ?? false;
            var projectKey = DocumentTracker.KeyOf(project);
            var skipped = new List<string>();
            if (!forceNew)
            {
                foreach (Document d in app.Application.Documents)
                {
                    if (!d.IsFamilyDocument || d.IsLinked) continue;
                    if (!DocRegistry.TitleMatches(d.Title, fam.Name)) continue;
                    // Procedencia (0.6.0): sólo se reutiliza un RFA abierto desde ESTE proyecto y ESTA familia (UniqueId). Un RFA con el mismo
                    // título abierto desde otro proyecto o a mano no se reutiliza: cargarlo podría llevar otra versión de la familia.
                    var key = DocumentTracker.KeyOf(d);
                    if (key != null && Provenance.TryGetValue(key, out var prov) && prov.Project == projectKey && prov.FamilyUid == fam.UniqueId)
                    {
                        DocRegistry.Instance.Refresh(app);
                        return new Dictionary<string, object>
                        {
                            ["doc"] = DocRegistry.Instance.IdOf(d), ["titulo"] = d.Title, ["reutilizado"] = true, ["desde"] = DocRegistry.Instance.IdOf(project),
                            ["procedencia"] = "abierto antes desde este proyecto y esta familia",
                            ["nota"] = "ya estaba abierta; nuevo=si abre otra copia desde el proyecto (descarta cambios no cargados de la que está abierta si luego se carga)",
                        };
                    }
                    skipped.Add(DocRegistry.Instance.IdOf(d) + " (" + d.Title + "): procedencia desconocida u otro proyecto");
                }
            }
            var famDoc = project.EditFamily(fam);
            var famKey = DocumentTracker.KeyOf(famDoc);
            if (famKey != null) Provenance[famKey] = new FamilyProvenance { Project = projectKey, FamilyUid = fam.UniqueId };
            DocRegistry.Instance.Refresh(app);
            var res = new Dictionary<string, object>
            {
                ["doc"] = DocRegistry.Instance.IdOf(famDoc), ["titulo"] = famDoc.Title, ["reutilizado"] = false, ["desde"] = DocRegistry.Instance.IdOf(project),
                ["siguiente"] = "fam info doc=" + DocRegistry.Instance.IdOf(famDoc) + "; los cambios en el RFA no llegan al proyecto hasta fam cargar",
            };
            if (skipped.Count > 0) res["no_reutilizados"] = skipped;
            return res;
        }

        private sealed class FamilyProvenance { public string Project; public string FamilyUid; }

        /// <summary>RFA abiertos por fam abrir: clave del documento de familia → proyecto y familia de origen. Sólo vive mientras Revit está abierto.</summary>
        private static readonly Dictionary<string, FamilyProvenance> Provenance = new Dictionary<string, FamilyProvenance>(StringComparer.Ordinal);

        // ---------------- conector ----------------

        public sealed class ConnectorRequest
        {
            public string Key;
            public string Kind = "tuberia";
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
                    Key = Arg.GetString(a, "clave_conector") ?? Arg.GetString(a, "nombre") ?? Arg.GetString(a, "funcion"),
                    Kind = (Arg.GetString(a, "tipo") ?? "tuberia").Trim().ToLowerInvariant(),
                    System = Arg.GetString(a, "sistema"),
                    Face = Arg.GetString(a, "cara"),
                    Flow = FlowValue(Arg.GetString(a, "flujo")),
                    DimType = Arg.GetString(a, "cota"),
                    Adopt = Arg.GetBool(a, "adoptar") ?? true,
                };
                if (string.IsNullOrWhiteSpace(r.Key)) throw new OpFail(Status.Invalido, "falta nombre= (clave semántica del conector, p. ej. fria, caliente, desague): con ella se reconoce al repetir");
                double? Len(string k)
                {
                    var s = Arg.GetString(a, k);
                    if (s == null) return null;
                    var v = LengthFeet(s);
                    if (v == null) throw new OpFail(Status.Invalido, k + "='" + s + "' necesita unidad explícita (mm, cm, m, in, ft)");
                    if (v <= 0) throw new OpFail(Status.Invalido, k + " debe ser mayor que 0");
                    return v;
                }
                r.RadiusFt = Len("radio");
                r.DiameterFt = Len("diametro");
                r.WidthFt = Len("ancho");
                r.HeightFt = Len("alto");
                if (r.RadiusFt.HasValue && r.DiameterFt.HasValue) throw new OpFail(Status.Invalido, "dé radio o diametro, no los dos");
                return r;
            }
        }

        private static MEPSystemClassification Classification(string kind, string system, out PipeSystemType pipe, out DuctSystemType duct, out ElectricalSystemType elec)
        {
            pipe = PipeSystemType.UndefinedSystemType; duct = DuctSystemType.UndefinedSystemType; elec = ElectricalSystemType.UndefinedSystemType;
            var s = (system ?? "").Trim();
            switch (s.ToLowerInvariant())
            {
                case "fria": case "agua_fria": s = "DomesticColdWater"; break;
                case "caliente": case "agua_caliente": s = "DomesticHotWater"; break;
                case "sanitario": case "desague": s = "Sanitary"; break;
                case "ventilacion": s = "Vent"; break;
            }
            switch (kind)
            {
                case "tuberia":
                    if (!Enum.TryParse(s, true, out pipe) || s.Length == 0)
                        throw new OpFail(Status.Invalido, "sistema de tubería: " + string.Join(", ", Enum.GetNames(typeof(PipeSystemType))) + " (o fria, caliente, sanitario)");
                    return (MEPSystemClassification)Enum.Parse(typeof(MEPSystemClassification), pipe.ToString());
                case "ducto":
                    if (!Enum.TryParse(s, true, out duct) || s.Length == 0)
                        throw new OpFail(Status.Invalido, "sistema de ducto: " + string.Join(", ", Enum.GetNames(typeof(DuctSystemType))));
                    return (MEPSystemClassification)Enum.Parse(typeof(MEPSystemClassification), duct.ToString());
                case "electrico":
                    if (!Enum.TryParse(s, true, out elec) || s.Length == 0)
                        throw new OpFail(Status.Invalido, "sistema eléctrico: " + string.Join(", ", Enum.GetNames(typeof(ElectricalSystemType))));
                    MEPSystemClassification ec;
                    return Enum.TryParse(elec.ToString(), true, out ec) ? ec : MEPSystemClassification.PowerCircuit;
                case "conduit": return MEPSystemClassification.UndefinedSystemClassification;
                case "bandeja": return MEPSystemClassification.UndefinedSystemClassification;
                default: throw new OpFail(Status.Invalido, "tipo de conector: tuberia, ducto, electrico, conduit o bandeja");
            }
        }

        /// <summary>
        /// Crea o reconcilia un conector. Devuelve lo hecho, el estado final leído de Revit y las diferencias con lo pedido.
        /// Debe correr dentro de una transacción abierta por quien llama.
        /// </summary>
        public static Dictionary<string, object> UpsertConnector(Document doc, ConnectorRequest q, List<string> changes, List<string> diffs, List<string> pending)
        {
            RequireFamily(doc, "fam conector");
            var cls = Classification(q.Kind, q.System, out var pipe, out var duct, out var elec);
            var faces = Faces(doc);
            FaceInfo face = q.Face != null ? FindFace(doc, q.Face) : null;
            var all = Connectors(doc);

            // 1) Por clave semántica (descripción del conector)
            var byKey = all.Where(c => string.Equals(Description(c), q.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byKey.Count > 1)
                throw new OpFail(Status.NecesitaUsuario, "hay " + byKey.Count + " conectores con la clave '" + q.Key + "' (ids " + string.Join(",", byKey.Select(c => Ids.ToLong(c.Id))) +
                                                         "): duplicados previos. Decida cuál conservar y borre el otro (plan delete o exec) antes de reconciliar");
            ConnectorElement ce = byKey.FirstOrDefault();
            var how = ce != null ? "existente (por clave)" : null;

            // 2) Adoptar uno sin clave del mismo sistema sobre la misma cara
            if (ce == null && q.Adopt && face != null)
            {
                var cands = all.Where(c => Description(c).Length == 0 && OnFace(face, c.Origin, 0.5 / MmPerFt)).ToList();
                try { cands = cands.Where(c => cls == MEPSystemClassification.UndefinedSystemClassification || c.SystemClassification == cls).ToList(); } catch (Exception) { }
                if (cands.Count > 1)
                    throw new OpFail(Status.NecesitaUsuario, "hay " + cands.Count + " conectores sin clave en la cara " + face.Label + " del mismo sistema: indique cuál con exec o borre el sobrante");
                if (cands.Count == 1) { ce = cands[0]; how = "adoptado (estaba en la cara " + face.Label + " sin clave)"; }
            }

            // 3) Crear
            if (ce == null)
            {
                if (face == null) throw new OpFail(Status.Invalido, "no existe un conector '" + q.Key + "': para crearlo indique cara=elemento:n (fam caras)");
                if (q.Kind == "tuberia" && !q.RadiusFt.HasValue && !q.DiameterFt.HasValue)
                    throw new OpFail(Status.Invalido, "para crear un conector de tubería indique radio= o diametro= con unidad (p. ej. radio=6.5mm); no se supone ninguno");
                switch (q.Kind)
                {
                    case "tuberia": ce = ConnectorElement.CreatePipeConnector(doc, pipe, face.Ref); break;
                    case "ducto": ce = ConnectorElement.CreateDuctConnector(doc, duct, (q.WidthFt.HasValue ? ConnectorProfileType.Rectangular : ConnectorProfileType.Round), face.Ref); break;
                    case "electrico": ce = ConnectorElement.CreateElectricalConnector(doc, elec, face.Ref); break;
                    case "conduit": ce = ConnectorElement.CreateConduitConnector(doc, face.Ref); break;
                    case "bandeja": ce = ConnectorElement.CreateCableTrayConnector(doc, face.Ref); break;
                }
                how = "creado en la cara " + face.Label;
                changes.Add("creado");
            }

            // Clave
            var desc = Bip(ce, BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);
            if (desc != null && !string.Equals(desc.AsString(), q.Key, StringComparison.Ordinal))
            {
                if (desc.IsReadOnly) pending.Add("la descripción del conector es de sólo lectura: no se pudo guardar la clave");
                else { desc.Set(q.Key); changes.Add("clave → " + q.Key); }
            }

            // Sistema
            if (cls != MEPSystemClassification.UndefinedSystemClassification)
            {
                try
                {
                    if (ce.SystemClassification != cls)
                    {
                        if (!ce.IsSystemClassificationValid(cls)) diffs.Add("sistema " + cls + " no es válido para este conector");
                        else { ce.SystemClassification = cls; changes.Add("sistema → " + cls); }
                    }
                }
                catch (Exception ex) { diffs.Add("sistema: " + ex.Message); }
            }

            // Tamaño
            var fm = doc.FamilyManager;
            void SetLen(BuiltInParameter b, double ft, string label)
            {
                var p = Bip(ce, b);
                if (p == null) { diffs.Add(label + ": el conector no tiene ese parámetro"); return; }
                FamilyParameter assoc = null;
                try { assoc = fm.GetAssociatedFamilyParameter(p); } catch (Exception) { }
                if (assoc != null)
                {
                    var cur = p.AsDouble();
                    if (Math.Abs(cur - ft) > Tol)
                        pending.Add(label + " lo controla el parámetro de familia '" + assoc.Definition.Name + "' (" + Mm(cur) + " mm): cámbielo con fam param nombre=\"" +
                                    assoc.Definition.Name + "\" valor=" + Mm(ft) + "mm; la asociación existente se conserva");
                    return;
                }
                if (Math.Abs(p.AsDouble() - ft) <= Tol) return;
                if (p.IsReadOnly) { diffs.Add(label + " es de sólo lectura en este conector"); return; }
                p.Set(ft);
                changes.Add(label + " → " + Mm(ft) + " mm");
            }
            if (q.RadiusFt.HasValue) SetLen(BuiltInParameter.CONNECTOR_RADIUS, q.RadiusFt.Value, "radio");
            if (q.DiameterFt.HasValue) SetLen(BuiltInParameter.CONNECTOR_DIAMETER, q.DiameterFt.Value, "diámetro");
            if (q.WidthFt.HasValue) SetLen(BuiltInParameter.CONNECTOR_WIDTH, q.WidthFt.Value, "ancho");
            if (q.HeightFt.HasValue) SetLen(BuiltInParameter.CONNECTOR_HEIGHT, q.HeightFt.Value, "alto");

            // Cota del conector redondo (usar radio o diámetro en la interfaz): se prueba cada valor y se conserva el que Revit muestra como pedido.
            if (!string.IsNullOrWhiteSpace(q.DimType))
            {
                var want = q.DimType.Trim().ToLowerInvariant().StartsWith("diam") ? "diam" : "radi";
                var p = Bip(ce, BuiltInParameter.FAMILY_ROUNDCONNECTOR_DIMENSIONTYPE);
                if (p == null || p.IsReadOnly) diffs.Add("cota: el conector no permite elegir radio/diámetro");
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
                            if (Shown().Contains(want)) { ok = true; changes.Add("cota → " + p.AsValueString()); break; }
                        }
                        if (!ok) { try { p.Set(original); } catch (Exception) { } diffs.Add("cota: no se encontró el valor '" + q.DimType + "' (Revit muestra '" + p.AsValueString() + "')"); }
                    }
                }
            }

            // Flujo: sólo si se pidió
            var flow = FlowParam(ce);
            if (q.Flow.HasValue)
            {
                if (flow == null || flow.IsReadOnly) diffs.Add("flujo: el conector no tiene un parámetro de flujo editable");
                else
                {
                    FamilyParameter assoc = null;
                    try { assoc = fm.GetAssociatedFamilyParameter(flow); } catch (Exception) { }
                    if (assoc != null) pending.Add("flujo lo controla el parámetro de familia '" + assoc.Definition.Name + "'");
                    else if (flow.AsInteger() != q.Flow.Value) { flow.Set(q.Flow.Value); changes.Add("flujo → " + FlowName(q.Flow.Value)); }
                }
            }
            else if (flow != null && flow.HasValue)
                pending.Add("flujo no indicado: queda " + FlowName(flow.AsInteger()) + " (valor de Revit, no confirmado por la referencia)");

            doc.Regenerate();

            // Verificación: releer de Revit, no confiar en lo escrito.
            var final = DescribeConnector(doc, ce, faces);
            if (face != null)
            {
                bool on = false; double dot = 0;
                try { on = OnFace(face, ce.Origin, 0.5 / MmPerFt); dot = ce.CoordinateSystem.BasisZ.DotProduct(face.Normal); } catch (Exception) { }
                if (!on) diffs.Add("el origen del conector no está sobre la cara " + face.Label);
                if (dot < 0.999) diffs.Add("la dirección del conector no coincide con la normal de la cara " + face.Label + " (producto " + Math.Round(dot, 4) + ")");
            }
            var rp = Bip(ce, BuiltInParameter.CONNECTOR_RADIUS);
            if (q.RadiusFt.HasValue && rp != null && Math.Abs(rp.AsDouble() - q.RadiusFt.Value) > Tol && !pending.Any(x => x.StartsWith("radio")))
                diffs.Add("radio quedó en " + Mm(rp.AsDouble()) + " mm, se pidió " + Mm(q.RadiusFt.Value) + " mm");
            if (q.Flow.HasValue && flow != null && flow.AsInteger() != q.Flow.Value && !pending.Any(x => x.StartsWith("flujo")))
                diffs.Add("flujo quedó " + FlowName(flow.AsInteger()));
            try { if (cls != MEPSystemClassification.UndefinedSystemClassification && ce.SystemClassification != cls) diffs.Add("sistema quedó " + ce.SystemClassification); } catch (Exception) { }

            // Duplicados después de la operación
            var after = Connectors(doc);
            var sameKey = after.Count(c => string.Equals(Description(c), q.Key, StringComparison.OrdinalIgnoreCase));
            if (sameKey > 1) diffs.Add("hay " + sameKey + " conectores con la clave '" + q.Key + "'");
            if (face != null)
            {
                var onFace = after.Where(c => OnFace(face, c.Origin, 0.5 / MmPerFt)).ToList();
                if (onFace.Count > 1) diffs.Add("hay " + onFace.Count + " conectores en la cara " + face.Label + " (ids " + string.Join(",", onFace.Select(c => Ids.ToLong(c.Id))) + ")");
            }

            return new Dictionary<string, object>
            {
                ["conector"] = final,
                ["como"] = how,
                ["total_conectores"] = (long)after.Count,
            };
        }

        // ---------------- parámetros ----------------

        private static ForgeTypeId Spec(string dato)
        {
            switch ((dato ?? "longitud").Trim().ToLowerInvariant())
            {
                case "longitud": return SpecTypeId.Length;
                case "tamano_tuberia": case "tamaño_tuberia": return SpecTypeId.PipeSize;
                case "texto": return SpecTypeId.String.Text;
                case "entero": return SpecTypeId.Int.Integer;
                case "numero": case "número": return SpecTypeId.Number;
                case "si_no": case "sí_no": return SpecTypeId.Boolean.YesNo;
                case "angulo": case "ángulo": return SpecTypeId.Angle;
                case "caudal_tuberia": return SpecTypeId.Flow;
                default: throw new OpFail(Status.Invalido, "dato: longitud, tamano_tuberia, texto, entero, numero, si_no, angulo, caudal_tuberia");
            }
        }

        public static Dictionary<string, object> Param(Document doc, IDictionary<string, object> a, List<string> changes, List<string> diffs)
        {
            RequireFamily(doc, "fam param");
            var fm = doc.FamilyManager;
            var name = Arg.GetString(a, "nombre");
            if (string.IsNullOrWhiteSpace(name)) throw new OpFail(Status.Invalido, "falta nombre= del parámetro de familia");
            var fp = fm.get_Parameter(name);
            var crear = Arg.GetString(a, "crear");
            if (fp == null && crear == null) throw new OpFail(Status.Invalido, "no existe el parámetro de familia '" + name + "' (fam info lo lista); para crearlo: crear=tipo|instancia dato=longitud");
            if (fp == null)
            {
                var inst = crear.Trim().ToLowerInvariant().StartsWith("inst");
                fp = fm.AddParameter(name, GroupTypeId.General, Spec(Arg.GetString(a, "dato")), inst);
                changes.Add("creado parámetro " + name + " (" + (inst ? "instancia" : "tipo") + ")");
            }

            // Tipos a tocar
            var types = new List<FamilyType>();
            var tipo = Arg.GetString(a, "tipo");
            if (Arg.GetBool(a, "todos_tipos") ?? false) types.AddRange(fm.Types.Cast<FamilyType>());
            else if (tipo != null)
            {
                var t = fm.Types.Cast<FamilyType>().FirstOrDefault(x => string.Equals(x.Name, tipo, StringComparison.OrdinalIgnoreCase));
                if (t == null) throw new OpFail(Status.Invalido, "no existe el tipo '" + tipo + "'. Tipos: " + string.Join(", ", fm.Types.Cast<FamilyType>().Select(x => x.Name)));
                types.Add(t);
            }
            else if (fm.CurrentType != null) types.Add(fm.CurrentType);

            var formula = Arg.GetString(a, "formula");
            if (formula != null)
            {
                if (fp.Formula != formula) { fm.SetFormula(fp, formula.Length == 0 ? null : formula); changes.Add("fórmula → " + (formula.Length == 0 ? "(ninguna)" : formula)); }
            }

            var valor = Arg.GetString(a, "valor");
            var original = fm.CurrentType;
            var values = new List<object>();
            if (valor != null)
            {
                if (!string.IsNullOrEmpty(fp.Formula)) throw new OpFail(Status.Invalido, "'" + name + "' tiene fórmula (" + fp.Formula + "): su valor sale de ella; cambie la fórmula o quítela con formula=\"\"");
                foreach (var t in types)
                {
                    if (fm.CurrentType == null || fm.CurrentType.Name != t.Name) fm.CurrentType = t;
                    switch (fp.StorageType)
                    {
                        case StorageType.Double:
                            var ft = LengthFeet(valor);
                            double v;
                            if (ft.HasValue) v = ft.Value;
                            else if (!double.TryParse(valor.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                                throw new OpFail(Status.Invalido, "valor '" + valor + "' no es un número; longitudes con unidad (6.5mm)");
                            else
                            {
                                string specId = null;
                                try { specId = fp.Definition.GetDataType()?.TypeId; } catch (Exception) { }
                                if (specId != null && (specId.Contains(".length") || specId.Contains("pipesize") || specId.Contains("ductsize")))
                                    throw new OpFail(Status.Invalido, "'" + name + "' es una longitud: dé el valor con unidad (p. ej. 6.5mm) para no confundir pies con mm");
                            }
                            fm.Set(fp, v);
                            break;
                        case StorageType.Integer:
                            var low = valor.Trim().ToLowerInvariant();
                            var iv = low == "si" || low == "sí" || low == "true" ? 1 : low == "no" || low == "false" ? 0 : int.Parse(valor, CultureInfo.InvariantCulture);
                            fm.Set(fp, iv);
                            break;
                        case StorageType.String: fm.Set(fp, valor); break;
                        default: throw new OpFail(Status.NoSoportado, "'" + name + "' guarda un " + fp.StorageType + ": use exec para ese caso");
                    }
                    var shown = fp.StorageType == StorageType.String ? fm.CurrentType.AsString(fp) : fm.CurrentType.AsValueString(fp);
                    values.Add(new object[] { t.Name, shown });
                    changes.Add(t.Name + ": " + name + " = " + shown);
                }
                if (original != null && fm.CurrentType?.Name != original.Name) fm.CurrentType = original;
            }

            var asociar = Arg.GetString(a, "asociar");
            if (asociar != null)
            {
                var parts = asociar.Split(new[] { ':' }, 2);
                if (parts.Length != 2 || !long.TryParse(parts[0], out var eid)) throw new OpFail(Status.Invalido, "asociar=elemento:PARAMETRO (p. ej. 12345:CONNECTOR_RADIUS)");
                var e = doc.GetElement(Ids.ToId(eid)) ?? throw new OpFail(Status.Invalido, "no existe el elemento " + eid);
                Parameter ep = null;
                if (Enum.TryParse(parts[1], out BuiltInParameter bip)) ep = Bip(e, bip);
                if (ep == null) ep = e.LookupParameter(parts[1]);
                if (ep == null) throw new OpFail(Status.Invalido, "el elemento " + eid + " no tiene el parámetro " + parts[1]);
                if (!fm.CanElementParameterBeAssociated(ep)) throw new OpFail(Status.NoSoportado, parts[1] + " de " + eid + " no se puede asociar a un parámetro de familia");
                var cur = fm.GetAssociatedFamilyParameter(ep);
                if (cur == null || cur.Id != fp.Id)
                {
                    fm.AssociateElementParameterToFamilyParameter(ep, fp);
                    changes.Add("asociado " + eid + ":" + parts[1] + " → " + name);
                }
            }

            doc.Regenerate();
            var d = new Dictionary<string, object>
            {
                ["parametro"] = name,
                ["alcance"] = fp.IsInstance ? "instancia" : "tipo",
                ["formula"] = fp.Formula ?? "",
                ["asociados"] = (long)(fp.AssociatedParameters?.Size ?? 0),
            };
            if (values.Count > 0) d["valores"] = values;
            return d;
        }

        // ---------------- cargar ----------------

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
            RequireFamily(famDoc, "fam cargar");
            var target = Arg.GetString(a, "en");
            if (string.IsNullOrWhiteSpace(target)) throw new OpFail(Status.Invalido, "falta en=dN (proyecto donde cargar la familia; docs lista los abiertos)");
            var info = DocRegistry.Instance.Find(target, out var err);
            if (info == null) throw new OpFail(Status.DestinoNoDisponible, err);
            var project = DocRegistry.Instance.Resolve(app, info.Id) ?? throw new OpFail(Status.DestinoNoDisponible, "el proyecto " + target + " ya no está abierto");
            RequireProject(project, "fam cargar en=");
            var mode = (Arg.GetString(a, "sobrescribir") ?? "no").Trim().ToLowerInvariant();
            if (mode != "no" && mode != "valores") throw new OpFail(Status.Invalido, "sobrescribir=no (conserva valores de parámetros del proyecto) o valores");
            var famConnectors = Connectors(famDoc).Count;
            var fam = famDoc.LoadFamily(project, new LoadOptions(mode == "valores"));
            if (fam == null) throw new OpFail(Status.Error, "Revit no cargó la familia (LoadFamily devolvió null)");
            var check = ProjectFamilyInfo(project, new Dictionary<string, object> { ["id"] = Ids.ToLong(fam.Id) });
            foreach (var t in (check["tipos"] as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                if (t.TryGetValue("conectores_instancia", out var c) && c is List<object> cl && cl.Count != famConnectors)
                    diffs.Add("tipo " + t["tipo"] + ": la instancia muestra " + cl.Count + " conectores y el RFA tiene " + famConnectors);
            }
            check["cargada_en"] = DocRegistry.Instance.IdOf(project);
            check["sobrescribir"] = mode;
            check["conectores_rfa"] = (long)famConnectors;
            return check;
        }
    }
}
