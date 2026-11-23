// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Engine.Coverage
{
    /// <summary>
    /// 0.9.1 (universal inventory directive): breakdown of F01–F37 into operational sub-capabilities and linking of ribbon
    /// commands to their capability. Rules of this extension:
    /// - DYNAMIC_CSHARP_ONLY only cites surfaces that EXIST in the full symbol listing (checked by CapabilityCatalogTests);
    ///   existing does not mean the workflow is proven: typed operation, verifier and native test are missing.
    /// - UNKNOWN = not enough evidence: the pending research is recorded instead of inventing support.
    /// - NOT_APPLICABLE = a Revit UI or session function that is not a model operation.
    /// - Linking a command to a typed capability requires the operation to do the same thing; differences go in the limitations.
    /// </summary>
    public static partial class CapabilityCatalog
    {
        private static CapabilityEntry Unknown(string id, string dom, string sub, string name, string nameEn, string op, string expected, string[] cmds, string pending, string prio = "P2") => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = name, NameEn = nameEn, Operation = op, Expected = expected, Strategy = "none",
            Status = CoverageStatus.UNKNOWN, PostableCommands = cmds ?? new string[0], Limitations = "research pending: " + pending, Priority = prio,
        };

        private static CapabilityEntry NotApplicable(string id, string dom, string sub, string name, string nameEn, string[] cmds, string why) => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = name, NameEn = nameEn, Operation = "UI", Expected = "not applicable: " + why, Strategy = "none", DocumentKind = "any",
            Status = CoverageStatus.NOT_APPLICABLE, PostableCommands = cmds ?? new string[0], Limitations = why, Priority = "P3",
        };

        private static CapabilityEntry WithLimits(CapabilityEntry e, string limits) { e.Limitations = limits; return e; }

        private const string DB = "Autodesk.Revit.DB.";

        private static void Expand(List<CapabilityEntry> l)
        {
            var plan = new[] { "PlanTests", "WriteEngineTests" };
            l.AddRange(new[]
            {
                // ---------------- F01 Documents ----------------
                Dyn("F01-DOC-CLOUD-001", "F01", "cloud documents", "Save, open and manage cloud models", "Cloud models", "SAVE", "model saved or opened in ACC/BIM 360 with its cloud path",
                    new[] { DB + "Document::SaveAsCloudModel" }, new[] { "SaveAsCloudModel", "OpenCloudModel", "ManageCloudModels" }, "requires an Autodesk session and permissions on the cloud project", "P2"),
                Dyn("F01-DOC-TEMPLATE-001", "F01", "templates", "Save as template and create project from template", "Templates", "SAVE", ".rte file or new project from the requested template",
                    new[] { DB + "Document::SaveAs" }, new[] { "SaveAsTemplate", "NewProject", "ManageTemplates", "EditATemplate" }),
                Unknown("F01-DOC-STANDARDS-001", "F01", "standards", "Transfer project standards", "Transfer project standards", "UPDATE", "types and styles copied from the source project",
                    new[] { "TransferProjectStandards" }, "no public API equivalent to the dialog has been found; candidate: ElementTransformUtils.CopyElements between documents for types (does not cover every category of the dialog)"),
                Unknown("F01-DOC-REPAIR-001", "F01", "recovery", "Repair central model and restore backup", "Repair central / restore backup", "RECOVER", "model recovered with evidence",
                    new[] { "RepairCentralModel", "RestoreBackup" }, "no public API identified for either dialog; requires a manual workflow"),
                NotApplicable("F01-UI-SESSION-001", "F01", "session", "Exit Revit, application options and sample files", "Session and application UI",
                    new[] { "ExitRevit", "Options", "OpenSampleFiles", "Close", "OpenBuildingComponent" }, "application or user-session actions; they are not model operations (closing a document is already F01-DOC-CLOSE-001)"),

                // ---------------- F02 Editing ----------------
                Dyn("F02-EDIT-PIN-001", "F02", "editing", "Pin and unpin elements", "Pin / unpin", "UPDATE", "elements with Pinned as requested",
                    new[] { DB + "Element::Pinned" }, new[] { "Pin", "Unpin" }, prio: "P2"),
                Dyn("F02-EDIT-PASTE-001", "F02", "editing", "Copy to clipboard and paste aligned (views, levels, same place)", "Paste aligned", "COPY", "copies on the requested levels or views with the correct offset",
                    new[] { DB + "ElementTransformUtils::CopyElements" }, new[] { "CopyToClipboard", "CutToClipboard", "PasteFromClipboard", "AlignedToCurrentView", "AlignedToPickedLevel", "AlignedToSamePlace", "AlignedToSelectedLevels", "AlignedToSelectedViews" },
                    "the Revit clipboard has no API: the equivalent is CopyElements with a transform (typed copy covers translation and level)", "P1"),
                Dyn("F02-EDIT-CUT-001", "F02", "editing", "Cut and join geometry, switch join order", "Cut / join geometry", "JOIN", "cut/join relationships as requested",
                    new[] { DB + "SolidSolidCutUtils::AddCutBetweenSolids", DB + "InstanceVoidCutUtils::AddInstanceVoidCut", DB + "JoinGeometryUtils::SwitchJoinOrder" }, new[] { "CutGeometry", "UncutGeometry", "SwitchJoinOrder", "CutBy" }),
                Unknown("F02-EDIT-TRIM-001", "F02", "editing", "Trim/extend and split with gap", "Trim / extend / split with gap", "UPDATE", "location curves trimmed or extended",
                    new[] { "TrimOrExtendToCorner", "TrimOrExtendSingleElement", "TrimOrExtendMultipleElements", "SplitWithGap", "DisplaceElements" }, "there is no public trim method; possible via LocationCurve.Curve per category, to be validated per element type"),
                WithLimits(Typed("F02-EDIT-MATCH-001", "F02", "editing", "Match type properties", "Match type properties", "UPDATE", "change_type", "elements with the type of the reference element", plan, new[] { "MatchTypeProperties" }),
                    "change_type needs the explicit type; the ribbon \"brush\" takes it from a picked element. Effect in Revit pending the native campaign"),
                WithLimits(Typed("F02-EDIT-SIMILAR-001", "F02", "editing", "Create similar (place another of the same type)", "Create similar", "CREATE", "place", "instance of the same type at the requested position", plan, new[] { "CreateSimilar" }),
                    "place with the type of the reference element; hosted families require a host. Effect in Revit pending the native campaign"),
                Dyn("F02-SEL-FILTER-001", "F02", "selection", "Save, load and edit saved selections", "Saved selections", "QUERY", "SelectionFilterElement with the requested elements",
                    new[] { DB + "SelectionFilterElement::Create" }, new[] { "SaveSelection", "LoadSelection", "EditSelection", "AddToSet" }, "BIMBRIDGE named sets ($conjunto) live in the session; this persists in the model", "P2"),
                NotApplicable("F02-UI-SELECT-001", "F02", "selection", "Select by id, selection ids and undo/redo", "Selection UI and undo",
                    new[] { "SelectById", "IdsOfSelection", "Undo", "Redo", "ActivateControlsAndDimensions" }, "UI tools; BIMBRIDGE reads ids with query and undoes with one TransactionGroup per plan"),

                // ---------------- F03 Coordinates and datums ----------------
                Dyn("F03-COORD-SHARED-001", "F03", "coordinates", "Shared coordinates: report, specify, relocate and rotate north", "Shared coordinates", "UPDATE", "ProjectLocation/SiteLocation with the requested position and north",
                    new[] { DB + "ProjectLocation", DB + "SiteLocation" }, new[] { "ReportSharedCoordinates", "SpecifyCoordinatesAtPoint", "ResetSharedCoordinates", "RelocateProject", "RotateProjectNorth", "RotateTrueNorth", "Location", "MirrorProject" }, prio: "P1"),
                Dyn("F03-WORKPLANE-001", "F03", "work planes", "Set and show work plane", "Work plane", "UPDATE", "active SketchPlane as requested",
                    new[] { DB + "SketchPlane::Create" }, new[] { "SetWorkPlane", "ShowWorkPlane", "PickAPlane", "FormWorkPlaneView" }),

                // ---------------- F04 Standards ----------------
                Dyn("F04-STD-STYLES-001", "F04", "styles", "Line and fill patterns, line styles and line weights, object styles", "Line/fill patterns and object styles", "UPDATE", "patterns and styles created or adjusted",
                    new[] { DB + "LinePatternElement::Create", DB + "FillPatternElement::Create", DB + "Category::SetLineWeight" }, new[] { "LinePatterns", "FillPatterns", "LineStyles", "LineWeights", "ObjectStyles", "Arrowheads" }),
                Dyn("F04-PROJ-INFO-001", "F04", "project", "Project information and units", "Project information and units", "UPDATE", "ProjectInfo and Units as requested",
                    new[] { DB + "ProjectInfo", DB + "Document::SetUnits" }, new[] { "ProjectInformation", "ProjectUnits" }, prio: "P1"),
                Dyn("F04-PARAM-PROJ-001", "F04", "parameters", "Project parameters and family category parameters", "Project parameters", "CREATE", "parameters bound to the requested categories",
                    new[] { DB + "ParameterElement" }, new[] { "ProjectParameters", "FamilyCategoryAndParameters", "ParametersService" }, prio: "P1"),
                Dyn("F04-KEYNOTE-001", "F04", "keynotes", "Keynoting settings", "Keynoting settings", "UPDATE", "keynote table loaded", new[] { DB + "KeynoteTable" }, new[] { "KeynotingSettings" }),
                Unknown("F04-MAT-ASSET-001", "F04", "materials", "Material appearance and physical assets", "Material assets", "UPDATE", "material with the requested assets",
                    new[] { "MaterialAssets" }, "AppearanceAssetElement exists but asset editing has per-version restrictions; not verified"),

                // ---------------- F05 Walls ----------------
                Dyn("F05-WALL-OPEN-001", "F05", "walls", "Wall opening", "Wall opening", "CREATE", "rectangular opening in the wall", new[] { "Autodesk.Revit.Creation.Document::NewOpening" }, new[] { "WallOpening" }),
                Dyn("F05-WALL-JOINS-001", "F05", "walls", "Wall joins (allow/disallow at ends)", "Wall joins", "JOIN", "end join as requested", new[] { DB + "WallUtils::DisallowWallJoinAtEnd" }, new[] { "WallJoins" }),
                Unknown("F05-WALL-SWEEP-001", "F05", "walls", "Wall sweeps and reveals, wall by face", "Wall sweeps / reveals / by face", "CREATE", "sweep or reveal on the wall",
                    new[] { "SweepWall", "RevealWall", "WallByFaceWall" }, "WallSweep.Create exists in some versions; wall by face depends on masses; not verified"),

                // ---------------- F06 Floors, roofs, openings ----------------
                Dyn("F06-OPENING-001", "F06", "openings", "Vertical, shaft, dormer and by-face openings", "Shaft / vertical / dormer openings", "CREATE", "opening with the requested boundary and levels",
                    new[] { "Autodesk.Revit.Creation.Document::NewOpening" }, new[] { "ShaftOpening", "VerticalOpening", "DormerOpening", "OpeningByFace", "Opening" }, prio: "P1"),
                Unknown("F06-EDGES-001", "F06", "edges", "Fascias, gutters, soffits and slab edges; floor and roof by face", "Fascia, gutter, soffit, slab edge, by face", "CREATE", "profile swept along the requested edges",
                    new[] { "Fascia", "Gutter", "Soffit", "SlabEdgeFloor", "FloorByFaceFloor", "RoofByFace", "JoinOrUnjoinRoof" }, "creation API for these sweeps not confirmed in the listing; needs per-version research"),

                // ---------------- F07 Components ----------------
                Dyn("F07-MODELLINE-001", "F07", "model lines", "Model and symbolic lines", "Model / symbolic lines", "CREATE", "model curves on the requested work plane",
                    new[] { "Autodesk.Revit.Creation.ItemFactoryBase::NewModelCurve" }, new[] { "ModelLine", "SymbolicLine" }),
                Dyn("F07-MODELTEXT-001", "F07", "model text", "3D model text", "Model text", "CREATE", "model text with the requested type and text", new[] { DB + "ModelText" }, new[] { "ModelText" }, prio: "P3"),
                Unknown("F07-INPLACE-001", "F07", "in-place families", "Model in place and draw on face/work plane", "Model in place", "CREATE", "in-place family", new[] { "ModelInPlace", "DrawOnFace", "DrawOnWorkPlane", "ArchitecturalColumn", "RepeatComponent" },
                    "in-place modeling has no public API for editing in-place families; placement on face/plane are place options"),
                WithLimits(Typed("F16-MEP-PLACE-001", "F16", "MEP", "Place MEP equipment, terminals, fixtures and devices (loaded family)", "Place MEP equipment and devices", "CREATE", "place",
                    "instance of the requested type at the requested position and level", plan,
                    new[] { "AirTerminal", "MechanicalEquipment", "MechanicalControlDevice", "PlumbingFixture", "PlumbingEquipment", "ElectricalEquipment", "ElectricalFixture", "LightingFixture", "Communication", "Telephone", "NurseCall", "Security", "Control" }),
                    "generic placement: does not connect to systems or connectors, nor host on faces. Effect in Revit pending the native campaign"),

                // ---------------- F08 Circulation and curtain systems ----------------
                Dyn("F08-STAIR-EDIT-001", "F08", "stairs", "Path, numbering and elements on stairs/ramps", "Stair path, numbering, place on stair", "CREATE", "annotation or element on the stair",
                    new[] { DB + "StairsEditScope" }, new[] { "StairPath", "StairTreadOrRiserNumber", "PlaceOnStairOrRamp", "Ramp" }),
                Unknown("F08-CURTAIN-SYS-001", "F08", "curtain walls", "Curtain system by face and mullions", "Curtain system by face, mullions", "CREATE", "curtain system with grid and mullions",
                    new[] { "CurtainSystemByFace", "CurtainWallMullion" }, "mullions via CurtainGrid/Mullion; system by face depends on masses; not verified"),

                // ---------------- F09 Rooms and areas ----------------
                Dyn("F09-BOUNDARY-001", "F09", "boundaries", "Room and space separators", "Room / space separators", "CREATE", "separation lines that bound the room",
                    new[] { "Autodesk.Revit.Creation.Document::NewRoomBoundaryLines", "Autodesk.Revit.Creation.Document::NewSpaceBoundaryLines" }, new[] { "RoomSeparator", "SpaceSeparator" }, prio: "P1"),
                Dyn("F09-AREA-001", "F09", "areas", "Areas, area boundaries and area/volume computation", "Areas and area boundaries", "CREATE", "bounded area in the requested scheme",
                    new[] { DB + "AreaScheme", "Autodesk.Revit.Creation.Document::NewAreaBoundaryLine", DB + "ViewPlan::CreateAreaPlan" }, new[] { "Area", "AreaBoundary", "AreaAndVolumeComputations" }, prio: "P1"),
                Dyn("F09-TAG-001", "F09", "tags", "Room, area and space tags", "Room / area / space tags", "TAG", "tag in the requested view on the room",
                    new[] { "Autodesk.Revit.Creation.Document::NewRoomTag", "Autodesk.Revit.Creation.Document::NewAreaTag" }, new[] { "RoomTag", "AreaTag", "SpaceTag" }, "typed tag uses IndependentTag and does not tag rooms", "P1"),
                Dyn("F09-COLORFILL-001", "F09", "color schemes", "Color schemes and fill legends", "Color fill schemes", "UPDATE", "color scheme applied and legend placed",
                    new[] { DB + "ColorFillScheme" }, new[] { "ColorFillLegend", "ColorSchemes", "DuctLegend", "PipeLegend" }),
                Dyn("F09-SPACE-001", "F09", "spaces", "MEP spaces and their naming", "MEP spaces", "CREATE", "bounded space with name and number",
                    new[] { "Autodesk.Revit.Creation.Document::NewSpace" }, new[] { "Space", "SpaceNaming" }, prio: "P1"),

                // ---------------- F10–F11 Massing and site ----------------
                Unknown("F10-MASS-002", "F10", "masses", "Conceptual masses: create, place, show and mass floors", "Conceptual mass", "CREATE", "mass with its form and floors",
                    new[] { "NewConceptualMass", "PlaceMass", "ShowMassByViewSettings", "ShowMassFormAndFloors", "ShowMassSurfaceTypes", "ShowMassZonesAndShades", "SplitFace", "Paint", "RemovePaint", "PanelByBoundary", "PanelByExtrusion" },
                    "editing conceptual forms in mass families; some have API (FamilyItemFactory), others UI only"),
                Dyn("F11-PAD-001", "F11", "site", "Building pads", "Building pad", "CREATE", "pad with boundary and level", new[] { DB + "Architecture.BuildingPad::Create" }, new[] { "BuildingPad" }),
                Unknown("F11-SITE-002", "F11", "site", "Subregions, property lines, contours, graded region and site components", "Site tools", "CREATE", "site elements",
                    new[] { "Subregion", "PropertyLine", "PropertyLineData", "LabelContours", "GradedRegion", "SplitSurface", "MergeSurfaces", "SiteComponent", "ParkingComponent", "ToposolidByFace", "ToposolidSmoothShading", "TopographyCutVoidStability", "LinkTopography" },
                    "topography surfaces (2022–2023) and Toposolid (2024+) have different APIs; each tool needs its own verification"),

                // ---------------- F12–F15 Structure ----------------
                Dyn("F12-BEAMSYS-001", "F12", "structure", "Beam systems and trusses", "Beam systems and trusses", "CREATE", "beam system or truss with its type",
                    new[] { DB + "BeamSystem::Create", DB + "Structure.Truss::Create" }, new[] { "AutomaticBeamSystem", "StructuralTrusses", "BeamSystemSymbol", "TopChord", "BottomChord", "Web" }),
                Unknown("F12-JOINS-001", "F12", "structure", "Beam/column joins and beam annotations", "Beam/column joins, beam annotations", "UPDATE", "joins adjusted",
                    new[] { "BeamOrColumnJoins", "BeamAnnotations", "Brace", "StructuralPlan", "Slab", "MajorSegment", "Miter" }, "partly in the StructuralFramingUtils API; not verified per tool"),
                Dyn("F13-STEEL-CONN-001", "F13", "steel", "Structural connections", "Structural connections", "CREATE", "connection between the requested elements with its type",
                    new[] { DB + "Structure.StructuralConnectionHandler::Create" }, new[] { "StructuralConnection", "ConnectionSettings" }, prio: "P2"),
                Unknown("F13-STEEL-FAB-002", "F13", "steel", "Plates, bolts, welds, anchors, shear studs, copes and cuts", "Steel fabrication elements", "CREATE", "steel fabrication elements",
                    new[] { "Plate", "Bolts", "Welds", "Anchors", "ShearStuds", "Cope", "CopeSkewed", "ApplyCoping", "RemoveCoping", "CornerCut", "SawCutFlange", "SawCutWeb", "Holes", "Shorten", "ContourCut", "CutThrough", "CutProfile" },
                    "Advance Steel API (Autodesk.Advance.*) outside RevitAPI.dll; not inventoried here"),
                Dyn("F14-REBAR-SETS-001", "F14", "rebar", "Area, path and fabric reinforcement and couplers; covers", "Area/path/fabric reinforcement, couplers, cover", "CREATE", "reinforcement with its type and cover",
                    new[] { DB + "Structure.AreaReinforcement::Create", DB + "Structure.PathReinforcement::Create", DB + "Structure.FabricArea::Create", DB + "Structure.FabricSheet::Create", DB + "Structure.RebarCoupler::Create", DB + "Structure.RebarCoverType::Create" },
                    new[] { "StructuralAreaReinforcement", "StructuralPathReinforcement", "StructuralFabricArea", "SingleFabricSheetPlacement", "StructuralRebarCoupler", "InsertCoupler", "RebarCoverSettings", "EditRebarCover", "ReinforcementSettings", "ReinforcementNumbers", "AreaReinforcementSymbol", "PathReinforcementSymbol", "FabricReinforcementSymbol", "AllowableBarTypes", "RebarBendingDetail", "AlignedMultiRebarAnnotation", "LinearMultiRebarAnnotation", "LoadRebarShapeIntoProjectAndClose" }),
                Dyn("F15-LOADS-001", "F15", "loads", "Structural load cases, combinations and loads; analytical links", "Load cases, combinations, loads, analytical links", "CREATE", "loads and combinations with their nature",
                    new[] { DB + "Structure.LoadCase::Create", DB + "Structure.LoadCombination::Create", DB + "Structure.PointLoad::Create", DB + "Structure.AnalyticalLink::Create" },
                    new[] { "LoadCases", "LoadCombinations", "LoadCasesAndCombinations", "Loads", "LoadCasesDisplay", "LoadsScalingDisplay", "AnalyticalLink", "CreateAnalyticalLink", "BoundaryConditions", "BoundaryConditionsSettings", "AnalyticalMember", "AdjustAnalyticalModel", "ResetAnalyticalModel", "AnalyticalAutomation", "RunAnalyticalToPhysicalForBuildings", "RunPhysicalToAnalyticalForBuildings" }),

                // ---------------- F17–F22 MEP ----------------
                Dyn("F17-FLEX-001", "F17", "ducts", "Flex duct and convert to flex", "Flex duct", "CREATE", "connected flex duct", new[] { DB + "Mechanical.FlexDuct::Create" }, new[] { "FlexDuct", "ConvertToFlexDuct" }, prio: "P1"),
                Dyn("F17-PLACEHOLDER-001", "F17", "ducts", "Duct and pipe placeholders and their conversion", "Duct / pipe placeholders", "CREATE", "placeholder created or converted to duct/pipe",
                    new[] { DB + "Mechanical.Duct::CreatePlaceholder", DB + "Mechanical.MechanicalUtils::ConvertDuctPlaceholders", DB + "Plumbing.Pipe::CreatePlaceholder", DB + "Plumbing.PlumbingUtils::ConvertPipePlaceholders" }, new[] { "DuctPlaceholder", "PipePlaceholder" }, prio: "P1"),
                Dyn("F17-INSUL-001", "F17", "insulation", "Duct and pipe insulation", "Duct / pipe insulation", "CREATE", "insulation with type and thickness",
                    new[] { DB + "Mechanical.DuctInsulation::Create", DB + "Plumbing.PipeInsulation::Create" }, new[] { "Insulation" }),
                Unknown("F17-FITTINGS-001", "F17", "fittings", "Duct/pipe/cable tray/conduit fittings, accessories and connectors", "Fittings, accessories, connectors", "CREATE", "fitting connected at the requested point",
                    new[] { "DuctFitting", "DuctAccessory", "DuctConnector", "PipeFitting", "PipeAccessory", "PipeConnector", "CableTrayFitting", "CableTrayConnector", "ConduitFitting", "ConduitConnector", "ElectricalConnector", "ParallelPipes", "ParallelConduits", "MultiPointRouting", "OneWayIndicator" },
                    "Document.NewElbowFitting/NewTeeFitting and family connectors; they need a connectivity contract (F16) before being typed"),
                Unknown("F17-REPORTS-001", "F17", "analysis", "Pressure loss reports and mechanical settings", "Pressure loss reports and mechanical settings", "VERIFY", "report with the system sections",
                    new[] { "DuctPressureLossReport", "PipePressureLossReport", "MechanicalSettings", "SystemsAnalysis", "SystemZone", "Zone", "ZoneBySketch", "ZoneBySpace", "BuildingOperatingSchedules", "OperatingSchedules", "BuildingOrSpaceTypeSettings" },
                    "pressure report API not identified; zones and system analysis change between 2022 and 2026"),
                Dyn("F18-FLEX-001", "F18", "piping", "Flex pipe", "Flex pipe", "CREATE", "connected flex pipe", new[] { DB + "Plumbing.FlexPipe::Create" }, new[] { "FlexPipe" }),
                Dyn("F20-SETTINGS-001", "F20", "electrical", "Electrical settings, demand factors, load classifications and wires", "Electrical settings", "UPDATE", "electrical settings as requested",
                    new[] { DB + "Electrical.ElectricalSetting" }, new[] { "ElectricalSettings", "DemandFactors", "LoadClassifications", "ElectricalConductorAndCableSettings", "ElectricalAnalyticalLoadTypeSettings" }, prio: "P1"),
                Unknown("F20-ANALYTICAL-001", "F20", "electrical", "Equipment loads and area-based loads (electrical analysis)", "Equipment and area-based loads", "CREATE", "analytical load with its classification",
                    new[] { "EquipmentLoad", "CreateAnAreaBasedLoad", "AreaBasedLoadBoundary" }, "electrical analytical load API introduced in recent versions; verify per version"),
                Dyn("F22-FAB-PART-001", "F22", "fabrication", "MEP fabrication parts and settings", "Fabrication parts", "CREATE", "fabrication part with service and button",
                    new[] { DB + "FabricationPart::Create" }, new[] { "FabricationPart", "FabricationSettings", "MEPFabricationDuctworkStiffener" }),
                Unknown("F22-PID-001", "F22", "P&ID", "P&ID Modeler", "P&ID Modeler", "CREATE", "model from P&ID", new[] { "PAndIDModeler", "PAndIDSettings" }, "Autodesk add-in with its own API; not inventoried"),

                // ---------------- F23 Family Editor ----------------
                Dyn("F23-FAM-GEOM-001", "F23", "family geometry", "Solids and voids: extrusion, blend, revolve, sweep and swept blend", "Family solids and voids", "CREATE", "form with its sketch and plane",
                    new[] { "Autodesk.Revit.Creation.FamilyItemFactory::NewExtrusion", "Autodesk.Revit.Creation.FamilyItemFactory::NewBlend", "Autodesk.Revit.Creation.FamilyItemFactory::NewRevolution", "Autodesk.Revit.Creation.FamilyItemFactory::NewSweep", "Autodesk.Revit.Creation.FamilyItemFactory::NewSweptBlend" },
                    new[] { "SolidExtrusion", "SolidBlend", "SolidRevolve", "SolidSweep", "SolidSweptBlend", "VoidExtrusion", "VoidBlend", "VoidRevolve", "VoidSweep", "VoidSweptBlend", "ReferenceLine", "Label" }, prio: "P1"),
                Dyn("F23-FAM-TYPES-001", "F23", "family types", "Family types", "Family types", "CREATE", "new type with its values", new[] { DB + "FamilyManager::NewType" }, new[] { "FamilyTypes" }, prio: "P1"),
                Dyn("F23-FAM-NEW-001", "F23", "families", "New family, titleblock or symbol; save as library family", "New family / save as library", "CREATE", "new family file",
                    new[] { DB + "Document::SaveAs" }, new[] { "NewFamily", "NewTitleBlock", "NewAnnotationSymbol", "SaveAsFamily", "SaveAsLibraryFamily", "SaveAsLibraryView", "SaveAsLibraryGroup", "LoadIntoProject", "LoadFamilyIntoProjectAndClose" }),
                Unknown("F23-FAM-CLOUD-001", "F23", "families", "Load Autodesk family (cloud content)", "Load Autodesk family", "CREATE", "family downloaded and loaded", new[] { "LoadAutodeskFamily" }, "cloud service with no known public API"),

                // ---------------- F24 Views ----------------
                Dyn("F24-VIEW-FILTER-001", "F24", "filters", "View filters and filter overrides", "View filters", "UPDATE", "filter created and applied with its graphics",
                    new[] { DB + "ParameterFilterElement::Create", DB + "View::SetFilterOverrides" }, new[] { "OverrideByFilter" }, prio: "P1"),
                Dyn("F24-VIEW-TEMPLATE-001", "F24", "view templates", "Create template from view and apply its parameters", "View templates", "CREATE", "template created or applied",
                    new[] { DB + "View::CreateViewTemplate", DB + "View::ApplyViewTemplateParameters" }, new[] { "CreateTemplateFromCurrentView" }),
                Dyn("F24-VIEW-CAMERA-001", "F24", "views", "Camera (perspective 3D view) and structural plan", "Camera / structural plan", "CREATE", "view with the requested orientation and type",
                    new[] { DB + "View3D::CreatePerspective", DB + "ViewPlan::Create" }, new[] { "Camera" }),
                Unknown("F24-VIEW-MISC-001", "F24", "views", "Plan region, per-element hidden lines, walkthrough and view tags", "Plan region, hidden lines, walkthrough, view tags", "UPDATE", "view with the requested configuration",
                    new[] { "PlanRegion", "ShowHiddenLinesByElement", "RemoveHiddenLinesByElement", "Walkthrough", "SectionTags", "ElevationTags", "CalloutTags", "Linework", "HalftoneOrUnderlay", "ShowBoundaryOpenEnds", "HideBoundaryOpenEnds", "Isolated", "Multiplanar", "SharedViews" },
                    "each tool has a different API or none; not verified"),
                NotApplicable("F24-UI-001", "F24", "view UI", "Navigation, windows, ViewCube and UI display modes", "View UI",
                    new[] { "ViewCube", "NavigationBar", "TabViews", "TileViews", "CloseInactiveViews", "ThinLines", "ToggleRevealHiddenElementsMode", "ActivateView", "DeactivateView", "StartingView" },
                    "UI or window state; does not change model data that BIMBRIDGE must plan"),

                // ---------------- F25 Annotation ----------------
                Unknown("F25-DIM-002", "F25", "dimensions", "Angular, radial, diameter and arc length dimensions; temporary dimensions", "Angular / radial / diameter / arc length dimensions", "CREATE", "dimension with the requested references",
                    new[] { "AngularDimension", "RadialDimension", "DiameterDimension", "ArcLengthDimension", "AngularDimensionTypes", "RadialDimensionTypes", "DiameterDimensionTypes", "LinearDimensionTypes", "AlignmentStation", "AlignmentStationLabelTypes", "AlignmentStationSet" },
                    "NewDimension only creates linear dimensions; the others need version-specific API"),
                Dyn("F25-SPOT-001", "F25", "spot dimensions", "Spot elevations, spot coordinates and spot slopes", "Spot elevation / coordinate / slope", "CREATE", "spot dimension on the requested reference",
                    new[] { "Autodesk.Revit.Creation.Document::NewSpotElevation", "Autodesk.Revit.Creation.Document::NewSpotCoordinate" }, new[] { "SpotElevation", "SpotCoordinate", "SpotSlope", "SpotElevationTypes", "SpotCoordinateTypes", "SpotSlopeTypes" }),
                Unknown("F25-KEYNOTE-001", "F25", "keynotes", "Element, material and user keynotes; keynote legend", "Keynotes", "TAG", "keynote placed with its value",
                    new[] { "ElementKeynote", "MaterialKeynote", "UserKeynote", "KeynoteLegend", "MaterialTag", "MultiCategoryTag", "LoadedTagsAndSymbols", "SpanDirectionSymbol", "MultipleValuesIndication", "MeasureAlongAnElement", "MeasureBetweenTwoReferences" },
                    "IndependentTag covers some; verify tag types per category"),
                WithLimits(Typed("F25-TAG-ALL-001", "F25", "tags", "Tag all not tagged (per view)", "Tag all not tagged", "TAG", "tag", "one tag per untagged element in the view", plan, new[] { "TagAllNotTagged" }),
                    "composition: query of untagged elements + tag; does not tag rooms. Effect in Revit pending the native campaign"),
                WithLimits(Typed("F25-SYMBOL-001", "F25", "symbols", "Place annotation symbol", "Place annotation symbol", "CREATE", "detail_component", "symbol (view-based family) in the view", new[] { "Draw2DTests", "PlanTests" }, new[] { "Symbol" }),
                    "view-based families; no leaders. Effect in Revit pending the native campaign"),
                Unknown("F25-DETAIL-002", "F25", "detail", "Masking region, repeating detail component, detail insulation, legends and legend components", "Masking region, repeating detail, legends", "CREATE", "detail element in the view",
                    new[] { "MaskingRegion", "RepeatingDetailComponent", "Legend", "LegendComponent", "Matchline" }, "masking region = FilledRegion with a masking type (not verified); legends have no direct creation API"),

                // ---------------- F26–F27 Schedules, sheets, printing ----------------
                Dyn("F26-SCHED-SPECIAL-001", "F26", "schedules", "Sheet lists, view lists, note blocks, revision schedules, material takeoffs and key schedules", "Special schedules", "SCHEDULE", "special schedule with its fields",
                    new[] { DB + "ViewSchedule::CreateSheetList", DB + "ViewSchedule::CreateViewList", DB + "ViewSchedule::CreateNoteBlock", DB + "ViewSchedule::CreateRevisionSchedule", DB + "ViewSchedule::CreateMaterialTakeoff", DB + "ViewSchedule::CreateKeySchedule" },
                    new[] { "SheetList", "ViewList", "NoteBlock", "RevisionSchedule", "MaterialTakeoff" }, "typed schedule_create only creates category schedules", "P1"),
                Unknown("F26-SCHED-GRAPH-001", "F26", "schedules", "Graphical column schedule", "Graphical column schedule", "SCHEDULE", "graphical schedule", new[] { "GraphicalColumnSchedule" }, "no creation API identified"),
                WithLimits(Typed("F27-SHEET-VIEW-001", "F27", "sheets", "Place view on sheet", "Place view", "CREATE", "sheet_place", "viewport on the sheet", plan, new[] { "PlaceView" }), "effect in Revit pending the native campaign"),
                Dyn("F27-SHEET-PH-001", "F27", "sheets", "Placeholder sheets and titleblock", "Placeholder sheets, title block", "CREATE", "placeholder sheet or titleblock placed",
                    new[] { DB + "ViewSheet::CreatePlaceholder" }, new[] { "TitleBlock", "GuideGrid", "ViewReference" }),
                Dyn("F27-PRINT-001", "F27", "printing", "Print, print setup and print sets", "Print / print setup", "EXPORT", "print job with the requested setup",
                    new[] { DB + "Document::Print", DB + "PrintManager", DB + "ViewSheetSet" }, new[] { "Print", "PrintSetup", "PrintPreview", "BatchPrint" }, "system printer: external effect without rollback; doc pdf covers PDF", "P2"),

                // ---------------- F28 Links ----------------
                Dyn("F28-LINK-IFC-001", "F28", "links", "Link and open IFC", "Link / open IFC", "CREATE", "IFC link with its transform",
                    new[] { DB + "RevitLinkType::CreateFromIFC" }, new[] { "LinkIFC", "OpenIFC", "OpenIFCOptions" }, prio: "P1"),
                Dyn("F28-IMAGE-PDF-001", "F28", "links", "Link or import image and PDF; manage images", "Link / import image and PDF", "CREATE", "image or PDF page placed in the view",
                    new[] { DB + "ImageType::Create" }, new[] { "LinkImage", "ImportImage", "ManageImages", "LinkPDF", "ImportPDF" }, "PDF as ImageType per version-specific options; not verified", "P1"),
                Dyn("F28-POINTCLOUD-001", "F28", "links", "Point clouds", "Point clouds", "CREATE", "point cloud instance", new[] { DB + "PointCloudInstance" }, new[] { "PointCloud" }),
                Unknown("F28-MANAGE-001", "F28", "links", "Manage links, coordination models, FormIt and insert from file", "Manage links, coordination models, insert from file", "UPDATE", "links reloaded or unloaded",
                    new[] { "ManageLinks", "CoordinationModel", "CoordinationModelAutodeskDocs", "CoordinationModelChanges", "CoordinationModelLocal", "LinkFormit", "Insert2DElementsFromFile", "InsertViewsFromFile", "CreateFromImport", "ImportTypes", "ImportGBXML", "ManageConnectionToARevitServerAccelerator", "DecalTypes", "PlaceDecal" },
                    "RevitLinkType.Reload/Unload exist; coordination models and FormIt not verified"),

                // ---------------- F30–F31 Collaboration and organization ----------------
                Dyn("F30-OWN-001", "F30", "worksharing", "Relinquish all mine, reload latest and enable collaboration", "Relinquish / reload latest / enable worksharing", "SYNC", "ownership relinquished or model reloaded",
                    new[] { DB + "WorksharingUtils::RelinquishOwnership", DB + "Document::ReloadLatest", DB + "Document::EnableWorksharing" }, new[] { "RelinquishAllMine", "ReloadLatest", "Collaborate" }, prio: "P1"),
                Unknown("F30-CLOUD-001", "F30", "worksharing", "Cloud collaboration, editing requests, history and publishing", "Cloud collaboration, editing requests, history", "SYNC", "collaboration state",
                    new[] { "CollaborateInCloud", "EditingRequests", "ShowHistory", "PublishSettings" }, "no public API identified for these dialogs"),
                Dyn("F31-PARTS-001", "F31", "parts", "Create parts", "Create parts", "CREATE", "parts of the requested elements", new[] { DB + "PartUtils::CreateParts" }, new[] { "CreateParts" }),
                WithLimits(Typed("F31-DEMOLISH-001", "F31", "phases", "Demolish (phase demolished)", "Demolish", "UPDATE", "set_param", "elements with the requested phase demolished", plan, new[] { "Demolish" }),
                    "set_param on the phase demolished parameter; the ribbon tool uses the view's phase. Effect in Revit pending the native campaign"),
                WithLimits(Typed("F31-GROUP-DETAIL-001", "F31", "groups", "Place detail group", "Place detail group", "CREATE", "group_place", "group placed", plan, new[] { "PlaceDetailGroup" }), "effect in Revit pending the native campaign"),
                Unknown("F31-GROUP-LOAD-001", "F31", "groups", "Load group into open projects and assign assembly code", "Load group into projects, assembly code", "CREATE", "group loaded into the requested projects",
                    new[] { "LoadAsGroupIntoOpenProjects", "AssemblyCode" }, "no verified equivalent API"),

                // ---------------- F32–F34 Analysis, visualization and export ----------------
                Dyn("F32-ENERGY-002", "F32", "energy analysis", "Create and delete energy model; generate analysis", "Energy model", "CREATE", "energy analysis model",
                    new[] { DB + "Analysis.EnergyAnalysisDetailModel::Create" }, new[] { "CreateEnergyModel", "DeleteEnergyModel", "Generate", "GenerateAnalysis", "Optimize", "ViewAnalysis", "Lighting" }),
                Dyn("F32-PATH-001", "F32", "spatial analysis", "Path of travel", "Path of travel", "CREATE", "path between two points with its length", new[] { DB + "Analysis.PathOfTravel::Create" }, new[] { "PathOfTravel", "RevealObstacles", "SpatialGrid", "PeopleContent" }),
                Unknown("F33-RENDER-002", "F33", "visualization", "Render, gallery, cloud and sun settings", "Render and sun settings", "EXPORT", "rendered image", new[] { "Render", "RenderInCloud", "RenderGallery", "SunSettings", "AnalysisDisplayStyles" },
                    "render engine has no public launch API; SunAndShadowSettings does exist for the sun"),
                Dyn("F34-EXPORT-OTHER-001", "F34", "export", "Export FBX, DWF/DWFx, DGN, STL, OBJ, SAT and gbXML", "Export FBX/DWF/DGN/STL/OBJ/SAT/gbXML", "EXPORT", "file in the requested format",
                    new[] { DB + "FBXExportOptions", DB + "DWFExportOptions", DB + "DGNExportOptions", DB + "STLExportOptions", DB + "OBJExportOptions", DB + "SATExportOptions", DB + "GBXMLExportOptions" },
                    new[] { "ExportFBX", "ExportDWFOrDWFx", "ExportCADFormatsDGN", "ExportCADFormatsSTL", "ExportCADFormatsOBJ", "ExportCADFormatsACIS_SAT", "ExportGBXML", "ExportOptionsExportSetupsDGN" }, prio: "P2"),
                Dyn("F34-EXPORT-STEP-001", "F34", "export", "Export STEP (2025+)", "Export STEP", "EXPORT", "STEP file", new[] { DB + "STEPExportOptions" }, new[] { "ExportCADFormatsSTEP" }, "Revit 2025–2026 only (inventory)"),
                Unknown("F34-EXPORT-REPORTS-001", "F34", "export", "Room/area reports, ODBC, animations and types", "Room/area report, ODBC, animations, export types", "EXPORT", "report file",
                    new[] { "ExportReportsRoomOrAreaReport", "ExportODBCDatabase", "ExportImagesandAnimationsSolarStudy", "ExportImagesandAnimationsWalkthrough", "ExportExportTypes", "DWFMarkup" }, "no public API identified for these wizards"),

                // ---------------- F35 Automation (other tools) ----------------
                NotApplicable("F35-OTHER-TOOLS-001", "F35", "automation", "Dynamo, macros, add-in manager and customization", "Dynamo, macros, add-ins",
                    new[] { "Dynamo", "DynamoPlayer", "DynamoPlayerForSteel", "MacroManager", "MacroSecurity", "AddInsManager", "AutomationCustomization", "KeyboardShortcuts", "CanvasTheme" },
                    "other automation or UI customization tools; BIMBRIDGE has its own engine (exec, tasks, plans)"),

                // ---------------- F36 QA/QC ----------------
                new CapabilityEntry
                {
                    Id = "F36-HEALTH-001", Domain = "F36", Subdomain = "model health", Name = "Model health: warnings, duplicates, empty parameters and disconnected MEP connectors", NameEn = "Model health",
                    Operation = "VERIFY", Expected = "report with warnings by type, duplicates at the same position, empty required parameters and unconnected connectors, with ids",
                    Strategy = "query:check model_health", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED,
                    Implementation = new[] { "src/Bimbridge.Engine/Qa/ModelHealth.cs", "src/Bimbridge.Addin/Revit/ModelHealthProbe.cs" }, Tests = new[] { "ModelHealthTests" },
                    PostableCommands = new[] { "ReviewWarnings", "ShowWarningsInViews", "ShowDisconnects", "CheckDuctSystems", "CheckPipeSystems", "AnalyticalConsistencyChecks", "CheckMemberSupports" },
                    Verifier = "offline rules on the read inventory (ModelHealthTests); reading in Revit pending the campaign",
                    Limitations = "read-only; Revit's structural checks (supports, analytical consistency) are not reproduced. Reading in Revit pending the native campaign", Priority = "P1",
                },
                Unknown("F37-CONTEXT-001", "F37", "contextual modes", "Contextual edit and sketch modes (edit sketch, center reference, form state, paths, overrides, reconcile hosting)", "Contextual edit/sketch modes", "UPDATE",
                    "element edited in its contextual mode with verification", new[] { "PickToEdit", "ReCenterReference", "ShapeStatus", "MultiplePaths", "SubstitutionSettings", "ReconcileHosting" },
                    "depend on the active UI edit mode; no equivalent public API identified for each"),
                Unknown("F02-EDIT-SCALE-001", "F02", "editing", "Scale elements", "Scale", "UPDATE", "elements scaled about a point", new[] { "Scale" },
                    "Revit only scales certain 2D elements, images and links; no generic API method"),
                Unknown("F25-TEXT-REPLACE-001", "F25", "text", "Find and replace text", "Find / replace text", "UPDATE", "text notes with the text replaced", new[] { "FindOrReplace" },
                    "feasible with TextNote.Text + set_param, but no contract or verifier yet"),
                Unknown("F15-SETTINGS-001", "F15", "structure", "Structural settings", "Structural settings", "UPDATE", "structural settings as requested", new[] { "StructuralSettings" },
                    "structural settings class still to be located in the per-version listing"),
                Engine("F35-HISTORY-001", "F35", "long operations", "Operation history with filters, cancellation and safe-retry advice", "Operation history and safe retry", "QUERY",
                    "each finished job is persisted with status, duration and whether it is retryable or requires reconciliation; in-progress ones with progress",
                    new[] { "src/Bimbridge.Engine/OperationHistory.cs", "src/Bimbridge.Engine/Jobs.cs" }, new[] { "OperationHistoryTests" },
                    "history and JobStore with synthetic jobs", "query via check name=history (own scope; others' only with shared_history) and the \"Long operations\" section of the status panel with filter and Cancel; the WPF window has no automated test"),
                Engine("F35-SCHED-001", "F35", "long operations", "Long-operation scheduler across agents", "Long-operation scheduler", "UPDATE",
                    "global and per-agent limit, one operation at a time per document, fair sharing, bounded queue with refusal, and cancellation while queued without effects",
                    new[] { "src/Bimbridge.Engine/OperationScheduler.cs" }, new[] { "OperationSchedulerTests" },
                    "deterministic tests of ordering, limits, exclusion, cancellation and failure", "governs document operations (save, sync, export); commit and exec keep the Revit thread's own queue"),
                NotApplicable("F37-UI-002", "F37", "ui", "UI palettes, status bars, themes and controls", "UI palettes and bars",
                    new[] { "ProjectBrowser", "SystemBrowser", "StatusBar", "StatusBarDesignOptions", "StatusBarWorksets", "TogglePropertiesPalette", "ToggleHome", "TogglePAndIDModelerBrowser", "LaunchAsApp", "LaunchInBrowser", "LaunchInDockedWindow", "Snaps", "TemporaryDimensions", "ShowWorksharingMakeEditableControls", "GrayInactiveWorksets", "WorksharingMonitor", "ShowLastReport", "CheckSpelling", "BrowserOrganization" },
                    "Revit UI state; not model operations"),
            });

            // Ribbon commands that map to existing capabilities (same function; differences in their limitations).
            var extra = new Dictionary<string, string[]>
            {
                ["F24-VIEW-DUP-001"] = new[] { "DuplicateView", "DuplicateAsDependent", "DuplicateWithDetailing" },
                ["F24-VIEW-OVERRIDE-001"] = new[] { "OverrideByCategory", "OverrideByElement" },
                ["F24-VIEW-SET-001"] = new[] { "DetailLevel", "SelectionBox" },
                ["F03-DATUM-SCOPEBOX-002"] = new[] { "ScopeBox" },
                ["F25-DIM-001"] = new[] { "LinearDimension" },
                ["F01-DOC-PDF-001"] = new[] { "PDF" },
                ["F34-EXPORT-IFC-001"] = new[] { "ExportOptionsIFCOptions" },
                ["F34-EXPORT-DWG-001"] = new[] { "ExportOptionsExportSetupsDWGOrDXF" },
                ["F33-RENDER-IMG-001"] = new[] { "ExportImagesandAnimationsImage" },
                ["F32-GEO-CLASH-001"] = new[] { "RunInterferenceCheck" },
                ["F29-CM-BATCH-001"] = new[] { "CoordinationSettings" },
                ["F04-PARAM-TYPE-001"] = new[] { "TypeProperties" },
                ["F05-WALL-CREATE-001"] = new[] { "Wall" },
            };
            foreach (var kv in extra)
            {
                var e = l.FirstOrDefault(c => c.Id == kv.Key);
                if (e == null) throw new System.InvalidOperationException("Expand: does not exist " + kv.Key);
                e.PostableCommands = e.PostableCommands.Concat(kv.Value).Distinct().ToArray();
            }
        }
    }
}
