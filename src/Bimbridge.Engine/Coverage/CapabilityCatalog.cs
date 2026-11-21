// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Engine.Coverage
{
    /// <summary>Official coverage statuses (0.9.1 directive, §4.3). SUPPORTED is never inferred from UNKNOWN.</summary>
    public enum CoverageStatus
    {
        NATIVE_VERIFIED,
        OFFLINE_VERIFIED,
        IMPLEMENTED_UNVERIFIED,
        PARTIAL,
        DYNAMIC_CSHARP_ONLY,
        UI_ASSISTED,
        MANUAL_ONLY,
        MISSING,
        UNKNOWN,
        UNSUPPORTED_BY_PUBLIC_API,
        NOT_APPLICABLE,
    }

    /// <summary>Semantic contract of a functional Revit capability versus BIMBRIDGE (§2.1, §3.2, §4.3).</summary>
    public sealed class CapabilityEntry
    {
        public string Id;
        public string Domain;          // F01..F37
        public string Subdomain;
        public string Name;            // descriptive name
        public string NameEn;
        public string Operation;       // CREATE, READ, QUERY, UPDATE, DELETE, COPY, MOVE, ROTATE, MIRROR, TAG, SCHEDULE, EXPORT, SAVE, CONNECT, VERIFY…
        public string Expected;        // expected functional result
        public string[] Categories = new string[0];
        public string DocumentKind = "project";   // project | family | any
        public string Strategy;        // typed:<op> | query:<op> | doc:<verb> | fam:<verb> | exec | postcommand | manual | none
        public string[] ApiSurfaces = new string[0];   // inventory keys (docs/0.9.1/inventory/revit-api-*.json)
        public int MinVersion = 2022;
        public int MaxVersion = 2026;
        public CoverageStatus Status;
        public string[] Implementation = new string[0];   // code files (repository paths)
        public string[] Tests = new string[0];            // test classes (or Class.Method) that cover it
        public string Verifier;        // how the real result is checked
        public string Idempotency;
        public string Recovery;
        public string Limitations;
        public string[] PostableCommands = new string[0]; // related ribbon commands (PostableCommand enum)
        public string Priority = "P2";
    }

    /// <summary>
    /// Capability catalog F01–F37 (0.9.1, Block B). It is product code, not just documentation: the engine can report truthfully
    /// what each capability can do (#23). Rules checked by the tests (CapabilityCatalogTests):
    /// - No capability is NATIVE_VERIFIED without a native campaign for the commit (there is none yet).
    /// - IMPLEMENTED_* / OFFLINE_VERIFIED / PARTIAL cite implementation files that exist; OFFLINE_VERIFIED cites tests that exist.
    /// - DYNAMIC_CSHARP_ONLY and UNSUPPORTED_BY_PUBLIC_API cite the API surface (or its absence) from the per-version inventory.
    /// - Every PostableCommand of each version 2022–2026 is classified into a domain (see <see cref="CommandClassifier"/>).
    /// Existing typed strategies are marked IMPLEMENTED_UNVERIFIED: they have offline planning tests, but their effect in Revit
    /// is only proven by the single 0.9.1 native campaign.
    /// </summary>
    public static partial class CapabilityCatalog
    {
        private const string PlanBuilder = "src/Bimbridge.Addin/Revit/Writes/PlanBuilder.cs";
        private const string PlanApplier = "src/Bimbridge.Addin/Revit/Writes/PlanApplier.cs";
        private const string OpCatalogFile = "src/Bimbridge.Engine/OpCatalog.cs";
        private const string QueryRunner = "src/Bimbridge.Addin/Revit/QueryRunner.cs";
        private const string ExecHandler = "src/Bimbridge.Addin/Revit/RequestHandler.Exec.cs";
        private const string FamilyOps = "src/Bimbridge.Addin/Revit/Families/FamilyOps.cs";
        private const string DocOps = "src/Bimbridge.Addin/Revit/Families/DocOps.cs";
        private const string Draw2D = "src/Bimbridge.Engine/Draw2D.cs";

        private static CapabilityEntry Typed(string id, string dom, string sub, string es, string en, string op, string strategyOp, string expected, string[] tests, string[] cmds = null, string[] cats = null, string limits = null, string prio = "P1") => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = op, Expected = expected, Strategy = "typed:" + strategyOp,
            Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { PlanBuilder, PlanApplier, OpCatalogFile }, Tests = tests ?? new string[0],
            Verifier = "plan postconditions + model reread after commit (per-element diff)", Idempotency = "clave= per commit; unique plan_id",
            Recovery = "TransactionGroup + durable intent; reconciliation by plan_id", PostableCommands = cmds ?? new string[0], Categories = cats ?? new string[0],
            Limitations = limits ?? "effect in Revit pending the single 0.9.1 native campaign", Priority = prio,
        };

        /// <summary>0.9.1 (Blocks C/D/E): typed modeling operation: offline validation in Engine.ModelOps and verification of what was created.</summary>
        private static CapabilityEntry Model(string id, string dom, string sub, string es, string en, string op, string strategyOp, string expected, string[] api, string[] cmds = null, string[] cats = null, string limits = null, string prio = "P1")
        {
            var e = Typed(id, dom, sub, es, en, op, strategyOp, expected, new[] { "ModelOpsTests" }, cmds, cats, limits, prio);
            e.Implementation = e.Implementation.Concat(new[] { "src/Bimbridge.Engine/ModelOps.cs", "src/Bimbridge.Addin/Revit/Writes/PlanBuilder.Model.cs", "src/Bimbridge.Addin/Revit/Writes/PlanApplier.Model.cs" }).ToArray();
            e.ApiSurfaces = api ?? new string[0];
            e.Verifier = "reread of each created element against the request (verificacion_creacion)";
            return e;
        }

        private static CapabilityEntry Dyn(string id, string dom, string sub, string es, string en, string op, string expected, string[] api, string[] cmds = null, string limits = null, string prio = "P2") => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = op, Expected = expected, Strategy = "exec",
            Status = CoverageStatus.DYNAMIC_CSHARP_ONLY, Implementation = new[] { ExecHandler }, ApiSurfaces = api ?? new string[0], PostableCommands = cmds ?? new string[0],
            Verifier = "no typed verifier: the script must return evidence; pending a typed operation with a verifier",
            Idempotency = "not guaranteed (arbitrary code)", Recovery = "durable intent + audit; no rollback of external effects",
            Limitations = limits ?? "reachable only with dynamic C#: missing typed operation, verifier and tests", Priority = prio,
        };

        private static CapabilityEntry Missing(string id, string dom, string sub, string es, string en, string op, string expected, string[] api = null, string[] cmds = null, string limits = null, string prio = "P2") => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = op, Expected = expected, Strategy = "none",
            Status = CoverageStatus.MISSING, ApiSurfaces = api ?? new string[0], PostableCommands = cmds ?? new string[0],
            Limitations = limits ?? "not implemented in BIMBRIDGE", Priority = prio,
        };

        private static CapabilityEntry NoApi(string id, string dom, string sub, string es, string en, string op, string expected, string[] missingApi, string[] cmds, string limits, string prio = "P1") => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = op, Expected = expected, Strategy = "postcommand",
            Status = CoverageStatus.UNSUPPORTED_BY_PUBLIC_API, ApiSurfaces = missingApi, PostableCommands = cmds, Limitations = limits, Priority = prio,
        };

        private static CapabilityEntry Engine(string id, string dom, string sub, string es, string en, string op, string expected, string[] impl, string[] tests, string verifier, string limits = null) => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = op, Expected = expected, Strategy = "engine", DocumentKind = "any",
            Status = CoverageStatus.OFFLINE_VERIFIED, Implementation = impl, Tests = tests, Verifier = verifier, Limitations = limits, Priority = "P0",
        };

        public static readonly IReadOnlyList<CapabilityEntry> All = Build();

        private static List<CapabilityEntry> Build()
        {
            var plan = new[] { "PlanTests", "WriteEngineTests" };
            var draw = new[] { "Draw2DTests", "PlanTests" };
            var l = new List<CapabilityEntry>
            {
                // ---------------- F01 Documents and projects ----------------
                new CapabilityEntry { Id = "F01-DOC-READ-001", Domain = "F01", Subdomain = "documents", Name = "List open documents with stable identity", NameEn = "List open documents", Operation = "READ", Expected = "dN handles, durable key, RVT/RFA type, modified flag", Strategy = "query:ctx", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/Bimbridge.Addin/Revit/DocRegistry.cs" }, Tests = new[] { "IdentityJournalTests" }, Verifier = "document fingerprint", Priority = "P0" },
                new CapabilityEntry { Id = "F01-DOC-SAVE-001", Domain = "F01", Subdomain = "documents", Name = "Save and save as", NameEn = "Save / Save As", Operation = "SAVE", Expected = "file on disk verified by date and hash", Strategy = "doc:guardar", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { DocOps }, Tests = new[] { "AgentHostRevitTests" }, Verifier = "verificar_disco: file date, size and hash", PostableCommands = new[] { "Save", "SaveAsProject" }, Priority = "P0" },
                new CapabilityEntry { Id = "F01-DOC-SYNC-001", Domain = "F01", Subdomain = "documents", Name = "Synchronize with central", NameEn = "Synchronize with Central", Operation = "SAVE", Expected = "sync confirmed by the DocumentSynchronizedWithCentral event", Strategy = "doc:sync", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { DocOps }, Tests = new[] { "WorksharedPolicyTests" }, Verifier = "sync event + date", PostableCommands = new[] { "SynchronizeAndModifySettings", "SynchronizeNow" }, Priority = "P1" },
                new CapabilityEntry { Id = "F01-DOC-CLOSE-001", Domain = "F01", Subdomain = "documents", Name = "Close document", NameEn = "Close document", Operation = "UPDATE", Expected = "document closed without losing unsaved changes", Strategy = "doc:cerrar", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { DocOps }, Verifier = "the handle no longer exists", Priority = "P2" },
                Dyn("F01-DOC-OPEN-001", "F01", "documents", "Open project or family from path", "Open document", "READ", "document opened and registered with a handle", new[] { "Autodesk.Revit.DB.Document::EditFamily" }, new[] { "OpenRevitFile", "OpenProject", "OpenFamily" }, "opening models requires explicit authorization for the model; not automated on the user's projects"),
                Missing("F01-DOC-PURGE-001", "F01", "documents", "Purge unused elements", "Purge unused", "DELETE", "unused elements deleted with an evidence list", null, new[] { "PurgeUnused" }),
                new CapabilityEntry { Id = "F01-DOC-PDF-001", Domain = "F01", Subdomain = "export", Name = "Export sheets to PDF", NameEn = "Export to PDF", Operation = "EXPORT", Expected = "PDF files per sheet verified on disk", Strategy = "doc:pdf", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { DocOps }, ApiSurfaces = new[] { "Autodesk.Revit.DB.PDFExportOptions" }, Verifier = "expected files present and not empty", PostableCommands = new[] { "ExportPDF" }, Priority = "P1" },

                // ---------------- F02 Selection and transformations ----------------
                Typed("F02-XFORM-MOVE-001", "F02", "transformations", "Move elements", "Move", "MOVE", "move", "elements moved by exactly the requested vector", plan, new[] { "Move" }),
                Typed("F02-XFORM-ROTATE-001", "F02", "transformations", "Rotate elements", "Rotate", "ROTATE", "rotate", "elements rotated about the requested axis", plan, new[] { "Rotate" }),
                Typed("F02-XFORM-MIRROR-001", "F02", "transformations", "Mirror elements", "Mirror", "MIRROR", "mirror", "mirrored copy about the requested plane", plan, new[] { "MirrorPickAxis", "MirrorDrawAxis" }),
                Typed("F02-XFORM-COPY-001", "F02", "transformations", "Copy elements", "Copy", "COPY", "copy", "copies with the requested offset and their new ids", plan, new[] { "Copy" }),
                Typed("F02-XFORM-DELETE-001", "F02", "transformations", "Delete elements", "Delete", "DELETE", "delete", "elements deleted (and declared dependents)", plan, new[] { "Delete" }),
                Dyn("F02-XFORM-ARRAY-001", "F02", "transformations", "Linear or radial array", "Array", "ARRAY", "arrayed copies with or without grouping", new[] { "Autodesk.Revit.DB.ElementTransformUtils::CopyElements" }, new[] { "Array" }),
                Dyn("F02-XFORM-ALIGN-001", "F02", "transformations", "Alinear", "Align", "ALIGN", "aligned elements (with optional lock)", null, new[] { "Align" }),
                Dyn("F02-XFORM-OFFSET-001", "F02", "transformations", "Desfase", "Offset", "OFFSET", "copy offset perpendicularly", null, new[] { "Offset" }),
                new CapabilityEntry { Id = "F02-SEL-SHOW-001", Domain = "F02", Subdomain = "selection", Name = "Select and zoom to elements for the user", NameEn = "Select and zoom", Operation = "READ", Expected = "active selection and zoomed view", Strategy = "query:show", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/Bimbridge.Addin/Revit/RequestHandler.cs" }, Tests = new[] { "EndToEndTests" }, Verifier = "selection read after the operation", Priority = "P2" },
                new CapabilityEntry { Id = "F02-SEL-SETS-001", Domain = "F02", Subdomain = "selection", Name = "Named id sets bound to a document", NameEn = "Named selection sets", Operation = "QUERY", Expected = "reusable set that never resolves elements of another document", Strategy = "engine", DocumentKind = "any", Status = CoverageStatus.OFFLINE_VERIFIED, Implementation = new[] { "src/Bimbridge.Engine/Sessions.cs" }, Tests = new[] { "DurabilityTests" }, Verifier = "document-set binding", Priority = "P1" },

                // ---------------- F03 Datums and coordinates ----------------
                Model("F03-DATUM-LEVEL-001", "F03", "levels", "Create level", "Create level", "CREATE", "level_create", "level at the requested elevation with optional floor plan and RCP views", new[] { "Autodesk.Revit.DB.Level::Create" }, new[] { "Level" }),
                Model("F03-DATUM-GRID-001", "F03", "grids", "Create straight grid", "Create grid", "CREATE", "grid_create", "straight grid with its name", new[] { "Autodesk.Revit.DB.Grid::Create" }, new[] { "Grid" }, limits: "straight grids only; arcs and multi-segment grids still go through dynamic C#. Effect in Revit pending the native campaign"),
                Dyn("F03-DATUM-REFPLANE-001", "F03", "reference planes", "Create reference plane", "Create reference plane", "CREATE", "named reference plane", new[] { "Autodesk.Revit.DB.ReferencePlane" }, new[] { "ReferencePlane" }),
                Typed("F03-DATUM-SCOPEBOX-001", "F03", "scope boxes", "Assign scope box to views", "Assign scope box to views", "ASSOCIATE", "view_set", "views cropped by the requested scope box (view_set scope_box)", plan),
                new CapabilityEntry { Id = "F03-DATUM-SCOPEBOX-002", Domain = "F03", Subdomain = "scope boxes", Name = "Create scope box", NameEn = "Create scope box", Operation = "CREATE", Expected = "scope box with extents and name", Strategy = "none", Status = CoverageStatus.UNKNOWN, PostableCommands = new[] { "ScopeBox" }, Limitations = "no public API for creating scope boxes has been identified; investigate before classifying", Priority = "P3" },
                Dyn("F03-COORD-READ-001", "F03", "coordinates", "Read shared coordinates and base points", "Read shared coordinates", "READ", "base point, survey point and shared transform", new[] { "Autodesk.Revit.DB.ProjectLocation", "Autodesk.Revit.DB.BasePoint", "Autodesk.Revit.DB.InternalOrigin" }, new[] { "AcquireCoordinates", "PublishCoordinates" }, prio: "P1"),

                // ---------------- F04 Parameters and materials ----------------
                Typed("F04-PARAM-SET-001", "F04", "parameters", "Set instance parameter", "Set instance parameter", "UPDATE", "set_param", "value written and reread equal (units verified)", plan, cats: new[] { "*" }, prio: "P0"),
                Typed("F04-PARAM-VALUES-001", "F04", "parameters", "Set different values per element", "Set per-element values", "UPDATE", "set_values", "each element with its value; skipped ones explained", plan, prio: "P0"),
                Typed("F04-PARAM-TYPE-001", "F04", "parameters", "Set type parameter", "Set type parameter", "UPDATE", "set_type_param", "type value written and reread", plan, prio: "P1"),
                Typed("F04-TYPE-CHANGE-001", "F04", "types", "Change element type", "Change type", "RETYPE", "change_type", "elements with the requested type", plan, prio: "P0"),
                new CapabilityEntry { Id = "F04-PARAM-READ-001", Domain = "F04", Subdomain = "parameters", Name = "Query parameters (instance and type)", NameEn = "Read parameters", Operation = "QUERY", Expected = "compact paginated table with a revision-bound cursor", Strategy = "query:query", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { QueryRunner, "src/Bimbridge.Engine/ResultTable.cs" }, Tests = new[] { "QuerySnapshotTests", "QueryTests" }, Verifier = "row equality between cursor pagination and classic read (offline)", Priority = "P0" },
                Dyn("F04-PARAM-SHARED-001", "F04", "shared parameters", "Add shared parameter to categories", "Add shared parameter", "CREATE", "parameter bound to categories with a group", new[] { "Autodesk.Revit.DB.SharedParameterElement" }, new[] { "SharedParameters" }, prio: "P1"),
                Dyn("F04-PARAM-GLOBAL-001", "F04", "global parameters", "Create and associate global parameter", "Global parameters", "CREATE", "global parameter with value and associations", new[] { "Autodesk.Revit.DB.GlobalParameter::Create" }, new[] { "GlobalParameters" }),
                Dyn("F04-MAT-CREATE-001", "F04", "materials", "Create or duplicate material", "Create material", "CREATE", "material with appearance and properties", new[] { "Autodesk.Revit.DB.Material::Create" }, new[] { "Materials" }),

                // ---------------- F05–F08 Architecture ----------------
                Model("F05-WALL-CREATE-001", "F05", "walls", "Create walls along a polyline", "Create wall", "CREATE", "wall_create", "one wall per segment with the requested type, level, height (or top level) and offset", new[] { "Autodesk.Revit.DB.Wall::Create" }, new[] { "ArchitecturalWall", "StructuralWall" }, limits: "straight segments; no forced joins or profile walls. Effect in Revit pending the native campaign"),
                Dyn("F05-WALL-JOIN-001", "F05", "walls", "Join or unjoin geometry", "Join / unjoin geometry", "JOIN", "geometry joins as requested", new[] { "Autodesk.Revit.DB.JoinGeometryUtils::JoinGeometry" }, new[] { "JoinGeometry", "UnjoinGeometry" }),
                Missing("F05-WALL-SPLIT-001", "F05", "walls", "Split wall", "Split wall", "SPLIT", "two walls with the same type and hosted elements reassigned", null, new[] { "SplitElement" }),
                Model("F06-FLOOR-CREATE-001", "F06", "floors", "Create floor from boundary", "Create floor", "CREATE", "floor_create", "floor of the requested type and level with a closed boundary and offset", new[] { "Autodesk.Revit.DB.Floor::Create" }, new[] { "ArchitecturalFloor", "StructuralFloor" }, limits: "one boundary without openings or slope. Effect in Revit pending the native campaign"),
                Model("F06-CEIL-CREATE-001", "F06", "ceilings", "Create ceiling from boundary", "Create ceiling", "CREATE", "ceiling_create", "ceiling of the requested type at the requested height above the level", new[] { "Autodesk.Revit.DB.Ceiling::Create" }, new[] { "AutomaticCeiling" }, limits: "explicit boundary (no automatic room detection). Effect in Revit pending the native campaign"),
                Dyn("F06-ROOF-CREATE-001", "F06", "roofs", "Create roof by footprint or extrusion", "Create roof", "CREATE", "roof with defined slopes", new[] { "Autodesk.Revit.DB.FootPrintRoof", "Autodesk.Revit.DB.ExtrusionRoof" }, new[] { "RoofByFootprint", "RoofByExtrusion" }),
                Typed("F07-COMP-PLACE-001", "F07", "components", "Place component (loaded family)", "Place component", "CREATE", "place", "instance of the requested type at the requested position, level and host", plan, new[] { "PlaceAComponent" }, prio: "P0"),
                Typed("F07-COMP-SNAP-001", "F07", "components", "Snap to surface (real mounting)", "Snap to host surface", "UPDATE", "snap_to_surface", "element at the height of the real face (includes links)", new[] { "GeoComputeTests", "PlanTests" }, prio: "P1"),
                Dyn("F07-DOOR-WIN-001", "F07", "doors and windows", "Place door or window in wall", "Place door / window", "CREATE", "opening hosted in the wall with orientation", new[] { "Autodesk.Revit.DB.FamilyInstance" }, new[] { "Door", "Window" }, prio: "P1"),
                Dyn("F08-STAIR-CREATE-001", "F08", "stairs", "Create stair by components", "Create stair", "CREATE", "stair with runs and landings", new[] { "Autodesk.Revit.DB.Architecture.Stairs" }, new[] { "Stair" }, "stair editing requires StairsEditScope; no typed verifier"),
                Dyn("F08-RAIL-CREATE-001", "F08", "railings", "Create railing along a path", "Create railing", "CREATE", "railing of the requested type", new[] { "Autodesk.Revit.DB.Architecture.Railing::Create" }, new[] { "Railing" }),
                Dyn("F08-CURTAIN-001", "F08", "curtain walls", "Edit curtain wall grid", "Curtain grid", "UPDATE", "grid lines and panels as requested", new[] { "Autodesk.Revit.DB.CurtainGrid" }, new[] { "CurtainGrid" }),

                // ---------------- F09 Rooms and areas ----------------
                Model("F09-ROOM-CREATE-001", "F09", "rooms", "Create room in a closed loop", "Create room", "CREATE", "room_create", "bounded room (area > 0) with name and number", new[] { "Autodesk.Revit.DB.Architecture.Room" }, new[] { "Room" }),
                new CapabilityEntry { Id = "F09-ROOM-READ-001", Domain = "F09", Subdomain = "rooms", Name = "Query rooms, areas and spaces", NameEn = "Query rooms", Operation = "QUERY", Expected = "name, number, level, area and boundary status", Strategy = "query:query", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { QueryRunner }, Tests = new[] { "QuerySnapshotTests" }, Verifier = "query table", Priority = "P1" },
                Missing("F09-AREA-PLAN-001", "F09", "areas", "Create area plan and scheme", "Area plan", "CREATE", "area plan with scheme", new[] { "Autodesk.Revit.DB.Area" }, new[] { "AreaPlan" }),

                // ---------------- F10–F11 Massing and site ----------------
                Missing("F10-MASS-001", "F10", "masses", "Create in-place conceptual mass", "In-place mass", "CREATE", "mass with faces for by-face elements", null, new[] { "InPlaceMass" }, "conceptual modeling depends on the mass editor; no typed operation", "P3"),
                Dyn("F11-TOPO-001", "F11", "topography", "Create toposolid (2024+) or topography surface (≤2023)", "Create toposolid / topography", "CREATE", "terrain from points or boundary", new[] { "Autodesk.Revit.DB.Toposolid::Create", "Autodesk.Revit.DB.Architecture.TopographySurface" }, new[] { "Toposolid", "Toposurface" }, "Toposolid exists since 2024 per the inventory; 2022–2023 use TopographySurface"),

                // ---------------- F12–F15 Structure ----------------
                Typed("F12-STR-PLACE-001", "F12", "structure", "Place structural column or beam (loaded family)", "Place structural column / beam", "CREATE", "place", "structural instance with level and type", plan, new[] { "StructuralColumn", "Beam" }, prio: "P2", limits: "generic placement via place; no specific structural parameters or join verification"),
                Dyn("F13-STEEL-001", "F13", "steel", "Structural steel connections and elements", "Structural steel connections", "CREATE", "steel connection between members with its type", new[] { "Autodesk.Revit.DB.Structure.StructuralConnectionHandler", "Autodesk.Revit.DB.Steel.SteelElementProperties" }, null, "connections depend on the installed connection types; no typed verifier", "P3"),
                Dyn("F14-REBAR-001", "F14", "reinforcement", "Create rebar from curves", "Create rebar", "CREATE", "rebar with shape and covers", new[] { "Autodesk.Revit.DB.Structure.Rebar::CreateFromCurves" }, new[] { "StructuralRebar", "RebarLine" }),
                Dyn("F15-ANALYTICAL-001", "F15", "analytical model", "Read or create analytical elements (2023+)", "Analytical model", "READ", "analytical elements associated with the physical ones", new[] { "Autodesk.Revit.DB.Structure.AnalyticalElement", "Autodesk.Revit.DB.Structure.AnalyticalMember" }, null, "AnalyticalModel (≤2022) is replaced by AnalyticalElement in 2023 per the inventory"),

                // ---------------- F16–F22 MEP ----------------
                new CapabilityEntry { Id = "F16-CONN-FAM-001", Domain = "F16", Subdomain = "connectors", Name = "Create or adopt connector in family", NameEn = "Family connector", Operation = "CREATE", Expected = "connector on the requested face without duplicates", Strategy = "fam:conector", DocumentKind = "family", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { FamilyOps }, Tests = new[] { "FamilyLibraryTests" }, Verifier = "connector read after the transaction", Priority = "P1" },
                Dyn("F16-CONN-CONNECT-001", "F16", "connectors", "Connect MEP elements by connectors", "Connect MEP elements", "CONNECT", "paired connectors and a common system", new[] { "Autodesk.Revit.DB.Connector", "Autodesk.Revit.DB.ConnectorManager" }, null, prio: "P1"),
                Model("F17-DUCT-CREATE-001", "F17", "ducts", "Create duct between points", "Create duct", "CREATE", "duct_create", "straight duct with type, system, level and size", new[] { "Autodesk.Revit.DB.Mechanical.Duct::Create" }, new[] { "Duct" }, limits: "between points; connection to connectors and fittings still go through dynamic C#. Effect in Revit pending the native campaign"),
                Dyn("F17-SYS-CREATE-001", "F17", "systems", "Create mechanical system", "Create mechanical system", "CREATE", "system with base equipment and terminals", new[] { "Autodesk.Revit.DB.Mechanical.MechanicalSystem::Create" }, null),
                Model("F18-PIPE-CREATE-001", "F18", "piping", "Create pipe between points", "Create pipe", "CREATE", "pipe_create", "straight pipe with type, system, level and diameter", new[] { "Autodesk.Revit.DB.Plumbing.Pipe::Create" }, new[] { "Pipe" }, limits: "between points; connection to connectors and fittings still go through dynamic C#. Effect in Revit pending the native campaign"),
                Dyn("F18-SYS-CREATE-001", "F18", "systems", "Create piping system", "Create piping system", "CREATE", "system with connected elements", new[] { "Autodesk.Revit.DB.Plumbing.PipingSystem::Create" }, null),
                Missing("F19-SPRINKLER-001", "F19", "fire protection", "Place and connect sprinklers", "Place sprinklers", "CREATE", "sprinklers connected to the wet or dry system", null, new[] { "Sprinkler" }),
                Model("F20-CIRCUIT-CREATE-001", "F20", "circuits", "Create power circuit", "Create power circuit", "CREATE", "circuit_create", "circuit with the requested members (free power connector checked at plan and apply time)", new[] { "Autodesk.Revit.DB.Electrical.ElectricalSystem::Create" }, new[] { "CheckCircuits" }, limits: "verifies members and panel; voltage and pole compatibility is decided by Revit (if it rejects it, the plan is rolled back). Effect in Revit pending the native campaign", prio: "P0"),
                Model("F20-CIRCUIT-PANEL-001", "F20", "circuits", "Assign panel when creating the circuit", "Select panel", "ASSOCIATE", "circuit_create", "circuit fed by the requested panel (BaseEquipment verified)", new[] { "Autodesk.Revit.DB.Electrical.ElectricalSystem::Create" }, limits: "only at creation; reassigning the panel of an existing circuit still goes through dynamic C#. Effect in Revit pending the native campaign", prio: "P0"),
                Dyn("F20-WIRE-CREATE-001", "F20", "wiring", "Create wiring", "Create wire", "CREATE", "wire with type and vertices", new[] { "Autodesk.Revit.DB.Electrical.Wire::Create" }, new[] { "ArcWire", "ChamferedWire", "SplineWire" }, prio: "P1"),
                Model("F20-CONDUIT-001", "F20", "raceways", "Create conduit segment", "Create conduit segment", "CREATE", "conduit_create", "straight segment with type, level, diameter and optional workset", new[] { "Autodesk.Revit.DB.Electrical.Conduit::Create" }, new[] { "Conduit" }, limits: "placement between points; pending native verification"),
                Model("F20-CONDUIT-ROUTE-001", "F20", "raceways", "Create conduit route with elbows", "Create conduit route with elbows", "CREATE", "conduit_route", "segments connected by elbows, with free ends", new[] { "Autodesk.Revit.DB.Electrical.Conduit::Create", "Autodesk.Revit.Creation.Document::NewElbowFitting" }, Array.Empty<string>(), limits: "creates a new route; pending native verification"),
                Dyn("F20-TRAY-001", "F20", "raceways", "Create cable tray", "Create cable tray", "CREATE", "route with type and level", new[] { "Autodesk.Revit.DB.Electrical.CableTray::Create" }, new[] { "CableTray" }),
                Dyn("F20-PANELSCHED-001", "F20", "panels", "Create panel schedule", "Panel schedule", "SCHEDULE", "panel schedule with circuits", new[] { "Autodesk.Revit.DB.Electrical.PanelScheduleView" }, new[] { "PanelSchedules" }),
                new CapabilityEntry { Id = "F20-LIGHT-QA-001", Domain = "F20", Subdomain = "lighting", Name = "Lighting fixture and tag QA", NameEn = "Lighting fixture QA", Operation = "VERIFY", Expected = "untagged fixtures, orphaned or duplicate tags, with ids", Strategy = "query:check", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/Bimbridge.Engine/LightingAudit.cs" }, Tests = new[] { "LightingAuditTests" }, Verifier = "tagging rules per task", Priority = "P1" },
                Missing("F21-LOWVOLT-001", "F21", "low voltage", "Data, alarm and security devices and circuits", "Low-voltage devices", "CREATE", "devices connected to their systems", null, new[] { "Data", "FireAlarm" }),
                Dyn("F22-FAB-001", "F22", "fabrication", "Place or read MEP fabrication parts", "Fabrication parts", "READ", "fabrication part with service and configuration", new[] { "Autodesk.Revit.DB.FabricationPart", "Autodesk.Revit.DB.FabricationConfiguration" }, null, "depends on the loaded fabrication configuration", "P3"),

                // ---------------- F23 Family Editor ----------------
                new CapabilityEntry { Id = "F23-FAM-INFO-001", Domain = "F23", Subdomain = "families", Name = "Family and face information", NameEn = "Family info", Operation = "READ", Expected = "types, parameters and reference faces", Strategy = "fam:info", DocumentKind = "family", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { FamilyOps }, Tests = new[] { "FamilyLibraryTests" }, Verifier = "read in the family document", Priority = "P1" },
                new CapabilityEntry { Id = "F23-FAM-PARAM-001", Domain = "F23", Subdomain = "families", Name = "Create or edit family parameter", NameEn = "Family parameter", Operation = "UPDATE", Expected = "parameter with data type, group and value per type", Strategy = "fam:param", DocumentKind = "family", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { FamilyOps }, Tests = new[] { "FamilyLibraryTests" }, Verifier = "FamilyManager reread", Priority = "P1" },
                new CapabilityEntry { Id = "F23-FAM-LOAD-001", Domain = "F23", Subdomain = "families", Name = "Load family into project from an authorized folder", NameEn = "Load family", Operation = "CREATE", Expected = "family and types available in the project", Strategy = "fam:cargar", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { FamilyOps }, Tests = new[] { "FamilyLibraryTests" }, Verifier = "types present after loading", PostableCommands = new[] { "LoadShapes" }, Priority = "P1" },

                // ---------------- F24 Views ----------------
                Typed("F24-VIEW-CREATE-001", "F24", "views", "Create view (floor plan, RCP, section, callout)", "Create view", "CREATE", "view_create", "view of the requested type with level, scale and template", plan, new[] { "FloorPlan", "ReflectedCeilingPlan", "Section", "Callout" }, prio: "P0"),
                Dyn("F24-VIEW-ELEV-001", "F24", "views", "Create elevation (marker and view)", "Create elevation", "CREATE", "elevation marker with its view", null, new[] { "BuildingElevation", "FramingElevation" }, "view_create does not support elevations: dynamic C# only", "P2"),
                Typed("F24-VIEW-DUP-001", "F24", "views", "Duplicate view (plain, with detailing or dependent)", "Duplicate view", "DUPLICATE", "view_create", "copy with or without detailing, or dependent (view_create tipo=duplicar)", plan, prio: "P1"),
                Typed("F24-VIEW-SET-001", "F24", "views", "Configure view (range, crop, scale, template)", "Set view properties", "UPDATE", "view_set", "view properties reread equal", plan, new[] { "ManageViewTemplates", "ApplyTemplatePropertiesToCurrentView" }, prio: "P1"),
                Typed("F24-VIEW-HIDE-001", "F24", "visibility", "Hide or show elements and categories in view", "Hide in view", "UPDATE", "view_hide", "requested visibility in the view", plan, new[] { "HideElements", "HideCategory" }, prio: "P1"),
                Typed("F24-VIEW-OVERRIDE-001", "F24", "visibility", "Graphic overrides and view filters", "Graphic overrides / filters", "UPDATE", "view_override", "override applied (color, pattern, line) and filter assigned", plan, new[] { "Filters", "VisibilityOrGraphics" }, prio: "P1"),
                Typed("F24-VIEW-3D-001", "F24", "views", "Create isometric 3D view with section box", "Create 3D view", "CREATE", "view_create", "3D view with optional section box (view_create tipo=3d)", plan, new[] { "Default3DView" }),
                Typed("F24-VIEW-DRAFT-001", "F24", "views", "Create drafting view", "Drafting view", "CREATE", "view_create", "drafting view with scale (view_create tipo=dibujo)", plan, new[] { "DraftingView" }),
                new CapabilityEntry { Id = "F24-VIEW-CAPTURE-001", Domain = "F24", Subdomain = "views", Name = "Capture view image with temporary highlight", NameEn = "Capture view image", Operation = "EXPORT", Expected = "view image with highlighted elements without changing the model", Strategy = "query:capture", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/Bimbridge.Addin/Revit/Capture.cs" }, Tests = new[] { "EndToEndTests" }, Verifier = "image file; temporary transaction rolled back", Priority = "P2" },

                // ---------------- F25 Annotations ----------------
                Typed("F25-TAG-001", "F25", "tags", "Tag elements", "Tag elements", "TAG", "tag", "one tag per element with type, orientation and leader", plan, new[] { "TagByCategory" }, prio: "P0"),
                Typed("F25-TAG-MOVE-001", "F25", "tags", "Move tags", "Move tags", "MOVE", "tag_move", "tag head at the requested position", plan, prio: "P1"),
                Typed("F25-TEXT-001", "F25", "text", "Create text note", "Text note", "CREATE", "text_note", "text with type and position", draw, new[] { "Text" }, prio: "P1"),
                Typed("F25-DIM-001", "F25", "dimensions", "Create dimension between references", "Dimension", "CREATE", "dimension", "linear dimension between the requested references", draw, new[] { "AlignedDimension" }, prio: "P1"),
                Typed("F25-DETAIL-001", "F25", "2D detailing", "Detail lines, arcs, polygons and rectangles", "Detail lines", "CREATE", "detail_line", "2D geometry in view with line style", draw, new[] { "DetailLine" }, prio: "P1"),
                Typed("F25-DETAILCOMP-001", "F25", "2D detailing", "Detail component", "Detail component", "CREATE", "detail_component", "detail component placed", draw, new[] { "DetailComponent" }),
                Typed("F25-REGION-001", "F25", "2D detailing", "Filled region", "Filled region", "CREATE", "filled_region", "region with type and boundary", draw, new[] { "FilledRegion" }),
                Dyn("F25-REVCLOUD-001", "F25", "revisions", "Revision cloud", "Revision cloud", "CREATE", "cloud associated with a revision", new[] { "Autodesk.Revit.DB.RevisionCloud::Create" }, new[] { "RevisionCloud" }),

                // ---------------- F26 Schedules ----------------
                Model("F26-SCHED-CREATE-001", "F26", "schedules", "Create schedule with fields", "Create schedule", "SCHEDULE", "schedule_create", "category schedule with the requested fields in order", new[] { "Autodesk.Revit.DB.ViewSchedule::CreateSchedule" }, new[] { "ScheduleOrQuantities" }, limits: "fields in order; filters, sorting and grouping still go through dynamic C#. Effect in Revit pending the native campaign"),
                new CapabilityEntry { Id = "F26-SCHED-EXPORT-001", Domain = "F26", Subdomain = "schedules", Name = "Export schedule to CSV", NameEn = "Export schedule", Operation = "EXPORT", Expected = "CSV verified against the schedule rows",
                    Strategy = "doc:tabla_csv", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/Bimbridge.Addin/Revit/Families/ExportOps.cs" }, PostableCommands = new[] { "ExportReportsSchedule" },
                    Verifier = "new file, size, hash and lines against body rows", Idempotency = "explicit sobrescribir=si", Limitations = "effect in Revit pending the native campaign", Priority = "P1" },

                // ---------------- F27 Sheets ----------------
                Typed("F27-SHEET-CREATE-001", "F27", "sheets", "Create sheet with titleblock", "Create sheet", "CREATE", "sheet_create", "sheet with number, name and titleblock", new[] { "SheetSetOpTests", "PlanTests" }, new[] { "NewSheet" }, prio: "P0"),
                Typed("F27-SHEET-PLACE-001", "F27", "sheets", "Place view on sheet", "Place view on sheet", "CREATE", "sheet_place", "viewport at the requested position", new[] { "SheetSetOpTests" }, prio: "P0"),
                Typed("F27-SHEET-SET-001", "F27", "sheets", "Edit sheet parameters", "Sheet parameters", "UPDATE", "sheet_set", "sheet parameters reread", new[] { "SheetSetOpTests" }, prio: "P1"),
                Dyn("F27-SCHED-ONSHEET-001", "F27", "sheets", "Place schedule on sheet", "Place schedule on sheet", "CREATE", "schedule instance on sheet", new[] { "Autodesk.Revit.DB.ScheduleSheetInstance::Create" }, null),
                Dyn("F27-REVISION-001", "F27", "revisions", "Create and assign revisions to sheets", "Revisions", "UPDATE", "revision assigned to sheets", new[] { "Autodesk.Revit.DB.Revision" }, new[] { "SheetIssuesOrRevisions" }),

                // ---------------- F28–F31 Coordination and collaboration ----------------
                Dyn("F28-LINK-RVT-001", "F28", "links", "Link RVT model with positioning", "Link Revit model", "CREATE", "link with positioning (origin, shared)", new[] { "Autodesk.Revit.DB.RevitLinkType::Create", "Autodesk.Revit.DB.RevitLinkInstance::Create" }, new[] { "LinkRevit" }, prio: "P1"),
                Dyn("F28-LINK-CAD-001", "F28", "links", "Link or import CAD", "Link CAD", "CREATE", "CAD link with units and layers", new[] { "Autodesk.Revit.DB.CADLinkType" }, new[] { "LinkCAD", "ImportCAD" }),
                NoApi("F29-CM-BATCH-001", "F29", "copy/monitor", "Copy/Monitor: batch copy with monitoring", "Copy/Monitor batch copy", "MONITOR",
                    "elements copied from the link with a native monitoring relationship and type mapping",
                    new[] { "Autodesk.Revit.DB.CopyMonitorOptions" }, new[] { "CopyMonitorSelectLink", "CopyMonitorUseCurrentProject" },
                    "the inventory confirms that CopyMonitorOptions does not exist in the 2022–2026 public API. PostCommand only opens the tool: it neither completes nor verifies the workflow (#23, #24). 0.9.1: preflight, truthful manual handoff and later verification in F29-CM-VERIFY-001", "P0"),
                new CapabilityEntry { Id = "F29-CM-VERIFY-001", Domain = "F29", Subdomain = "copy/monitor", Name = "Copy/Monitor: preflight, manual handoff and verification of monitored fixtures", NameEn = "Copy/Monitor preflight and verification", Operation = "VERIFY",
                    Expected = "link and host inventory; after the copy, each fixture with expected type, position, orientation and monitoring relationship; duplicates and missing ones detected",
                    Strategy = "query:check copy_monitor", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED,
                    Implementation = new[] { "src/Bimbridge.Addin/Revit/CopyMonitorProbe.cs", "src/Bimbridge.Addin/Revit/RequestHandler.CopyMonitor.cs", "src/Bimbridge.Engine/Workflows/CopyMonitorVerifier.cs", "src/Bimbridge.Engine/Workflows/NativeCapabilities.cs" },
                    Tests = new[] { "NativeWorkflowTests" }, PostableCommands = new[] { "CopyMonitorSelectLink" },
                    Verifier = "CopyMonitorVerifier against expectations FROZEN at preflight (host, link and revision, source, transform, mapping, exclusions, category); a change of target is a conflict. Correspondence inferred by type and position; monitoring via IsMonitoringLinkElement",
                    Limitations = "read-only: the user does the copy with the returned steps (there is no UI executor). Reading in Revit pending the native campaign", Priority = "P0" },
                NoApi("F29-CR-001", "F29", "coordination review", "Coordination Review: review and resolve monitoring warnings", "Coordination Review", "VERIFY",
                    "monitoring warnings reviewed with the action recorded", new string[0], new[] { "CoordinationReviewUseCurrentProject", "CoordinationSelectLink" },
                    "no public API for Coordination Review; supervised UI workflow only", "P1"),
                Typed("F30-WS-SET-001", "F30", "worksharing", "Move elements to another workset", "Set workset", "UPDATE", "set_workset", "elements in the requested workset", new[] { "WorksharedPolicyTests", "PlanTests" }, prio: "P1"),
                Dyn("F30-WS-CREATE-001", "F30", "worksharing", "Create workset", "Create workset", "CREATE", "named workset", new[] { "Autodesk.Revit.DB.Workset::Create" }, new[] { "Worksets" }),
                Typed("F31-GROUP-001", "F31", "organization", "Create, place and ungroup groups", "Groups", "GROUP", "group_create", "group created or placed; ungrouped", new[] { "AdvancedEnginesTests", "PlanTests" }, new[] { "CreateGroup", "LoadAsGroup" }),
                Dyn("F31-PHASE-001", "F31", "phases", "Assign phase created and demolished", "Phasing", "UPDATE", "phases assigned per element", new[] { "Autodesk.Revit.DB.Phase" }, new[] { "Phases" }),
                Dyn("F31-OPTION-001", "F31", "design options", "Design options", "Design options", "UPDATE", "elements in the requested option", new[] { "Autodesk.Revit.DB.DesignOption" }, new[] { "DesignOptions" }, prio: "P3"),
                Dyn("F31-ASSEMBLY-001", "F31", "organization", "Create assembly", "Create assembly", "CREATE", "assembly with optional views", new[] { "Autodesk.Revit.DB.AssemblyInstance::Create" }, new[] { "CreateAssembly" }, prio: "P3"),

                // ---------------- F32–F35 Analysis, visualization, export, automation ----------------
                new CapabilityEntry { Id = "F32-GEO-CLASH-001", Domain = "F32", Subdomain = "analysis", Name = "Clash detection and real mounting", NameEn = "Clash / mounting analysis", Operation = "VERIFY", Expected = "clashes and mounting classified with geometric evidence", Strategy = "query:geo", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/Bimbridge.Addin/Revit/Geo/GeoRunner.cs", "src/Bimbridge.Engine/Geo/GeoSummary.cs" }, Tests = new[] { "GeoComputeTests", "MeshTests" }, Verifier = "offline geometric computation verified (GeoComputeTests); geometry capture in Revit pending the campaign", Priority = "P1" },
                Missing("F32-ENERGY-001", "F32", "energy analysis", "Energy analysis model", "Energy analysis model", "CREATE", "energy model generated", new[] { "Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel" }, new[] { "EnergySettings" }, prio: "P3"),
                Dyn("F33-RENDER-IMG-001", "F33", "visualization", "Export view image", "Export image", "EXPORT", "view image at the requested resolution", new[] { "Autodesk.Revit.DB.ImageExportOptions" }, null, prio: "P3"),
                new CapabilityEntry { Id = "F34-EXPORT-IFC-001", Domain = "F34", Subdomain = "export", Name = "Export IFC with options", NameEn = "Export IFC", Operation = "EXPORT", Expected = "IFC file with the requested version and view, without modifying the model",
                    Strategy = "doc:ifc", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/Bimbridge.Addin/Revit/Families/ExportOps.cs" },
                    ApiSurfaces = new[] { "Autodesk.Revit.DB.IFCExportOptions", "Autodesk.Revit.DB.Document::Export" }, PostableCommands = new[] { "ExportIFC" },
                    Verifier = "new file, size and hash; transaction rolled back", Idempotency = "explicit sobrescribir=si",
                    Limitations = "no property set mapping or IFC configuration file (still through dynamic C#). Effect in Revit pending the native campaign", Priority = "P1" },
                Dyn("F34-EXPORT-DWG-001", "F34", "export", "Export DWG", "Export DWG", "EXPORT", "DWG files per view or sheet", new[] { "Autodesk.Revit.DB.DWGExportOptions" }, new[] { "ExportCADFormatsDWG", "ExportCADFormatsDXF" }),
                Dyn("F34-EXPORT-NWC-001", "F34", "export", "Export NWC", "Export Navisworks", "EXPORT", "NWC file", new[] { "Autodesk.Revit.DB.NavisworksExportOptions" }, null, "requires the Navisworks exporter to be installed"),
                new CapabilityEntry { Id = "F35-EXEC-001", Domain = "F35", Subdomain = "automation", Name = "Run dynamic C# (query, trial, run)", NameEn = "Dynamic C#", Operation = "UPDATE", Expected = "script compiled and run with transaction, audit and optional lock", Strategy = "exec", DocumentKind = "any", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { ExecHandler, "src/Bimbridge.Addin/Scripting/ScriptHost.cs", "src/Bimbridge.Engine/CompileCache.cs" }, Tests = new[] { "ScriptingTests", "CompileCacheTests", "Rc3Tests" }, Verifier = "evidence returned by the script; audit of intent and result", Limitations = "not a sandbox; external effects without rollback", Priority = "P0" },
                new CapabilityEntry { Id = "F35-RECIPE-001", Domain = "F35", Subdomain = "automation", Name = "Recipe tasks with capability grants", NameEn = "Recipe tasks", Operation = "UPDATE", Expected = "task authorized by a person, batched, resumable and reconcilable", Strategy = "engine", DocumentKind = "any", Status = CoverageStatus.OFFLINE_VERIFIED, Implementation = new[] { "src/Bimbridge.Engine/Tasks/TaskManager.cs" }, Tests = new[] { "TaskEngineTests", "ReleaseGateTests" }, Verifier = "task status, lease with fencing and audit", Priority = "P1" },
                new CapabilityEntry { Id = "F35-AGENTHOST-001", Domain = "F35", Subdomain = "automation", Name = "Durable IR plans with cross-process recovery", NameEn = "Durable IR plans", Operation = "UPDATE", Expected = "plan run exactly once with recovery after a crash", Strategy = "engine", DocumentKind = "any", Status = CoverageStatus.OFFLINE_VERIFIED, Implementation = new[] { "src/Bimbridge.AgentHost/Exec/PlanRunner.cs" }, Tests = new[] { "AgentHostProcessTests", "AgentHostScenarioTests", "AgentHostAlphaTests" }, Verifier = "durable simulated model + journal", Limitations = "verified against the simulated backend; against Revit, pending the campaign", Priority = "P0" },

                // ---------------- F36–F37 QA/QC and interactive workflows ----------------
                new CapabilityEntry { Id = "F36-QC-MANIFEST-001", Domain = "F36", Subdomain = "QA/QC", Name = "One-to-one QC against manifest", NameEn = "QC against manifest", Operation = "VERIFY", Expected = "deviations in position, elevation, rotation, type, level and tags", Strategy = "exec", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/Bimbridge.Addin/recetas/qc_manifiesto.cs" }, Tests = new[] { "QcFixtureTests" }, Verifier = "postcondition with a list of deviations", Priority = "P1" },
                new CapabilityEntry { Id = "F36-QC-INDEP-001", Domain = "F36", Subdomain = "QA/QC", Name = "Independent verification against a declared source", NameEn = "Independent verification", Operation = "VERIFY", Expected = "expected items from an independent source versus the model", Strategy = "engine", Status = CoverageStatus.OFFLINE_VERIFIED, Implementation = new[] { "src/Bimbridge.Engine/Workflow/Traceability.cs" }, Tests = new[] { "TraceabilityTests", "Hardening090Tests" }, Verifier = "refused if the expected source is the same one that guided the build", Priority = "P1" },
                Engine("F37-NWE-001", "F37", "native workflows", "Native workflow engine: evidence-driven states, receipts and idempotency", "Native Workflow Engine", "VERIFY",
                    "workflow whose states only advance on observed evidence, persistent receipts per attempt key, reconciliation on uncertainty and no blind re-PostCommand",
                    new[] { "src/Bimbridge.Engine/Workflows/WorkflowReceipt.cs", "src/Bimbridge.Engine/Workflows/NativeCapabilities.cs" }, new[] { "NativeWorkflowTests" },
                    "state machine + receipt store with synthetic fault injection",
                    "no UI executor in the product: interactive workflows end in a manual handoff; observing the native mode in Revit is left to the native campaign"),
                Engine("F37-TARGET-001", "F37", "target", "Stable target per instance and document (multi-Revit)", "Stable instance/document targeting", "VERIFY",
                    "operation bound to (pid, process start) and to the document identity; ambiguity, a dN from another session and a reused pid are refused",
                    new[] { "src/Bimbridge.Engine/Workflows/TargetBinding.cs" }, new[] { "NativeWorkflowTests.TwoRevitInstances_RequireAnExplicitTarget_AndStaleHandlesCannotWrite" },
                    "target resolution and revalidation with synthetic instances", "the Bridge's real instance list is not wired in yet (#25 diagnosis)"),
                Engine("F37-DIAG-001", "F37", "diagnostics", "Connection diagnosis: Bridge, client registration, tools, Revit", "Connection diagnostics", "VERIFY",
                    "precise verdict that separates client registration/permissions from Revit availability",
                    new[] { "src/Bimbridge.Engine/Workflows/TargetBinding.cs" }, new[] { "NativeWorkflowTests.Diagnostics_SeparateClientRegistrationFromRevitAvailability" },
                    "classification with synthetic observations", "per-client registration observations are measured outside the engine (installer / Bridge)"),
                Engine("F37-PDF-PLAN-001", "F37", "PDF→Revit", "Placement plan from PDF (document → plan)", "PDF to placement plan", "CREATE",
                    "placement plan with traceability and explicit uncertainty", new[] { "src/Bimbridge.Engine/PowerEngine/Workflows/PdfToPlan.cs" }, new[] { "PdfToModelWorkflowTests", "AgentHostDocumentTests" },
                    "synthetic scenarios with known expectations", "validates the document and the plan and turns it into typed plans with a source witness; the commit applies the content-derived key (idempotent without the agent passing it along, on top of the operation journal). Applying in Revit is pending the campaign"),

                // ---------------- Engine guarantees (cross-cutting) ----------------
                Engine("F35-AUDIT-001", "F35", "guarantees", "Durable audit with group commit and recovery", "Durable audit journal", "VERIFY",
                    "intact chain after crashes at any point, with no duplicates or false commits", new[] { "src/Bimbridge.Engine/AuditLog.cs" },
                    new[] { "AuditRecoveryTests", "AuditGroupCommitTests", "AuditLogTests", "ReleaseGateTests" }, "chain, head and batch verification"),
                Engine("F35-QUERY-SNAP-001", "F35", "guarantees", "Paginated read from a revision-bound snapshot", "Revision-bound query snapshots", "QUERY",
                    "pages identical to the full read, without mixing revisions, within a memory budget", new[] { "src/Bimbridge.Engine/QuerySnapshots.cs" },
                    new[] { "QuerySnapshotTests" }, "row equality and revalidation after LiveRev"),
                Engine("F35-IDENTITY-001", "F35", "guarantees", "Named identity and request correlation", "Agent identity and correlation", "VERIFY",
                    "every operation attributed to a named agent with unique correlation ids", new[] { "src/Bimbridge.Engine/Correlation.cs", "src/Bimbridge.Engine/Sessions.cs" },
                    new[] { "AgentIdentityGateTests" }, "journal with agent_id, session_id, request_id, trace_id"),
            };
            Expand(l);
            foreach (var entry in l.Where(e => e.Status == CoverageStatus.IMPLEMENTED_UNVERIFIED || e.Status == CoverageStatus.OFFLINE_VERIFIED))
                entry.MaxVersion = 2027;
            return l;
        }

        /// <summary>Summary by status and by domain (for the report and the panel).</summary>
        public static Dictionary<string, object> Summary()
        {
            var byStatus = All.GroupBy(e => e.Status.ToString()).ToDictionary(g => g.Key, g => (object)(long)g.Count());
            var byDomain = All.GroupBy(e => e.Domain).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (object)g.GroupBy(e => e.Status.ToString()).ToDictionary(x => x.Key, x => (object)(long)x.Count()));
            return new Dictionary<string, object> { ["capacidades"] = (long)All.Count, ["por_estado"] = byStatus, ["por_dominio"] = byDomain };
        }
    }
}
