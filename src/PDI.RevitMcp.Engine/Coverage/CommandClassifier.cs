// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PDI.RevitMcp.Engine.Coverage
{
    /// <summary>
    /// Clasifica cada miembro del enum PostableCommand (inventario por versión) en un dominio F01–F37 por su nombre (0.9.1, §6).
    /// Es una clasificación por dominio, no una afirmación de soporte: el estado de un comando sin capacidad enlazada en
    /// <see cref="CapabilityCatalog"/> es UNKNOWN (sin analizar). Las reglas van de lo específico a lo general; la primera que coincide gana.
    /// </summary>
    public static class CommandClassifier
    {
        private static readonly (string Domain, Regex Rule)[] Rules =
        {
            ("F29", R("CopyMonitor|CoordinationReview|Monitor")),
            ("F30", R("Workset|Synchroni|Relinquish|EditingRequest|Collaborat|CloudModel|Central|ReloadLatest|Borrow")),
            ("F28", R("Link|Import|PointCloud|Coordination(Model|Settings)|ManageLinks|DecalTypes|Decal")),
            ("F22", R("Fabrication|MEPFabrication|Multipoint")),
            ("F20", R("Electrical|Circuit|Panel|Wire|Lighting(Fixture|Device)|ElectricalEquipment|ElectricalFixture|Load(Classification|Summary)|Distribution|Demand|Switch|Conduit|CableTray")),
            ("F21", R("Data(Device)?|Telephone|Communication|Security|NurseCall|FireAlarm")),
            ("F19", R("Sprinkler|FireProtection")),
            ("F18", R("Pipe|Piping|Plumbing|Fixture(?!.*Light)")),
            ("F17", R("Duct|AirTerminal|Mechanical|HVAC|Heating|Cooling|Pressure|Space(Separator|Naming|Tag)?\\b|Zone")),
            ("F16", R("Connector|MEPSettings|MEP|SystemBrowser|Insulation|Lining")),
            ("F15", R("Analytical|Load(s|Case|Combination)|BoundaryCondition|StructuralSettings|AnalysisModel")),
            ("F14", R("Rebar|Reinforcement|Fabric|Cover")),
            ("F13", R("Steel|Connection|Plate|Bolt|Weld|Anchor|ShearStud|Cope|Contour|Skewed")),
            ("F12", R("Structural|Beam|Brace|Column|Foundation|Truss|Footing|Slab")),
            ("F11", R("Topo|Site|Property(Line)?|Building(Pad)?|Subregion|Parking|Planting|GradedRegion|Contour")),
            ("F10", R("Mass|Conceptual|Form|Adaptive|Divide|Repeat")),
            ("F09", R("Room|Area|Tag(Room|Area)|ColorFill|Legend")),
            ("F08", R("Stair|Railing|Ramp|Curtain|Mullion|Grid(Line)?Curtain|Shaft")),
            ("F06", R("Floor|Ceiling|Roof|Fascia|Gutter|Soffit|SlabEdge")),
            ("F05", R("Wall|Sweep|Reveal")),
            ("F07", R("Door|Window|Component|Furniture|Casework|ModelText|ModelLine|ModelGroup|Opening|Hole|ByFace|InPlace")),
            ("F03", R("Level|Grid|ReferencePlane|ScopeBox|Coordinate|ProjectBasePoint|SurveyPoint|Location|TrueNorth|ProjectNorth|Position")),
            ("F23", R("Family|FamilyType|Parameter(s)?(?!Filter)|Extrusion|Blend|Revolve|Sweep|Void|ReferenceLine|LoadAsGroup|LoadShapes|Nested")),
            ("F26", R("Schedule|Quantit|MaterialTakeoff|NoteBlock|ViewList|SheetList|KeySchedule")),
            ("F27", R("Sheet|Titleblock|Viewport|Guide|Revision|Matchline|ViewReference|Placeholder")),
            ("F25", R("Tag|Text|Dimension|Spot|Keynote|Symbol|DetailLine|DetailComponent|DetailGroup|FilledRegion|MaskingRegion|Insulation|Cloud|Annotation|Leader|Arrowhead|ColorScheme|Label|Note")),
            ("F24", R("View|Section|Elevation|Callout|Plan|3D|Camera|Walkthrough|Crop|Visibility|Graphics|Filter|Template|Hide|Isolate|Reveal|Thin|Zoom|Tile|Window|Tab|Underlay|Display|Graphic")),
            ("F31", R("Phase|DesignOption|Group|Assembl|Part|Pin|Unpin|Selection")),
            ("F32", R("Energy|Analysis|Check|Interference|Warning|Review|Inquiry|Measure")),
            ("F33", R("Render|Material(?!Takeoff)|Appearance|Sun|Lighting(?!Fixture)|Decal|Cloud(Rendering)?|Realistic")),
            ("F34", R("Export|Publish|Print|PDF|IFC|DWG|DGN|FBX|NWC|gbXML|Image")),
            ("F04", R("Material|Parameter|ProjectInformation|ProjectUnits|ObjectStyles|LineStyles|LineWeights|LinePatterns|FillPatterns|Snaps|Units|Purge|TransferProjectStandards|GlobalParameter|SharedParameter")),
            ("F02", R("Move|Copy|Rotate|Mirror|Array|Align|Offset|Scale|Split|Trim|Extend|Delete|Match|Paste|Cut|Join|Unjoin|Pick|Select|Measure|Create(Similar)?")),
            ("F35", R("Macro|Dynamo|AddIn|External|API|Script")),
            ("F01", R("Open|Save|Close|New|Recent|Options|Exit|Recover|Backup|Project|Document|Audit|Compact|Upgrade")),
        };

        private static Regex R(string pattern) => new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Dominio del comando; F37 (contextual/interactivo) si ninguna regla coincide, con <paramref name="byRule"/> = false.</summary>
        public static string Classify(string command, out bool byRule)
        {
            foreach (var (domain, rule) in Rules)
                if (rule.IsMatch(command)) { byRule = true; return domain; }
            byRule = false;
            return "F37";
        }

        /// <summary>Capacidades del catálogo que citan este comando.</summary>
        public static List<CapabilityEntry> LinkedCapabilities(string command) =>
            CapabilityCatalog.All.Where(c => c.PostableCommands.Contains(command, StringComparer.Ordinal)).ToList();
    }
}
