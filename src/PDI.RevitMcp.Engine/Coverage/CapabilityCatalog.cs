// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Engine.Coverage
{
    /// <summary>Estados oficiales de cobertura (directiva 0.9.1, §4.3). Nunca se infiere SUPPORTED desde UNKNOWN.</summary>
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

    /// <summary>Contrato semántico de una capacidad funcional de Revit frente a BIMBRIDGE (§2.1, §3.2, §4.3).</summary>
    public sealed class CapabilityEntry
    {
        public string Id;
        public string Domain;          // F01..F37
        public string Subdomain;
        public string Name;            // español
        public string NameEn;
        public string Operation;       // CREATE, READ, QUERY, UPDATE, DELETE, COPY, MOVE, ROTATE, MIRROR, TAG, SCHEDULE, EXPORT, SAVE, CONNECT, VERIFY…
        public string Expected;        // resultado funcional esperado
        public string[] Categories = new string[0];
        public string DocumentKind = "project";   // project | family | any
        public string Strategy;        // typed:<op> | query:<op> | doc:<verb> | fam:<verb> | exec | postcommand | manual | none
        public string[] ApiSurfaces = new string[0];   // claves del inventario (docs/0.9.1/inventory/revit-api-*.json)
        public int MinVersion = 2022;
        public int MaxVersion = 2026;
        public CoverageStatus Status;
        public string[] Implementation = new string[0];   // archivos de código (rutas del repositorio)
        public string[] Tests = new string[0];            // clases de prueba (o Clase.Metodo) que la cubren
        public string Verifier;        // cómo se comprueba el resultado real
        public string Idempotency;
        public string Recovery;
        public string Limitations;
        public string[] PostableCommands = new string[0]; // comandos de la cinta relacionados (enum PostableCommand)
        public string Priority = "P2";
    }

    /// <summary>
    /// Catálogo de capacidades F01–F37 (0.9.1, Bloque B). Es código del producto, no sólo documentación: el motor puede informar con verdad
    /// qué puede hacer cada capacidad (#23). Reglas que comprueban las pruebas (CapabilityCatalogTests):
    /// - Ninguna capacidad es NATIVE_VERIFIED sin una campaña nativa del commit (todavía no la hay).
    /// - IMPLEMENTED_* / OFFLINE_VERIFIED / PARTIAL citan archivos de implementación que existen; OFFLINE_VERIFIED cita pruebas que existen.
    /// - DYNAMIC_CSHARP_ONLY y UNSUPPORTED_BY_PUBLIC_API citan la superficie de API (o su ausencia) del inventario por versión.
    /// - Todo PostableCommand de cada versión 2022–2026 queda clasificado en un dominio (ver <see cref="CommandClassifier"/>).
    /// Las estrategias tipadas existentes se marcan IMPLEMENTED_UNVERIFIED: tienen pruebas de planificación offline, pero su efecto en Revit
    /// sólo lo demuestra la campaña nativa única de 0.9.1.
    /// </summary>
    public static partial class CapabilityCatalog
    {
        private const string PlanBuilder = "src/PDI.RevitMcp.Addin/Revit/Writes/PlanBuilder.cs";
        private const string PlanApplier = "src/PDI.RevitMcp.Addin/Revit/Writes/PlanApplier.cs";
        private const string OpCatalogFile = "src/PDI.RevitMcp.Engine/OpCatalog.cs";
        private const string QueryRunner = "src/PDI.RevitMcp.Addin/Revit/QueryRunner.cs";
        private const string ExecHandler = "src/PDI.RevitMcp.Addin/Revit/RequestHandler.Exec.cs";
        private const string FamilyOps = "src/PDI.RevitMcp.Addin/Revit/Families/FamilyOps.cs";
        private const string DocOps = "src/PDI.RevitMcp.Addin/Revit/Families/DocOps.cs";
        private const string Draw2D = "src/PDI.RevitMcp.Engine/Draw2D.cs";

        private static CapabilityEntry Typed(string id, string dom, string sub, string es, string en, string op, string strategyOp, string expected, string[] tests, string[] cmds = null, string[] cats = null, string limits = null, string prio = "P1") => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = op, Expected = expected, Strategy = "typed:" + strategyOp,
            Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { PlanBuilder, PlanApplier, OpCatalogFile }, Tests = tests ?? new string[0],
            Verifier = "postcondiciones del plan + relectura del modelo tras commit (diff por elemento)", Idempotency = "clave= por commit; plan_id único",
            Recovery = "TransactionGroup + intención durable; reconciliación por plan_id", PostableCommands = cmds ?? new string[0], Categories = cats ?? new string[0],
            Limitations = limits ?? "efecto en Revit pendiente de la campaña nativa única de 0.9.1", Priority = prio,
        };

        /// <summary>0.9.1 (Bloques C/D/E): operación tipada de modelado: validación offline en Engine.ModelOps y verificación de lo creado.</summary>
        private static CapabilityEntry Model(string id, string dom, string sub, string es, string en, string op, string strategyOp, string expected, string[] api, string[] cmds = null, string[] cats = null, string limits = null, string prio = "P1")
        {
            var e = Typed(id, dom, sub, es, en, op, strategyOp, expected, new[] { "ModelOpsTests" }, cmds, cats, limits, prio);
            e.Implementation = e.Implementation.Concat(new[] { "src/PDI.RevitMcp.Engine/ModelOps.cs", "src/PDI.RevitMcp.Addin/Revit/Writes/PlanBuilder.Model.cs", "src/PDI.RevitMcp.Addin/Revit/Writes/PlanApplier.Model.cs" }).ToArray();
            e.ApiSurfaces = api ?? new string[0];
            e.Verifier = "relectura de cada elemento creado frente a lo pedido (verificacion_creacion)";
            return e;
        }

        private static CapabilityEntry Dyn(string id, string dom, string sub, string es, string en, string op, string expected, string[] api, string[] cmds = null, string limits = null, string prio = "P2") => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = op, Expected = expected, Strategy = "exec",
            Status = CoverageStatus.DYNAMIC_CSHARP_ONLY, Implementation = new[] { ExecHandler }, ApiSurfaces = api ?? new string[0], PostableCommands = cmds ?? new string[0],
            Verifier = "ninguno tipado: el script debe devolver evidencia; pendiente de operación tipada con verificador",
            Idempotency = "no garantizada (código arbitrario)", Recovery = "intención durable + auditoría; sin rollback de efectos externos",
            Limitations = limits ?? "accesible sólo con C# dinámico: falta operación tipada, verificador y pruebas", Priority = prio,
        };

        private static CapabilityEntry Missing(string id, string dom, string sub, string es, string en, string op, string expected, string[] api = null, string[] cmds = null, string limits = null, string prio = "P2") => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = op, Expected = expected, Strategy = "none",
            Status = CoverageStatus.MISSING, ApiSurfaces = api ?? new string[0], PostableCommands = cmds ?? new string[0],
            Limitations = limits ?? "sin implementación en BIMBRIDGE", Priority = prio,
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
                // ---------------- F01 Documentos y proyectos ----------------
                new CapabilityEntry { Id = "F01-DOC-READ-001", Domain = "F01", Subdomain = "documentos", Name = "Listar documentos abiertos con identidad estable", NameEn = "List open documents", Operation = "READ", Expected = "handles dN, clave duradera, tipo RVT/RFA, modificado", Strategy = "query:ctx", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Addin/Revit/DocRegistry.cs" }, Tests = new[] { "IdentityJournalTests" }, Verifier = "huella de documento", Priority = "P0" },
                new CapabilityEntry { Id = "F01-DOC-SAVE-001", Domain = "F01", Subdomain = "documentos", Name = "Guardar y guardar como", NameEn = "Save / Save As", Operation = "SAVE", Expected = "archivo en disco verificado por fecha y hash", Strategy = "doc:guardar", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { DocOps }, Tests = new[] { "AgentHostRevitTests" }, Verifier = "verificar_disco: fecha, tamaño y hash del archivo", PostableCommands = new[] { "Save", "SaveAsProject" }, Priority = "P0" },
                new CapabilityEntry { Id = "F01-DOC-SYNC-001", Domain = "F01", Subdomain = "documentos", Name = "Sincronizar con el central", NameEn = "Synchronize with Central", Operation = "SAVE", Expected = "sincronización confirmada por evento DocumentSynchronizedWithCentral", Strategy = "doc:sync", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { DocOps }, Tests = new[] { "WorksharedPolicyTests" }, Verifier = "evento de sincronización + fecha", PostableCommands = new[] { "SynchronizeAndModifySettings", "SynchronizeNow" }, Priority = "P1" },
                new CapabilityEntry { Id = "F01-DOC-CLOSE-001", Domain = "F01", Subdomain = "documentos", Name = "Cerrar documento", NameEn = "Close document", Operation = "UPDATE", Expected = "documento cerrado sin perder cambios no guardados", Strategy = "doc:cerrar", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { DocOps }, Verifier = "el handle deja de existir", Priority = "P2" },
                Dyn("F01-DOC-OPEN-001", "F01", "documentos", "Abrir proyecto o familia desde ruta", "Open document", "READ", "documento abierto y registrado con handle", new[] { "Autodesk.Revit.DB.Document::EditFamily" }, new[] { "OpenRevitFile", "OpenProject", "OpenFamily" }, "abrir modelos exige autorización explícita del modelo; no se automatiza sobre proyectos del usuario"),
                Missing("F01-DOC-PURGE-001", "F01", "documentos", "Purgar elementos no usados", "Purge unused", "DELETE", "elementos no usados eliminados con lista de evidencia", null, new[] { "PurgeUnused" }),
                new CapabilityEntry { Id = "F01-DOC-PDF-001", Domain = "F01", Subdomain = "exportación", Name = "Exportar láminas a PDF", NameEn = "Export to PDF", Operation = "EXPORT", Expected = "archivos PDF por lámina verificados en disco", Strategy = "doc:pdf", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { DocOps }, ApiSurfaces = new[] { "Autodesk.Revit.DB.PDFExportOptions" }, Verifier = "archivos esperados presentes y no vacíos", PostableCommands = new[] { "ExportPDF" }, Priority = "P1" },

                // ---------------- F02 Selección y transformaciones ----------------
                Typed("F02-XFORM-MOVE-001", "F02", "transformaciones", "Mover elementos", "Move", "MOVE", "move", "elementos desplazados exactamente el vector pedido", plan, new[] { "Move" }),
                Typed("F02-XFORM-ROTATE-001", "F02", "transformaciones", "Rotar elementos", "Rotate", "ROTATE", "rotate", "elementos girados alrededor del eje pedido", plan, new[] { "Rotate" }),
                Typed("F02-XFORM-MIRROR-001", "F02", "transformaciones", "Reflejar elementos", "Mirror", "MIRROR", "mirror", "copia reflejada respecto al plano pedido", plan, new[] { "MirrorPickAxis", "MirrorDrawAxis" }),
                Typed("F02-XFORM-COPY-001", "F02", "transformaciones", "Copiar elementos", "Copy", "COPY", "copy", "copias con el desplazamiento pedido y sus ids nuevos", plan, new[] { "Copy" }),
                Typed("F02-XFORM-DELETE-001", "F02", "transformaciones", "Eliminar elementos", "Delete", "DELETE", "delete", "elementos eliminados (y dependientes declarados)", plan, new[] { "Delete" }),
                Dyn("F02-XFORM-ARRAY-001", "F02", "transformaciones", "Matriz lineal o radial", "Array", "ARRAY", "copias en matriz con o sin agrupación", new[] { "Autodesk.Revit.DB.ElementTransformUtils::CopyElements" }, new[] { "Array" }),
                Dyn("F02-XFORM-ALIGN-001", "F02", "transformaciones", "Alinear", "Align", "ALIGN", "elementos alineados (con bloqueo opcional)", null, new[] { "Align" }),
                Dyn("F02-XFORM-OFFSET-001", "F02", "transformaciones", "Desfase", "Offset", "OFFSET", "copia desplazada perpendicularmente", null, new[] { "Offset" }),
                new CapabilityEntry { Id = "F02-SEL-SHOW-001", Domain = "F02", Subdomain = "selección", Name = "Seleccionar y encuadrar elementos para el usuario", NameEn = "Select and zoom", Operation = "READ", Expected = "selección activa y vista encuadrada", Strategy = "query:show", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Addin/Revit/RequestHandler.cs" }, Tests = new[] { "EndToEndTests" }, Verifier = "selección leída tras la operación", Priority = "P2" },
                new CapabilityEntry { Id = "F02-SEL-SETS-001", Domain = "F02", Subdomain = "selección", Name = "Conjuntos de ids con nombre ligados a documento", NameEn = "Named selection sets", Operation = "QUERY", Expected = "conjunto reutilizable que nunca resuelve elementos de otro documento", Strategy = "engine", DocumentKind = "any", Status = CoverageStatus.OFFLINE_VERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Engine/Sessions.cs" }, Tests = new[] { "DurabilityTests" }, Verifier = "ligadura documento-conjunto", Priority = "P1" },

                // ---------------- F03 Datums y coordenadas ----------------
                Model("F03-DATUM-LEVEL-001", "F03", "niveles", "Crear nivel", "Create level", "CREATE", "level_create", "nivel a la elevación pedida con sus vistas de planta y RCP opcionales", new[] { "Autodesk.Revit.DB.Level::Create" }, new[] { "Level" }),
                Model("F03-DATUM-GRID-001", "F03", "rejillas", "Crear rejilla recta", "Create grid", "CREATE", "grid_create", "rejilla recta con su nombre", new[] { "Autodesk.Revit.DB.Grid::Create" }, new[] { "Grid" }, limits: "sólo rejillas rectas; arcos y rejillas multisegmento siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa"),
                Dyn("F03-DATUM-REFPLANE-001", "F03", "planos de referencia", "Crear plano de referencia", "Create reference plane", "CREATE", "plano de referencia con nombre", new[] { "Autodesk.Revit.DB.ReferencePlane" }, new[] { "ReferencePlane" }),
                Typed("F03-DATUM-SCOPEBOX-001", "F03", "cajas de referencia", "Asignar caja de referencia a vistas", "Assign scope box to views", "ASSOCIATE", "view_set", "vistas recortadas por la caja de referencia pedida (view_set scope_box)", plan),
                new CapabilityEntry { Id = "F03-DATUM-SCOPEBOX-002", Domain = "F03", Subdomain = "cajas de referencia", Name = "Crear caja de referencia", NameEn = "Create scope box", Operation = "CREATE", Expected = "caja de referencia con extensión y nombre", Strategy = "none", Status = CoverageStatus.UNKNOWN, PostableCommands = new[] { "ScopeBox" }, Limitations = "no se ha identificado una API pública de creación de cajas de referencia; investigar antes de clasificar", Priority = "P3" },
                Dyn("F03-COORD-READ-001", "F03", "coordenadas", "Leer coordenadas compartidas y puntos base", "Read shared coordinates", "READ", "punto base, punto de reconocimiento y transformación compartida", new[] { "Autodesk.Revit.DB.ProjectLocation", "Autodesk.Revit.DB.BasePoint", "Autodesk.Revit.DB.InternalOrigin" }, new[] { "AcquireCoordinates", "PublishCoordinates" }, prio: "P1"),

                // ---------------- F04 Parámetros y materiales ----------------
                Typed("F04-PARAM-SET-001", "F04", "parámetros", "Fijar parámetro de instancia", "Set instance parameter", "UPDATE", "set_param", "valor escrito y releído igual (unidades verificadas)", plan, cats: new[] { "*" }, prio: "P0"),
                Typed("F04-PARAM-VALUES-001", "F04", "parámetros", "Fijar valores distintos por elemento", "Set per-element values", "UPDATE", "set_values", "cada elemento con su valor; omitidos explicados", plan, prio: "P0"),
                Typed("F04-PARAM-TYPE-001", "F04", "parámetros", "Fijar parámetro de tipo", "Set type parameter", "UPDATE", "set_type_param", "valor de tipo escrito y releído", plan, prio: "P1"),
                Typed("F04-TYPE-CHANGE-001", "F04", "tipos", "Cambiar tipo de elementos", "Change type", "RETYPE", "change_type", "elementos con el tipo pedido", plan, prio: "P0"),
                new CapabilityEntry { Id = "F04-PARAM-READ-001", Domain = "F04", Subdomain = "parámetros", Name = "Consultar parámetros (instancia y tipo)", NameEn = "Read parameters", Operation = "QUERY", Expected = "tabla compacta paginada con cursor ligado a revisión", Strategy = "query:query", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { QueryRunner, "src/PDI.RevitMcp.Engine/ResultTable.cs" }, Tests = new[] { "QuerySnapshotTests", "QueryTests" }, Verifier = "igualdad de filas entre paginación por cursor y lectura clásica (offline)", Priority = "P0" },
                Dyn("F04-PARAM-SHARED-001", "F04", "parámetros compartidos", "Añadir parámetro compartido a categorías", "Add shared parameter", "CREATE", "parámetro ligado a categorías con grupo", new[] { "Autodesk.Revit.DB.SharedParameterElement" }, new[] { "SharedParameters" }, prio: "P1"),
                Dyn("F04-PARAM-GLOBAL-001", "F04", "parámetros globales", "Crear y asociar parámetro global", "Global parameters", "CREATE", "parámetro global con valor y asociaciones", new[] { "Autodesk.Revit.DB.GlobalParameter::Create" }, new[] { "GlobalParameters" }),
                Dyn("F04-MAT-CREATE-001", "F04", "materiales", "Crear o duplicar material", "Create material", "CREATE", "material con apariencia y propiedades", new[] { "Autodesk.Revit.DB.Material::Create" }, new[] { "Materials" }),

                // ---------------- F05–F08 Arquitectura ----------------
                Model("F05-WALL-CREATE-001", "F05", "muros", "Crear muros por polilínea", "Create wall", "CREATE", "wall_create", "un muro por tramo con el tipo, nivel, altura (o nivel superior) y desfase pedidos", new[] { "Autodesk.Revit.DB.Wall::Create" }, new[] { "ArchitecturalWall", "StructuralWall" }, limits: "tramos rectos; sin uniones forzadas ni muros de perfil. Efecto en Revit pendiente de la campaña nativa"),
                Dyn("F05-WALL-JOIN-001", "F05", "muros", "Unir o separar geometría", "Join / unjoin geometry", "JOIN", "uniones de geometría según lo pedido", new[] { "Autodesk.Revit.DB.JoinGeometryUtils::JoinGeometry" }, new[] { "JoinGeometry", "UnjoinGeometry" }),
                Missing("F05-WALL-SPLIT-001", "F05", "muros", "Dividir muro", "Split wall", "SPLIT", "dos muros con la misma tipología y alojados reasignados", null, new[] { "SplitElement" }),
                Model("F06-FLOOR-CREATE-001", "F06", "suelos", "Crear suelo por contorno", "Create floor", "CREATE", "floor_create", "suelo del tipo y nivel pedidos con contorno cerrado y desfase", new[] { "Autodesk.Revit.DB.Floor::Create" }, new[] { "ArchitecturalFloor", "StructuralFloor" }, limits: "un contorno sin huecos ni pendiente. Efecto en Revit pendiente de la campaña nativa"),
                Model("F06-CEIL-CREATE-001", "F06", "techos", "Crear techo por contorno", "Create ceiling", "CREATE", "ceiling_create", "techo del tipo pedido a la altura pedida sobre el nivel", new[] { "Autodesk.Revit.DB.Ceiling::Create" }, new[] { "AutomaticCeiling" }, limits: "contorno explícito (no detección automática de recinto). Efecto en Revit pendiente de la campaña nativa"),
                Dyn("F06-ROOF-CREATE-001", "F06", "cubiertas", "Crear cubierta por huella o extrusión", "Create roof", "CREATE", "cubierta con pendientes definidas", new[] { "Autodesk.Revit.DB.FootPrintRoof", "Autodesk.Revit.DB.ExtrusionRoof" }, new[] { "RoofByFootprint", "RoofByExtrusion" }),
                Typed("F07-COMP-PLACE-001", "F07", "componentes", "Colocar componente (familia cargada)", "Place component", "CREATE", "place", "instancia del tipo pedido en la posición, nivel y anfitrión pedidos", plan, new[] { "PlaceAComponent" }, prio: "P0"),
                Typed("F07-COMP-SNAP-001", "F07", "componentes", "Ajustar a superficie (montaje real)", "Snap to host surface", "UPDATE", "snap_to_surface", "elemento a la altura de la cara real (incluye vínculos)", new[] { "GeoComputeTests", "PlanTests" }, prio: "P1"),
                Dyn("F07-DOOR-WIN-001", "F07", "puertas y ventanas", "Colocar puerta o ventana en muro", "Place door / window", "CREATE", "hueco alojado en el muro con orientación", new[] { "Autodesk.Revit.DB.FamilyInstance" }, new[] { "Door", "Window" }, prio: "P1"),
                Dyn("F08-STAIR-CREATE-001", "F08", "escaleras", "Crear escalera por componentes", "Create stair", "CREATE", "escalera con tramos y descansillos", new[] { "Autodesk.Revit.DB.Architecture.Stairs" }, new[] { "Stair" }, "edición de escaleras exige StairsEditScope; sin verificador tipado"),
                Dyn("F08-RAIL-CREATE-001", "F08", "barandillas", "Crear barandilla por trayectoria", "Create railing", "CREATE", "barandilla del tipo pedido", new[] { "Autodesk.Revit.DB.Architecture.Railing::Create" }, new[] { "Railing" }),
                Dyn("F08-CURTAIN-001", "F08", "muros cortina", "Editar rejilla de muro cortina", "Curtain grid", "UPDATE", "líneas de rejilla y paneles según lo pedido", new[] { "Autodesk.Revit.DB.CurtainGrid" }, new[] { "CurtainGrid" }),

                // ---------------- F09 Habitaciones y áreas ----------------
                Model("F09-ROOM-CREATE-001", "F09", "habitaciones", "Crear habitación en circuito cerrado", "Create room", "CREATE", "room_create", "habitación delimitada (área > 0) con nombre y número", new[] { "Autodesk.Revit.DB.Architecture.Room" }, new[] { "Room" }),
                new CapabilityEntry { Id = "F09-ROOM-READ-001", Domain = "F09", Subdomain = "habitaciones", Name = "Consultar habitaciones, áreas y espacios", NameEn = "Query rooms", Operation = "QUERY", Expected = "nombre, número, nivel, área y estado de delimitación", Strategy = "query:query", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { QueryRunner }, Tests = new[] { "QuerySnapshotTests" }, Verifier = "tabla de consulta", Priority = "P1" },
                Missing("F09-AREA-PLAN-001", "F09", "áreas", "Crear plano de áreas y esquema", "Area plan", "CREATE", "plano de áreas con esquema", new[] { "Autodesk.Revit.DB.Area" }, new[] { "AreaPlan" }),

                // ---------------- F10–F11 Massing y site ----------------
                Missing("F10-MASS-001", "F10", "masas", "Crear masa conceptual in situ", "In-place mass", "CREATE", "masa con caras para elementos por cara", null, new[] { "InPlaceMass" }, "modelado conceptual depende del editor de masas; sin operación tipada", "P3"),
                Dyn("F11-TOPO-001", "F11", "topografía", "Crear toposólido (2024+) o superficie topográfica (≤2023)", "Create toposolid / topography", "CREATE", "terreno desde puntos o contorno", new[] { "Autodesk.Revit.DB.Toposolid::Create", "Autodesk.Revit.DB.Architecture.TopographySurface" }, new[] { "Toposolid", "Toposurface" }, "Toposolid existe desde 2024 según el inventario; 2022–2023 usan TopographySurface"),

                // ---------------- F12–F15 Estructura ----------------
                Typed("F12-STR-PLACE-001", "F12", "estructura", "Colocar pilar o viga estructural (familia cargada)", "Place structural column / beam", "CREATE", "place", "instancia estructural con nivel y tipo", plan, new[] { "StructuralColumn", "Beam" }, prio: "P2", limits: "colocación genérica por place; sin parámetros estructurales específicos ni verificación de uniones"),
                Dyn("F13-STEEL-001", "F13", "acero", "Conexiones y elementos de acero estructural", "Structural steel connections", "CREATE", "conexión de acero entre miembros con su tipo", new[] { "Autodesk.Revit.DB.Structure.StructuralConnectionHandler", "Autodesk.Revit.DB.Steel.SteelElementProperties" }, null, "conexiones dependen de los tipos de conexión instalados; sin verificador tipado", "P3"),
                Dyn("F14-REBAR-001", "F14", "refuerzo", "Crear armadura desde curvas", "Create rebar", "CREATE", "armadura con forma y recubrimientos", new[] { "Autodesk.Revit.DB.Structure.Rebar::CreateFromCurves" }, new[] { "StructuralRebar", "RebarLine" }),
                Dyn("F15-ANALYTICAL-001", "F15", "modelo analítico", "Leer o crear elementos analíticos (2023+)", "Analytical model", "READ", "elementos analíticos asociados al físico", new[] { "Autodesk.Revit.DB.Structure.AnalyticalElement", "Autodesk.Revit.DB.Structure.AnalyticalMember" }, null, "AnalyticalModel (≤2022) se sustituye por AnalyticalElement en 2023 según el inventario"),

                // ---------------- F16–F22 MEP ----------------
                new CapabilityEntry { Id = "F16-CONN-FAM-001", Domain = "F16", Subdomain = "conectores", Name = "Crear o adoptar conector en familia", NameEn = "Family connector", Operation = "CREATE", Expected = "conector en la cara pedida sin duplicar", Strategy = "fam:conector", DocumentKind = "family", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { FamilyOps }, Tests = new[] { "FamilyLibraryTests" }, Verifier = "conector leído tras la transacción", Priority = "P1" },
                Dyn("F16-CONN-CONNECT-001", "F16", "conectores", "Conectar elementos MEP por conectores", "Connect MEP elements", "CONNECT", "conectores emparejados y sistema común", new[] { "Autodesk.Revit.DB.Connector", "Autodesk.Revit.DB.ConnectorManager" }, null, prio: "P1"),
                Model("F17-DUCT-CREATE-001", "F17", "conductos", "Crear conducto entre puntos", "Create duct", "CREATE", "duct_create", "conducto recto con tipo, sistema, nivel y tamaño", new[] { "Autodesk.Revit.DB.Mechanical.Duct::Create" }, new[] { "Duct" }, limits: "entre puntos; la conexión a conectores y los accesorios siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa"),
                Dyn("F17-SYS-CREATE-001", "F17", "sistemas", "Crear sistema mecánico", "Create mechanical system", "CREATE", "sistema con equipo base y terminales", new[] { "Autodesk.Revit.DB.Mechanical.MechanicalSystem::Create" }, null),
                Model("F18-PIPE-CREATE-001", "F18", "tuberías", "Crear tubería entre puntos", "Create pipe", "CREATE", "pipe_create", "tubería recta con tipo, sistema, nivel y diámetro", new[] { "Autodesk.Revit.DB.Plumbing.Pipe::Create" }, new[] { "Pipe" }, limits: "entre puntos; la conexión a conectores y los accesorios siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa"),
                Dyn("F18-SYS-CREATE-001", "F18", "sistemas", "Crear sistema de tuberías", "Create piping system", "CREATE", "sistema con elementos conectados", new[] { "Autodesk.Revit.DB.Plumbing.PipingSystem::Create" }, null),
                Missing("F19-SPRINKLER-001", "F19", "protección contra incendios", "Colocar y conectar rociadores", "Place sprinklers", "CREATE", "rociadores conectados al sistema húmedo o seco", null, new[] { "Sprinkler" }),
                Model("F20-CIRCUIT-CREATE-001", "F20", "circuitos", "Crear circuito eléctrico de fuerza", "Create power circuit", "CREATE", "circuit_create", "circuito con los miembros pedidos (conector de fuerza libre comprobado al planificar y al aplicar)", new[] { "Autodesk.Revit.DB.Electrical.ElectricalSystem::Create" }, new[] { "CheckCircuits" }, limits: "verifica miembros y cuadro; la compatibilidad de tensión y polos la decide Revit (si la rechaza, el plan se revierte). Efecto en Revit pendiente de la campaña nativa", prio: "P0"),
                Model("F20-CIRCUIT-PANEL-001", "F20", "circuitos", "Asignar panel al crear el circuito", "Select panel", "ASSOCIATE", "circuit_create", "circuito alimentado por el cuadro pedido (BaseEquipment verificado)", new[] { "Autodesk.Revit.DB.Electrical.ElectricalSystem::Create" }, limits: "sólo al crear; reasignar el cuadro de un circuito existente sigue por C# dinámico. Efecto en Revit pendiente de la campaña nativa", prio: "P0"),
                Dyn("F20-WIRE-CREATE-001", "F20", "cableado", "Crear cableado", "Create wire", "CREATE", "cable con tipo y vértices", new[] { "Autodesk.Revit.DB.Electrical.Wire::Create" }, new[] { "ArcWire", "ChamferedWire", "SplineWire" }, prio: "P1"),
                Dyn("F20-CONDUIT-001", "F20", "canalizaciones", "Crear conduit o bandeja", "Create conduit / cable tray", "CREATE", "recorrido con tipo y nivel", new[] { "Autodesk.Revit.DB.Electrical.Conduit::Create", "Autodesk.Revit.DB.Electrical.CableTray::Create" }, new[] { "Conduit", "CableTray" }),
                Dyn("F20-PANELSCHED-001", "F20", "cuadros", "Crear tabla de cuadro eléctrico", "Panel schedule", "SCHEDULE", "tabla del cuadro con circuitos", new[] { "Autodesk.Revit.DB.Electrical.PanelScheduleView" }, new[] { "PanelSchedules" }),
                new CapabilityEntry { Id = "F20-LIGHT-QA-001", Domain = "F20", Subdomain = "iluminación", Name = "QA de luminarias y etiquetas", NameEn = "Lighting fixture QA", Operation = "VERIFY", Expected = "luminarias sin etiqueta, etiquetas huérfanas o duplicadas con ids", Strategy = "query:check", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Engine/LightingAudit.cs" }, Tests = new[] { "LightingAuditTests" }, Verifier = "reglas de etiquetado por tarea", Priority = "P1" },
                Missing("F21-LOWVOLT-001", "F21", "bajo voltaje", "Dispositivos y circuitos de datos, alarma y seguridad", "Low-voltage devices", "CREATE", "dispositivos conectados a sus sistemas", null, new[] { "Data", "FireAlarm" }),
                Dyn("F22-FAB-001", "F22", "fabricación", "Colocar o leer partes de fabricación MEP", "Fabrication parts", "READ", "parte de fabricación con servicio y configuración", new[] { "Autodesk.Revit.DB.FabricationPart", "Autodesk.Revit.DB.FabricationConfiguration" }, null, "depende de la configuración de fabricación cargada", "P3"),

                // ---------------- F23 Family Editor ----------------
                new CapabilityEntry { Id = "F23-FAM-INFO-001", Domain = "F23", Subdomain = "familias", Name = "Información de familia y caras", NameEn = "Family info", Operation = "READ", Expected = "tipos, parámetros y caras de referencia", Strategy = "fam:info", DocumentKind = "family", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { FamilyOps }, Tests = new[] { "FamilyLibraryTests" }, Verifier = "lectura en el documento de familia", Priority = "P1" },
                new CapabilityEntry { Id = "F23-FAM-PARAM-001", Domain = "F23", Subdomain = "familias", Name = "Crear o editar parámetro de familia", NameEn = "Family parameter", Operation = "UPDATE", Expected = "parámetro con tipo de dato, grupo y valor por tipo", Strategy = "fam:param", DocumentKind = "family", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { FamilyOps }, Tests = new[] { "FamilyLibraryTests" }, Verifier = "FamilyManager releído", Priority = "P1" },
                new CapabilityEntry { Id = "F23-FAM-LOAD-001", Domain = "F23", Subdomain = "familias", Name = "Cargar familia en proyecto desde carpeta autorizada", NameEn = "Load family", Operation = "CREATE", Expected = "familia y tipos disponibles en el proyecto", Strategy = "fam:cargar", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { FamilyOps }, Tests = new[] { "FamilyLibraryTests" }, Verifier = "tipos presentes tras la carga", PostableCommands = new[] { "LoadShapes" }, Priority = "P1" },

                // ---------------- F24 Vistas ----------------
                Typed("F24-VIEW-CREATE-001", "F24", "vistas", "Crear vista (planta, RCP, sección, callout)", "Create view", "CREATE", "view_create", "vista del tipo pedido con nivel, escala y plantilla", plan, new[] { "FloorPlan", "ReflectedCeilingPlan", "Section", "Callout" }, prio: "P0"),
                Dyn("F24-VIEW-ELEV-001", "F24", "vistas", "Crear alzado (marcador y vista)", "Create elevation", "CREATE", "marcador de alzado con su vista", null, new[] { "BuildingElevation", "FramingElevation" }, "view_create no admite alzados: sólo C# dinámico", "P2"),
                Typed("F24-VIEW-DUP-001", "F24", "vistas", "Duplicar vista (simple, con detalles o dependiente)", "Duplicate view", "DUPLICATE", "view_create", "copia con o sin detalle o dependiente (view_create tipo=duplicar)", plan, prio: "P1"),
                Typed("F24-VIEW-SET-001", "F24", "vistas", "Configurar vista (rango, recorte, escala, plantilla)", "Set view properties", "UPDATE", "view_set", "propiedades de vista releídas iguales", plan, new[] { "ManageViewTemplates", "ApplyTemplatePropertiesToCurrentView" }, prio: "P1"),
                Typed("F24-VIEW-HIDE-001", "F24", "visibilidad", "Ocultar o mostrar elementos y categorías en vista", "Hide in view", "UPDATE", "view_hide", "visibilidad pedida en la vista", plan, new[] { "HideElements", "HideCategory" }, prio: "P1"),
                Typed("F24-VIEW-OVERRIDE-001", "F24", "visibilidad", "Sustituciones gráficas y filtros de vista", "Graphic overrides / filters", "UPDATE", "view_override", "sustitución aplicada (color, patrón, línea) y filtro asignado", plan, new[] { "Filters", "VisibilityOrGraphics" }, prio: "P1"),
                Typed("F24-VIEW-3D-001", "F24", "vistas", "Crear vista 3D isométrica con caja de sección", "Create 3D view", "CREATE", "view_create", "vista 3D con caja de sección opcional (view_create tipo=3d)", plan, new[] { "Default3DView" }),
                Typed("F24-VIEW-DRAFT-001", "F24", "vistas", "Crear vista de dibujo", "Drafting view", "CREATE", "view_create", "vista de dibujo con escala (view_create tipo=dibujo)", plan, new[] { "DraftingView" }),
                new CapabilityEntry { Id = "F24-VIEW-CAPTURE-001", Domain = "F24", Subdomain = "vistas", Name = "Capturar imagen de vista con resaltado temporal", NameEn = "Capture view image", Operation = "EXPORT", Expected = "imagen de la vista con elementos resaltados sin cambiar el modelo", Strategy = "query:capture", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Addin/Revit/Capture.cs" }, Tests = new[] { "EndToEndTests" }, Verifier = "archivo de imagen; transacción temporal revertida", Priority = "P2" },

                // ---------------- F25 Anotaciones ----------------
                Typed("F25-TAG-001", "F25", "etiquetas", "Etiquetar elementos", "Tag elements", "TAG", "tag", "una etiqueta por elemento con tipo, orientación y directriz", plan, new[] { "TagByCategory" }, prio: "P0"),
                Typed("F25-TAG-MOVE-001", "F25", "etiquetas", "Mover etiquetas", "Move tags", "MOVE", "tag_move", "cabeza de etiqueta en la posición pedida", plan, prio: "P1"),
                Typed("F25-TEXT-001", "F25", "texto", "Crear nota de texto", "Text note", "CREATE", "text_note", "texto con tipo y posición", draw, new[] { "Text" }, prio: "P1"),
                Typed("F25-DIM-001", "F25", "cotas", "Crear cota entre referencias", "Dimension", "CREATE", "dimension", "cota lineal entre referencias pedidas", draw, new[] { "AlignedDimension" }, prio: "P1"),
                Typed("F25-DETAIL-001", "F25", "detalle 2D", "Líneas, arcos, polígonos y rectángulos de detalle", "Detail lines", "CREATE", "detail_line", "geometría 2D en vista con estilo de línea", draw, new[] { "DetailLine" }, prio: "P1"),
                Typed("F25-DETAILCOMP-001", "F25", "detalle 2D", "Componente de detalle", "Detail component", "CREATE", "detail_component", "componente de detalle colocado", draw, new[] { "DetailComponent" }),
                Typed("F25-REGION-001", "F25", "detalle 2D", "Región rellena", "Filled region", "CREATE", "filled_region", "región con tipo y contorno", draw, new[] { "FilledRegion" }),
                Dyn("F25-REVCLOUD-001", "F25", "revisiones", "Nube de revisión", "Revision cloud", "CREATE", "nube asociada a una revisión", new[] { "Autodesk.Revit.DB.RevisionCloud::Create" }, new[] { "RevisionCloud" }),

                // ---------------- F26 Schedules ----------------
                Model("F26-SCHED-CREATE-001", "F26", "tablas", "Crear tabla de planificación con campos", "Create schedule", "SCHEDULE", "schedule_create", "tabla de la categoría con los campos pedidos en orden", new[] { "Autodesk.Revit.DB.ViewSchedule::CreateSchedule" }, new[] { "ScheduleOrQuantities" }, limits: "campos en orden; filtros, orden y agrupación siguen por C# dinámico. Efecto en Revit pendiente de la campaña nativa"),
                new CapabilityEntry { Id = "F26-SCHED-EXPORT-001", Domain = "F26", Subdomain = "tablas", Name = "Exportar tabla a CSV", NameEn = "Export schedule", Operation = "EXPORT", Expected = "CSV verificado con las filas de la tabla",
                    Strategy = "doc:tabla_csv", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Addin/Revit/Families/ExportOps.cs" }, PostableCommands = new[] { "ExportReportsSchedule" },
                    Verifier = "archivo nuevo, tamaño, hash y líneas frente a filas del cuerpo", Idempotency = "sobrescribir=si explícito", Limitations = "efecto en Revit pendiente de la campaña nativa", Priority = "P1" },

                // ---------------- F27 Sheets ----------------
                Typed("F27-SHEET-CREATE-001", "F27", "láminas", "Crear lámina con cajetín", "Create sheet", "CREATE", "sheet_create", "lámina con número, nombre y cajetín", new[] { "SheetSetOpTests", "PlanTests" }, new[] { "NewSheet" }, prio: "P0"),
                Typed("F27-SHEET-PLACE-001", "F27", "láminas", "Colocar vista en lámina", "Place view on sheet", "CREATE", "sheet_place", "viewport en la posición pedida", new[] { "SheetSetOpTests" }, prio: "P0"),
                Typed("F27-SHEET-SET-001", "F27", "láminas", "Editar parámetros de lámina", "Sheet parameters", "UPDATE", "sheet_set", "parámetros de lámina releídos", new[] { "SheetSetOpTests" }, prio: "P1"),
                Dyn("F27-SCHED-ONSHEET-001", "F27", "láminas", "Colocar tabla en lámina", "Place schedule on sheet", "CREATE", "instancia de tabla en lámina", new[] { "Autodesk.Revit.DB.ScheduleSheetInstance::Create" }, null),
                Dyn("F27-REVISION-001", "F27", "revisiones", "Crear y asignar revisiones a láminas", "Revisions", "UPDATE", "revisión asignada a láminas", new[] { "Autodesk.Revit.DB.Revision" }, new[] { "SheetIssuesOrRevisions" }),

                // ---------------- F28–F31 Coordinación y colaboración ----------------
                Dyn("F28-LINK-RVT-001", "F28", "vínculos", "Vincular modelo RVT con posicionamiento", "Link Revit model", "CREATE", "vínculo con posicionamiento (origen, compartido)", new[] { "Autodesk.Revit.DB.RevitLinkType::Create", "Autodesk.Revit.DB.RevitLinkInstance::Create" }, new[] { "LinkRevit" }, prio: "P1"),
                Dyn("F28-LINK-CAD-001", "F28", "vínculos", "Vincular o importar CAD", "Link CAD", "CREATE", "vínculo CAD con unidades y capas", new[] { "Autodesk.Revit.DB.CADLinkType" }, new[] { "LinkCAD", "ImportCAD" }),
                NoApi("F29-CM-BATCH-001", "F29", "copy/monitor", "Copy/Monitor: copia por lotes con monitorización", "Copy/Monitor batch copy", "MONITOR",
                    "elementos copiados del vínculo con relación de monitorización nativa y mapeo de tipos",
                    new[] { "Autodesk.Revit.DB.CopyMonitorOptions" }, new[] { "CopyMonitorSelectLink", "CopyMonitorUseCurrentProject" },
                    "el inventario confirma que CopyMonitorOptions no existe en la API pública 2022–2026. PostCommand sólo abre la herramienta: no completa ni verifica el workflow (#23, #24). 0.9.1: preflight, traspaso manual veraz y verificación posterior en F29-CM-VERIFY-001", "P0"),
                new CapabilityEntry { Id = "F29-CM-VERIFY-001", Domain = "F29", Subdomain = "copy/monitor", Name = "Copy/Monitor: preflight, traspaso manual y verificación de aparatos supervisados", NameEn = "Copy/Monitor preflight and verification", Operation = "VERIFY",
                    Expected = "inventario del vínculo y del anfitrión; tras la copia, cada aparato con tipo esperado, posición, orientación y relación de supervisión; duplicados y ausentes detectados",
                    Strategy = "query:check copy_monitor", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED,
                    Implementation = new[] { "src/PDI.RevitMcp.Addin/Revit/CopyMonitorProbe.cs", "src/PDI.RevitMcp.Addin/Revit/RequestHandler.CopyMonitor.cs", "src/PDI.RevitMcp.Engine/Workflows/CopyMonitorVerifier.cs", "src/PDI.RevitMcp.Engine/Workflows/NativeCapabilities.cs" },
                    Tests = new[] { "NativeWorkflowTests" }, PostableCommands = new[] { "CopyMonitorSelectLink" },
                    Verifier = "CopyMonitorVerifier contra expectativas CONGELADAS en el preflight (anfitrión, vínculo y revisión, origen, transformación, mapeo, exclusiones, categoría); un cambio del objetivo es conflicto. Correspondencia inferida por tipo y posición; supervisión por IsMonitoringLinkElement",
                    Limitations = "sólo lectura: la copia la hace el usuario con los pasos devueltos (no hay ejecutor de UI). Lectura en Revit pendiente de la campaña nativa", Priority = "P0" },
                NoApi("F29-CR-001", "F29", "coordination review", "Coordination Review: revisar y resolver avisos de monitorización", "Coordination Review", "VERIFY",
                    "avisos de monitorización revisados con acción registrada", new string[0], new[] { "CoordinationReviewUseCurrentProject", "CoordinationSelectLink" },
                    "sin API pública para Coordination Review; sólo flujo de UI supervisado", "P1"),
                Typed("F30-WS-SET-001", "F30", "worksharing", "Mover elementos a otro workset", "Set workset", "UPDATE", "set_workset", "elementos en el workset pedido", new[] { "WorksharedPolicyTests", "PlanTests" }, prio: "P1"),
                Dyn("F30-WS-CREATE-001", "F30", "worksharing", "Crear workset", "Create workset", "CREATE", "workset con nombre", new[] { "Autodesk.Revit.DB.Workset::Create" }, new[] { "Worksets" }),
                Typed("F31-GROUP-001", "F31", "organización", "Crear, colocar y desagrupar grupos", "Groups", "GROUP", "group_create", "grupo creado o colocado; desagrupado", new[] { "AdvancedEnginesTests", "PlanTests" }, new[] { "CreateGroup", "LoadAsGroup" }),
                Dyn("F31-PHASE-001", "F31", "fases", "Asignar fase de creación y derribo", "Phasing", "UPDATE", "fases asignadas por elemento", new[] { "Autodesk.Revit.DB.Phase" }, new[] { "Phases" }),
                Dyn("F31-OPTION-001", "F31", "opciones de diseño", "Opciones de diseño", "Design options", "UPDATE", "elementos en la opción pedida", new[] { "Autodesk.Revit.DB.DesignOption" }, new[] { "DesignOptions" }, prio: "P3"),
                Dyn("F31-ASSEMBLY-001", "F31", "organización", "Crear ensamblaje", "Create assembly", "CREATE", "ensamblaje con vistas opcionales", new[] { "Autodesk.Revit.DB.AssemblyInstance::Create" }, new[] { "CreateAssembly" }, prio: "P3"),

                // ---------------- F32–F35 Análisis, visualización, exportación, automatización ----------------
                new CapabilityEntry { Id = "F32-GEO-CLASH-001", Domain = "F32", Subdomain = "análisis", Name = "Detección de interferencias y montaje real", NameEn = "Clash / mounting analysis", Operation = "VERIFY", Expected = "interferencias y montaje clasificados con evidencia geométrica", Strategy = "query:geo", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Addin/Revit/Geo/GeoRunner.cs", "src/PDI.RevitMcp.Engine/Geo/GeoSummary.cs" }, Tests = new[] { "GeoComputeTests", "MeshTests" }, Verifier = "cálculo geométrico offline verificado (GeoComputeTests); captura de geometría en Revit pendiente de campaña", Priority = "P1" },
                Missing("F32-ENERGY-001", "F32", "análisis energético", "Modelo de análisis energético", "Energy analysis model", "CREATE", "modelo energético generado", new[] { "Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel" }, new[] { "EnergySettings" }, prio: "P3"),
                Dyn("F33-RENDER-IMG-001", "F33", "visualización", "Exportar imagen de vista", "Export image", "EXPORT", "imagen de la vista con resolución pedida", new[] { "Autodesk.Revit.DB.ImageExportOptions" }, null, prio: "P3"),
                new CapabilityEntry { Id = "F34-EXPORT-IFC-001", Domain = "F34", Subdomain = "exportación", Name = "Exportar IFC con opciones", NameEn = "Export IFC", Operation = "EXPORT", Expected = "archivo IFC con la versión y la vista pedidas, sin modificar el modelo",
                    Strategy = "doc:ifc", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Addin/Revit/Families/ExportOps.cs" },
                    ApiSurfaces = new[] { "Autodesk.Revit.DB.IFCExportOptions", "Autodesk.Revit.DB.Document::Export" }, PostableCommands = new[] { "ExportIFC" },
                    Verifier = "archivo nuevo, tamaño y hash; transacción revertida", Idempotency = "sobrescribir=si explícito",
                    Limitations = "sin mapeo de property sets ni archivo de configuración IFC (siguen por C# dinámico). Efecto en Revit pendiente de la campaña nativa", Priority = "P1" },
                Dyn("F34-EXPORT-DWG-001", "F34", "exportación", "Exportar DWG", "Export DWG", "EXPORT", "archivos DWG por vista o lámina", new[] { "Autodesk.Revit.DB.DWGExportOptions" }, new[] { "ExportCADFormatsDWG", "ExportCADFormatsDXF" }),
                Dyn("F34-EXPORT-NWC-001", "F34", "exportación", "Exportar NWC", "Export Navisworks", "EXPORT", "archivo NWC", new[] { "Autodesk.Revit.DB.NavisworksExportOptions" }, null, "requiere el exportador de Navisworks instalado"),
                new CapabilityEntry { Id = "F35-EXEC-001", Domain = "F35", Subdomain = "automatización", Name = "Ejecutar C# dinámico (consulta, ensayo, ejecutar)", NameEn = "Dynamic C#", Operation = "UPDATE", Expected = "script compilado y ejecutado con transacción, auditoría y bloqueo opcional", Strategy = "exec", DocumentKind = "any", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { ExecHandler, "src/PDI.RevitMcp.Addin/Scripting/ScriptHost.cs", "src/PDI.RevitMcp.Engine/CompileCache.cs" }, Tests = new[] { "ScriptingTests", "CompileCacheTests", "Rc3Tests" }, Verifier = "evidencia devuelta por el script; auditoría de intención y resultado", Limitations = "no es un sandbox; efectos externos sin rollback", Priority = "P0" },
                new CapabilityEntry { Id = "F35-RECIPE-001", Domain = "F35", Subdomain = "automatización", Name = "Tareas por recetas con concesión de capacidades", NameEn = "Recipe tasks", Operation = "UPDATE", Expected = "tarea autorizada por persona, por lotes, reanudable y reconciliable", Strategy = "engine", DocumentKind = "any", Status = CoverageStatus.OFFLINE_VERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Engine/Tasks/TaskManager.cs" }, Tests = new[] { "TaskEngineTests", "ReleaseGateTests" }, Verifier = "estado de tarea, lease con fencing y auditoría", Priority = "P1" },
                new CapabilityEntry { Id = "F35-AGENTHOST-001", Domain = "F35", Subdomain = "automatización", Name = "Planes IR durables con recuperación entre procesos", NameEn = "Durable IR plans", Operation = "UPDATE", Expected = "plan ejecutado una sola vez con recuperación tras caída", Strategy = "engine", DocumentKind = "any", Status = CoverageStatus.OFFLINE_VERIFIED, Implementation = new[] { "src/PDI.RevitMcp.AgentHost/Exec/PlanRunner.cs" }, Tests = new[] { "AgentHostProcessTests", "AgentHostScenarioTests", "AgentHostAlphaTests" }, Verifier = "modelo simulado durable + diario", Limitations = "verificado contra el backend simulado; contra Revit, pendiente de campaña", Priority = "P0" },

                // ---------------- F36–F37 QA/QC y workflows interactivos ----------------
                new CapabilityEntry { Id = "F36-QC-MANIFEST-001", Domain = "F36", Subdomain = "QA/QC", Name = "QC uno a uno contra manifiesto", NameEn = "QC against manifest", Operation = "VERIFY", Expected = "desviaciones de posición, altura, giro, tipo, nivel y etiquetas", Strategy = "exec", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Addin/recetas/qc_manifiesto.cs" }, Tests = new[] { "QcFixtureTests" }, Verifier = "postcondición con lista de desviaciones", Priority = "P1" },
                new CapabilityEntry { Id = "F36-QC-INDEP-001", Domain = "F36", Subdomain = "QA/QC", Name = "Verificación independiente contra fuente declarada", NameEn = "Independent verification", Operation = "VERIFY", Expected = "esperado de una fuente independiente frente al modelo", Strategy = "engine", Status = CoverageStatus.OFFLINE_VERIFIED, Implementation = new[] { "src/PDI.RevitMcp.Engine/Workflow/Traceability.cs" }, Tests = new[] { "TraceabilityTests", "Hardening090Tests" }, Verifier = "rechazo si la fuente esperada es la misma que guió la construcción", Priority = "P1" },
                Engine("F37-NWE-001", "F37", "workflows nativos", "Motor de workflows nativos: estados por evidencia, recibos e idempotencia", "Native Workflow Engine", "VERIFY",
                    "workflow con estados que sólo avanzan con evidencia observada, recibos persistentes por clave de intento, reconciliación ante lo incierto y sin re-PostCommand a ciegas",
                    new[] { "src/PDI.RevitMcp.Engine/Workflows/WorkflowReceipt.cs", "src/PDI.RevitMcp.Engine/Workflows/NativeCapabilities.cs" }, new[] { "NativeWorkflowTests" },
                    "máquina de estados + almacén de recibos con inyección de fallos sintética",
                    "sin ejecutor de UI en el producto: los flujos interactivos terminan en traspaso manual; la observación del modo nativo en Revit queda para la campaña nativa"),
                Engine("F37-TARGET-001", "F37", "destino", "Destino estable por instancia y documento (multi-Revit)", "Stable instance/document targeting", "VERIFY",
                    "operación vinculada a (pid, inicio del proceso) y a la identidad del documento; ambigüedad, dN de otra sesión y pid reutilizado se rechazan",
                    new[] { "src/PDI.RevitMcp.Engine/Workflows/TargetBinding.cs" }, new[] { "NativeWorkflowTests.TwoRevitInstances_RequireAnExplicitTarget_AndStaleHandlesCannotWrite" },
                    "resolución y revalidación del destino con instancias sintéticas", "falta conectar la lista real de instancias del Bridge (diagnóstico de #25)"),
                Engine("F37-DIAG-001", "F37", "diagnóstico", "Diagnóstico de conexión: Bridge, registro del cliente, herramientas, Revit", "Connection diagnostics", "VERIFY",
                    "veredicto preciso que separa registro/permisos del cliente de la disponibilidad de Revit",
                    new[] { "src/PDI.RevitMcp.Engine/Workflows/TargetBinding.cs" }, new[] { "NativeWorkflowTests.Diagnostics_SeparateClientRegistrationFromRevitAvailability" },
                    "clasificación con observaciones sintéticas", "las observaciones de registro por cliente se miden fuera del motor (instalador / Bridge)"),
                Engine("F37-PDF-PLAN-001", "F37", "PDF→Revit", "Plan de colocación desde PDF (documento → plan)", "PDF to placement plan", "CREATE",
                    "plan de colocación con trazabilidad e incertidumbre explícita", new[] { "src/PDI.RevitMcp.Engine/PowerEngine/Workflows/PdfToPlan.cs" }, new[] { "PdfToModelWorkflowTests", "AgentHostDocumentTests" },
                    "escenarios sintéticos con expectativas conocidas", "valida el documento y el plan y lo convierte en planes tipados con testigo de origen; el commit aplica la clave derivada del contenido (idempotente sin que el agente la propague, sobre el diario de operaciones). La aplicación en Revit queda pendiente de campaña"),

                // ---------------- Garantías del motor (transversales) ----------------
                Engine("F35-AUDIT-001", "F35", "garantías", "Auditoría durable con group commit y recuperación", "Durable audit journal", "VERIFY",
                    "cadena íntegra tras caídas en cualquier punto, sin duplicados ni confirmaciones falsas", new[] { "src/PDI.RevitMcp.Engine/AuditLog.cs" },
                    new[] { "AuditRecoveryTests", "AuditGroupCommitTests", "AuditLogTests", "ReleaseGateTests" }, "verificación de cadena, cabeza y lotes"),
                Engine("F35-QUERY-SNAP-001", "F35", "garantías", "Lectura paginada desde una instantánea ligada a revisión", "Revision-bound query snapshots", "QUERY",
                    "páginas idénticas a la lectura completa, sin mezclar revisiones, con presupuesto de memoria", new[] { "src/PDI.RevitMcp.Engine/QuerySnapshots.cs" },
                    new[] { "QuerySnapshotTests" }, "igualdad de filas y revalidación tras LiveRev"),
                Engine("F35-IDENTITY-001", "F35", "garantías", "Identidad nominal y correlación de solicitudes", "Agent identity and correlation", "VERIFY",
                    "toda operación atribuida a un agente con nombre e ids de correlación únicos", new[] { "src/PDI.RevitMcp.Engine/Correlation.cs", "src/PDI.RevitMcp.Engine/Sessions.cs" },
                    new[] { "AgentIdentityGateTests" }, "diario con agent_id, session_id, request_id, trace_id"),
            };
            Expand(l);
            return l;
        }

        /// <summary>Resumen por estado y por dominio (para el informe y el panel).</summary>
        public static Dictionary<string, object> Summary()
        {
            var byStatus = All.GroupBy(e => e.Status.ToString()).ToDictionary(g => g.Key, g => (object)(long)g.Count());
            var byDomain = All.GroupBy(e => e.Domain).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (object)g.GroupBy(e => e.Status.ToString()).ToDictionary(x => x.Key, x => (object)(long)x.Count()));
            return new Dictionary<string, object> { ["capacidades"] = (long)All.Count, ["por_estado"] = byStatus, ["por_dominio"] = byDomain };
        }
    }
}
