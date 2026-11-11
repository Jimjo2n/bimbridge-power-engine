// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

// Inventario offline de la API pública de Revit por versión (0.9.1, Bloque B).
// Lee los ensamblados de referencia (paquetes Nice3point.Revit.Api.* de la caché de NuGet) con System.Reflection.Metadata:
// no carga los ensamblados ni ejecuta código de Autodesk, y no necesita Revit instalado.
//
// uso: RevitApiInventory <carpeta-salida> [raíz-paquetes-nuget]
// Salida: revit-api-<año>.json por versión y revit-api-diff.json con lo que aparece o desaparece entre versiones.
internal static class Program
{
    private static readonly (int Year, string Version)[] Versions =
    {
        (2022, "2022.1.80"), (2023, "2023.1.90"), (2024, "2024.3.60"), (2025, "2025.4.60"), (2026, "2026.4.10"),
    };

    // Superficies de API que la cobertura funcional necesita conocer (tipo y, opcionalmente, método estático/instancia por nombre).
    private static readonly string[] Surfaces =
    {
        "Autodesk.Revit.DB.Wall::Create", "Autodesk.Revit.DB.Floor::Create", "Autodesk.Revit.DB.Ceiling::Create", "Autodesk.Revit.DB.RoofBase",
        "Autodesk.Revit.DB.FootPrintRoof", "Autodesk.Revit.DB.ExtrusionRoof", "Autodesk.Revit.DB.Architecture.Stairs", "Autodesk.Revit.DB.Architecture.Railing::Create",
        "Autodesk.Revit.DB.Architecture.Room", "Autodesk.Revit.DB.Area", "Autodesk.Revit.DB.Mechanical.Space", "Autodesk.Revit.DB.Mechanical.Zone",
        "Autodesk.Revit.DB.CurtainGrid", "Autodesk.Revit.DB.FamilyInstance", "Autodesk.Revit.DB.Grid::Create", "Autodesk.Revit.DB.Level::Create",
        "Autodesk.Revit.DB.ReferencePlane", "Autodesk.Revit.DB.ProjectLocation", "Autodesk.Revit.DB.BasePoint", "Autodesk.Revit.DB.InternalOrigin",
        "Autodesk.Revit.DB.Architecture.TopographySurface", "Autodesk.Revit.DB.Toposolid::Create", "Autodesk.Revit.DB.Structure.Rebar::CreateFromCurves",
        "Autodesk.Revit.DB.Structure.AreaReinforcement", "Autodesk.Revit.DB.Structure.PathReinforcement", "Autodesk.Revit.DB.Structure.FabricSheet",
        "Autodesk.Revit.DB.Structure.AnalyticalElement", "Autodesk.Revit.DB.Structure.AnalyticalMember", "Autodesk.Revit.DB.Structure.AnalyticalModel",
        "Autodesk.Revit.DB.Steel.SteelElementProperties", "Autodesk.Revit.DB.Structure.StructuralConnectionHandler",
        "Autodesk.Revit.DB.Connector", "Autodesk.Revit.DB.ConnectorManager", "Autodesk.Revit.DB.MEPSystem",
        "Autodesk.Revit.DB.Mechanical.Duct::Create", "Autodesk.Revit.DB.Mechanical.MechanicalSystem::Create", "Autodesk.Revit.DB.Mechanical.FlexDuct::Create",
        "Autodesk.Revit.DB.Plumbing.Pipe::Create", "Autodesk.Revit.DB.Plumbing.PipingSystem::Create", "Autodesk.Revit.DB.Plumbing.FlexPipe::Create",
        "Autodesk.Revit.DB.Electrical.ElectricalSystem::Create", "Autodesk.Revit.DB.Electrical.Wire::Create", "Autodesk.Revit.DB.Electrical.Conduit::Create",
        "Autodesk.Revit.DB.Electrical.CableTray::Create", "Autodesk.Revit.DB.Electrical.PanelScheduleView", "Autodesk.Revit.DB.Electrical.ElectricalLoadClassification",
        "Autodesk.Revit.DB.Electrical.ElectricalSetting", "Autodesk.Revit.DB.Electrical.CircuitNamingScheme", "Autodesk.Revit.DB.Electrical.ElectricalAnalyticalNode",
        "Autodesk.Revit.DB.FabricationPart", "Autodesk.Revit.DB.FabricationConfiguration",
        "Autodesk.Revit.DB.FamilyManager", "Autodesk.Revit.DB.FamilyParameter", "Autodesk.Revit.DB.Document::EditFamily", "Autodesk.Revit.DB.Document::LoadFamily",
        "Autodesk.Revit.DB.ViewPlan::Create", "Autodesk.Revit.DB.ViewSection::CreateSection", "Autodesk.Revit.DB.View3D::CreateIsometric",
        "Autodesk.Revit.DB.ViewDrafting::Create", "Autodesk.Revit.DB.ViewSchedule::CreateSchedule", "Autodesk.Revit.DB.ViewSheet::Create",
        "Autodesk.Revit.DB.Viewport::Create", "Autodesk.Revit.DB.ScheduleSheetInstance::Create", "Autodesk.Revit.DB.IndependentTag::Create",
        "Autodesk.Revit.DB.Dimension", "Autodesk.Revit.DB.TextNote::Create", "Autodesk.Revit.DB.FilledRegion::Create", "Autodesk.Revit.DB.Revision",
        "Autodesk.Revit.DB.RevisionCloud::Create", "Autodesk.Revit.DB.ParameterFilterElement::Create", "Autodesk.Revit.DB.SelectionFilterElement::Create",
        "Autodesk.Revit.DB.RevitLinkType::Create", "Autodesk.Revit.DB.RevitLinkInstance::Create", "Autodesk.Revit.DB.ImportInstance", "Autodesk.Revit.DB.PointCloudInstance",
        "Autodesk.Revit.DB.CADLinkType", "Autodesk.Revit.DB.WorksharingUtils", "Autodesk.Revit.DB.Workset::Create", "Autodesk.Revit.DB.Document::SynchronizeWithCentral",
        "Autodesk.Revit.DB.Phase", "Autodesk.Revit.DB.DesignOption", "Autodesk.Revit.DB.AssemblyInstance::Create", "Autodesk.Revit.DB.Group",
        "Autodesk.Revit.DB.Document::Export", "Autodesk.Revit.DB.IFCExportOptions", "Autodesk.Revit.DB.DWGExportOptions", "Autodesk.Revit.DB.PDFExportOptions",
        "Autodesk.Revit.DB.ImageExportOptions", "Autodesk.Revit.DB.NavisworksExportOptions", "Autodesk.Revit.DB.DirectShape::CreateElement",
        "Autodesk.Revit.DB.ElementTransformUtils::MoveElements", "Autodesk.Revit.DB.ElementTransformUtils::RotateElements", "Autodesk.Revit.DB.ElementTransformUtils::MirrorElements",
        "Autodesk.Revit.DB.ElementTransformUtils::CopyElements", "Autodesk.Revit.DB.JoinGeometryUtils::JoinGeometry", "Autodesk.Revit.DB.SolidSolidCutUtils",
        "Autodesk.Revit.DB.Material::Create", "Autodesk.Revit.DB.SharedParameterElement", "Autodesk.Revit.DB.GlobalParameter::Create",
        "Autodesk.Revit.DB.ForgeTypeId", "Autodesk.Revit.DB.UnitTypeId", "Autodesk.Revit.DB.DisplayUnitType",
        "Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel", "Autodesk.Revit.DB.Analysis.SpatialFieldManager",
        "Autodesk.Revit.UI.UIApplication::PostCommand", "Autodesk.Revit.UI.RevitCommandId::LookupPostableCommandId",
        "Autodesk.Revit.DB.CopyMonitorOptions", "Autodesk.Revit.DB.ElementId::Value", "Autodesk.Revit.DB.ElementId::IntegerValue",
    };

    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("uso: RevitApiInventory <carpeta-salida> [raíz-paquetes-nuget] | registry <carpeta-salida> <carpeta-inventario>"); return 2; }
        if (args[0] == "registry") return Registry(args[1], args[2]);
        if (args[0] == "symbols") return SymbolsMode(args[1], args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        var outDir = args[0];
        var root = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        Directory.CreateDirectory(outDir);
        var perVersion = new SortedDictionary<int, Dictionary<string, object>>();
        foreach (var (year, ver) in Versions)
        {
            var api = FindDll(root, "nice3point.revit.api.revitapi", ver, "RevitAPI.dll");
            var ui = FindDll(root, "nice3point.revit.api.revitapiui", ver, "RevitAPIUI.dll");
            if (api == null || ui == null) { Console.Error.WriteLine("falta el paquete de referencia " + year + " (" + ver + ")"); return 3; }
            using var apiPe = new PEReader(File.OpenRead(api));
            using var uiPe = new PEReader(File.OpenRead(ui));
            var apiMd = apiPe.GetMetadataReader(); var uiMd = uiPe.GetMetadataReader();
            var postable = EnumMembers(uiMd, "Autodesk.Revit.UI", "PostableCommand");
            var categories = EnumMembers(apiMd, "Autodesk.Revit.DB", "BuiltInCategory");
            var parameters = EnumMembers(apiMd, "Autodesk.Revit.DB", "BuiltInParameter");
            var viewTypes = EnumMembers(apiMd, "Autodesk.Revit.DB", "ViewType");
            var surfaces = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (var s in Surfaces) surfaces[s] = HasSurface(apiMd, s) || HasSurface(uiMd, s);
            var doc = new Dictionary<string, object>
            {
                ["revit"] = year, ["paquete_referencia"] = ver,
                ["metodo"] = "System.Reflection.Metadata sobre ensamblados de referencia; sin cargar ni ejecutar Revit",
                ["postable_command"] = postable, ["built_in_category"] = categories, ["built_in_parameter_total"] = parameters.Count,
                ["view_type"] = viewTypes, ["superficies"] = surfaces,
            };
            perVersion[year] = doc;
            File.WriteAllText(Path.Combine(outDir, "revit-api-" + year + ".json"), JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine(year + ": PostableCommand " + postable.Count + ", BuiltInCategory " + categories.Count + ", BuiltInParameter " + parameters.Count + ", superficies presentes " + surfaces.Count(kv => kv.Value) + "/" + surfaces.Count);
        }
        // Diferencias entre versiones consecutivas.
        var diff = new List<Dictionary<string, object>>();
        var years = perVersion.Keys.ToList();
        for (var i = 1; i < years.Count; i++)
        {
            var a = perVersion[years[i - 1]]; var b = perVersion[years[i]];
            List<string> L(Dictionary<string, object> d, string k) => (List<string>)d[k];
            var sa = (SortedDictionary<string, bool>)a["superficies"]; var sb = (SortedDictionary<string, bool>)b["superficies"];
            diff.Add(new Dictionary<string, object>
            {
                ["de"] = years[i - 1], ["a"] = years[i],
                ["postable_command_nuevos"] = L(b, "postable_command").Except(L(a, "postable_command")).ToList(),
                ["postable_command_retirados"] = L(a, "postable_command").Except(L(b, "postable_command")).ToList(),
                ["built_in_category_nuevas"] = L(b, "built_in_category").Except(L(a, "built_in_category")).ToList(),
                ["built_in_category_retiradas"] = L(a, "built_in_category").Except(L(b, "built_in_category")).ToList(),
                ["superficies_aparecen"] = sb.Where(kv => kv.Value && !sa[kv.Key]).Select(kv => kv.Key).ToList(),
                ["superficies_desaparecen"] = sa.Where(kv => kv.Value && !sb[kv.Key]).Select(kv => kv.Key).ToList(),
            });
        }
        File.WriteAllText(Path.Combine(outDir, "revit-api-diff.json"), JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return 0;
    }

    /// <summary>
    /// Exporta el catálogo de capacidades (código del motor) y la clasificación de cada PostableCommand de cada versión.
    /// registry.json: contratos de capacidad. commands.json: comando → dominio, versiones, capacidad enlazada y estado
    /// (UNKNOWN si ninguna capacidad lo cubre: clasificar no es soportar). coverage.md: resumen legible.
    /// </summary>
    private static int Registry(string outDir, string inventoryDir)
    {
        Directory.CreateDirectory(outDir);
        var opts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
        var catalog = PDI.RevitMcp.Engine.Coverage.CapabilityCatalog.All.Select(c => new Dictionary<string, object>
        {
            ["capability_id"] = c.Id, ["domain"] = c.Domain, ["subdomain"] = c.Subdomain, ["name"] = c.Name, ["name_en"] = c.NameEn,
            ["operation"] = c.Operation, ["expected_postconditions"] = c.Expected, ["categories"] = c.Categories, ["document_kind"] = c.DocumentKind,
            ["implementation_path"] = c.Strategy, ["api_surface"] = c.ApiSurfaces, ["revit_versions"] = Enumerable.Range(c.MinVersion, c.MaxVersion - c.MinVersion + 1).ToArray(),
            ["status"] = c.Status.ToString(), ["source_files"] = c.Implementation, ["test_ids"] = c.Tests, ["verifier"] = c.Verifier,
            ["idempotency"] = c.Idempotency, ["recovery"] = c.Recovery, ["limitations"] = c.Limitations, ["ui_command"] = c.PostableCommands, ["priority"] = c.Priority,
            ["native_status"] = "not_tested",
        }).ToList();
        File.WriteAllText(Path.Combine(outDir, "registry.json"), JsonSerializer.Serialize(catalog, opts), new UTF8Encoding(false));

        var versions = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var (year, _) in Versions)
        {
            var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(inventoryDir, "revit-api-" + year + ".json")));
            foreach (var e in doc.RootElement.GetProperty("postable_command").EnumerateArray())
            {
                var n = e.GetString();
                if (!versions.TryGetValue(n, out var ys)) versions[n] = ys = new List<int>();
                ys.Add(year);
            }
        }
        var commands = versions.Select(kv =>
        {
            var domain = PDI.RevitMcp.Engine.Coverage.CommandClassifier.Classify(kv.Key, out var byRule);
            var linked = PDI.RevitMcp.Engine.Coverage.CommandClassifier.LinkedCapabilities(kv.Key);
            return new Dictionary<string, object>
            {
                ["command"] = kv.Key, ["domain"] = domain, ["classified_by_rule"] = byRule, ["revit_versions"] = kv.Value,
                ["capabilities"] = linked.Select(c => c.Id).ToArray(),
                ["status"] = linked.Count == 0 ? "UNKNOWN" : linked.Select(c => c.Status).Min().ToString(),
            };
        }).ToList();
        File.WriteAllText(Path.Combine(outDir, "commands.json"), JsonSerializer.Serialize(commands, opts), new UTF8Encoding(false));

        // Resumen legible.
        var sb = new StringBuilder();
        sb.AppendLine("# Cobertura funcional de Revit — registro generado");
        sb.AppendLine();
        sb.AppendLine("Generado por `tools/inventory/RevitApiInventory registry` desde `src/PDI.RevitMcp.Engine/Coverage/CapabilityCatalog.cs` y el inventario offline de la API (`docs/0.9.1/inventory`). No editar a mano.");
        sb.AppendLine();
        sb.AppendLine("Estados: ninguna capacidad es `NATIVE_VERIFIED` mientras no exista la campaña nativa única de 0.9.1. `IMPLEMENTED_UNVERIFIED` = implementación tipada con pruebas de planificación offline; su efecto en Revit está pendiente de campaña.");
        sb.AppendLine();
        sb.AppendLine("## Capacidades por estado");
        sb.AppendLine();
        foreach (var g in PDI.RevitMcp.Engine.Coverage.CapabilityCatalog.All.GroupBy(c => c.Status).OrderBy(g => g.Key))
            sb.AppendLine("- `" + g.Key + "`: " + g.Count());
        sb.AppendLine();
        sb.AppendLine("## Comandos de la cinta (PostableCommand, unión 2022–2026)");
        sb.AppendLine();
        sb.AppendLine("- Total distintos: " + commands.Count);
        sb.AppendLine("- Con capacidad enlazada: " + commands.Count(c => ((string[])c["capabilities"]).Length > 0));
        sb.AppendLine("- Sin analizar (`UNKNOWN`): " + commands.Count(c => (string)c["status"] == "UNKNOWN"));
        sb.AppendLine("- Clasificados por regla de dominio: " + commands.Count(c => (bool)c["classified_by_rule"]) + "; sin regla (F37 por defecto): " + commands.Count(c => !(bool)c["classified_by_rule"]));
        sb.AppendLine();
        sb.AppendLine("## Por dominio");
        sb.AppendLine();
        sb.AppendLine("| Dominio | Capacidades | Estados | Comandos de la cinta |");
        sb.AppendLine("|---|---|---|---|");
        for (var f = 1; f <= 37; f++)
        {
            var d = "F" + f.ToString("00");
            var caps = PDI.RevitMcp.Engine.Coverage.CapabilityCatalog.All.Where(c => c.Domain == d).ToList();
            var states = string.Join(", ", caps.GroupBy(c => c.Status).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count()));
            sb.AppendLine("| " + d + " | " + caps.Count + " | " + states + " | " + commands.Count(c => (string)c["domain"] == d) + " |");
        }
        sb.AppendLine();
        sb.AppendLine("## Capacidades");
        sb.AppendLine();
        sb.AppendLine("| ID | Capacidad | Estado | Estrategia | Limitaciones |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var c in PDI.RevitMcp.Engine.Coverage.CapabilityCatalog.All.OrderBy(c => c.Id, StringComparer.Ordinal))
            sb.AppendLine("| " + c.Id + " | " + c.Name + " | `" + c.Status + "` | " + c.Strategy + " | " + (c.Limitations ?? "").Replace("|", "/") + " |");
        File.WriteAllText(Path.Combine(outDir, "coverage.md"), sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine("capacidades " + catalog.Count + ", comandos " + commands.Count + ", UNKNOWN " + commands.Count(c => (string)c["status"] == "UNKNOWN"));
        return 0;
    }

    /// <summary>
    /// Indexador completo: todos los símbolos públicos de RevitAPI.dll y RevitAPIUI.dll por versión (revit-symbols-&lt;año&gt;.txt.gz),
    /// resumen con trazabilidad (SHA-256 de cada ensamblado de referencia y del listado) y diferencias entre versiones
    /// (introducidos, retirados, modificados, obsoletos nuevos).
    /// </summary>
    private static int SymbolsMode(string outDir, string root)
    {
        Directory.CreateDirectory(outDir);
        var per = new SortedDictionary<int, Dictionary<string, string>>();
        var summary = new List<Dictionary<string, object>>();
        foreach (var (year, ver) in Versions)
        {
            var api = FindDll(root, "nice3point.revit.api.revitapi", ver, "RevitAPI.dll");
            var ui = FindDll(root, "nice3point.revit.api.revitapiui", ver, "RevitAPIUI.dll");
            if (api == null || ui == null) { Console.Error.WriteLine("falta el paquete de referencia " + year + " (" + ver + ")"); return 3; }
            var lines = Symbols.Extract(api).Select(l => "RevitAPI|" + l).Concat(Symbols.Extract(ui).Select(l => "RevitAPIUI|" + l)).OrderBy(l => l, StringComparer.Ordinal).ToList();
            var sha = Symbols.WriteGz(Path.Combine(outDir, "revit-symbols-" + year + ".txt.gz"), lines);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var l in lines)
            {
                var p = l.Split('|');
                map[p[0] + "|" + p[1] + "|" + p[2]] = l;
            }
            per[year] = map;
            var byKind = lines.GroupBy(l => l.Split('|')[1]).ToDictionary(g => g.Key switch { "T" => "tipos", "C" => "constructores", "M" => "metodos", "P" => "propiedades", "F" => "campos", "E" => "eventos", _ => g.Key }, g => (object)g.Count());
            var types = lines.Where(l => l.Split('|')[1] == "T").Select(l => l.Split('|')).ToList();
            summary.Add(new Dictionary<string, object>
            {
                ["revit"] = year, ["paquete_referencia"] = ver,
                ["revitapi_sha256"] = Symbols.Sha256(File.ReadAllBytes(api)), ["revitapiui_sha256"] = Symbols.Sha256(File.ReadAllBytes(ui)),
                ["simbolos"] = lines.Count, ["simbolos_sha256"] = sha, ["por_clase"] = byKind,
                ["tipos_por_tipo"] = types.GroupBy(p => p[3]).ToDictionary(g => g.Key, g => (object)g.Count()),
                ["obsoletos"] = lines.Count(l => l.Contains("|obsolete")),
                ["metodos_estaticos"] = lines.Count(l => l.Split('|')[1] == "M" && l.Contains("|static")),
                ["espacios_de_nombres"] = types.Select(p => { var n = p[2].Split('+')[0]; var d = n.LastIndexOf('.'); return d > 0 ? n.Substring(0, d) : ""; })
                                              .GroupBy(n => n).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count()),
            });
            Console.WriteLine(year + ": " + lines.Count + " símbolos (" + string.Join(", ", byKind.Select(kv => kv.Key + " " + kv.Value)) + ")");
        }
        var diffs = new List<Dictionary<string, object>>();
        var years = per.Keys.ToList();
        for (var i = 1; i < years.Count; i++)
        {
            var a = per[years[i - 1]]; var b = per[years[i]];
            var added = b.Keys.Where(k => !a.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var removed = a.Keys.Where(k => !b.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var changed = b.Keys.Where(k => a.TryGetValue(k, out var old) && old != b[k]).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var newlyObsolete = changed.Where(k => b[k].Contains("|obsolete") && !a[k].Contains("|obsolete")).ToList();
            diffs.Add(new Dictionary<string, object>
            {
                ["de"] = years[i - 1], ["a"] = years[i],
                ["introducidos"] = added.Count, ["retirados"] = removed.Count, ["modificados"] = changed.Count, ["obsoletos_nuevos"] = newlyObsolete.Count,
                ["lista_introducidos"] = added, ["lista_retirados"] = removed,
                ["lista_modificados"] = changed.Select(k => (object)new Dictionary<string, object> { ["antes"] = a[k], ["despues"] = b[k] }).ToList(),
            });
            Console.WriteLine(years[i - 1] + "→" + years[i] + ": +" + added.Count + " −" + removed.Count + " ~" + changed.Count + " obsoletos nuevos " + newlyObsolete.Count);
        }
        var o = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outDir, "revit-symbols-summary.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["metodo"] = "System.Reflection.Metadata sobre ensamblados de referencia Nice3point; sin cargar ni ejecutar Revit. Presencia técnica: no implica semántica, precondiciones ni transacciones.",
            ["formato_listado"] = "Ensamblado|Clase|Símbolo|atributos (T tipo, C constructor, M método, P propiedad, F campo, E evento)",
            ["versiones"] = summary,
        }, o), new UTF8Encoding(false));
        using (var gzOut = File.Create(Path.Combine(outDir, "revit-symbols-diff.json.gz")))
        using (var gz = new System.IO.Compression.GZipStream(gzOut, System.IO.Compression.CompressionLevel.SmallestSize))
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(diffs, o));
            gz.Write(bytes, 0, bytes.Length);
        }
        File.WriteAllText(Path.Combine(outDir, "revit-symbols-diff-summary.json"), JsonSerializer.Serialize(diffs.Select(d => d.Where(kv => !kv.Key.StartsWith("lista_")).ToDictionary(kv => kv.Key, kv => kv.Value)).ToList(), o), new UTF8Encoding(false));
        return 0;
    }

    private static string FindDll(string root, string package, string version, string dll)
    {
        var dir = Path.Combine(root, package, version);
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, dll, SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
    }

    private static TypeDefinition? FindType(MetadataReader md, string ns, string name)
    {
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            if (md.GetString(t.Name) == name && md.GetString(t.Namespace) == ns) return t;
        }
        return null;
    }

    private static List<string> EnumMembers(MetadataReader md, string ns, string name)
    {
        var t = FindType(md, ns, name);
        var res = new List<string>();
        if (t == null) return res;
        foreach (var fh in t.Value.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            var n = md.GetString(f.Name);
            if (n == "value__") continue;
            res.Add(n);
        }
        return res;
    }

    private static bool HasSurface(MetadataReader md, string surface)
    {
        var parts = surface.Split(new[] { "::" }, StringSplitOptions.None);
        var full = parts[0];
        var dot = full.LastIndexOf('.');
        var t = FindType(md, full.Substring(0, dot), full.Substring(dot + 1));
        if (t == null) return false;
        if (parts.Length == 1) return true;
        var member = parts[1];
        foreach (var mh in t.Value.GetMethods()) if (md.GetString(md.GetMethodDefinition(mh).Name) == member) return true;
        foreach (var ph in t.Value.GetProperties()) if (md.GetString(md.GetPropertyDefinition(ph).Name) == member) return true;
        return false;
    }
}
