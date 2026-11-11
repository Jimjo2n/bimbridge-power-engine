// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Engine.Coverage
{
    /// <summary>
    /// 0.9.1 (directiva de inventario universal): descomposición de F01–F37 en subcapacidades operativas y enlace de comandos de la
    /// cinta con su capacidad. Reglas de esta ampliación:
    /// - DYNAMIC_CSHARP_ONLY sólo cita superficies que EXISTEN en el listado completo de símbolos (lo comprueba CapabilityCatalogTests);
    ///   existir no implica que el flujo esté probado: falta operación tipada, verificador y prueba nativa.
    /// - UNKNOWN = sin evidencia suficiente: se registra la investigación pendiente en vez de inventar soporte.
    /// - NOT_APPLICABLE = función de la interfaz o de la sesión de Revit que no es una operación sobre el modelo.
    /// - Enlazar un comando a una capacidad tipada exige que la operación haga lo mismo; las diferencias van en limitaciones.
    /// </summary>
    public static partial class CapabilityCatalog
    {
        private static CapabilityEntry Unknown(string id, string dom, string sub, string es, string en, string op, string expected, string[] cmds, string pending, string prio = "P2") => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = op, Expected = expected, Strategy = "none",
            Status = CoverageStatus.UNKNOWN, PostableCommands = cmds ?? new string[0], Limitations = "investigación pendiente: " + pending, Priority = prio,
        };

        private static CapabilityEntry NotApplicable(string id, string dom, string sub, string es, string en, string[] cmds, string why) => new CapabilityEntry
        {
            Id = id, Domain = dom, Subdomain = sub, Name = es, NameEn = en, Operation = "UI", Expected = "no aplica: " + why, Strategy = "none", DocumentKind = "any",
            Status = CoverageStatus.NOT_APPLICABLE, PostableCommands = cmds ?? new string[0], Limitations = why, Priority = "P3",
        };

        private static CapabilityEntry WithLimits(CapabilityEntry e, string limits) { e.Limitations = limits; return e; }

        private const string DB = "Autodesk.Revit.DB.";

        private static void Expand(List<CapabilityEntry> l)
        {
            var plan = new[] { "PlanTests", "WriteEngineTests" };
            l.AddRange(new[]
            {
                // ---------------- F01 Documentos ----------------
                Dyn("F01-DOC-CLOUD-001", "F01", "documentos en la nube", "Guardar, abrir y gestionar modelos en la nube", "Cloud models", "SAVE", "modelo guardado o abierto en ACC/BIM 360 con su ruta en la nube",
                    new[] { DB + "Document::SaveAsCloudModel" }, new[] { "SaveAsCloudModel", "OpenCloudModel", "ManageCloudModels" }, "requiere sesión de Autodesk y permisos del proyecto en la nube", "P2"),
                Dyn("F01-DOC-TEMPLATE-001", "F01", "plantillas", "Guardar como plantilla y crear proyecto desde plantilla", "Templates", "SAVE", "archivo .rte o proyecto nuevo desde la plantilla pedida",
                    new[] { DB + "Document::SaveAs" }, new[] { "SaveAsTemplate", "NewProject", "ManageTemplates", "EditATemplate" }),
                Unknown("F01-DOC-STANDARDS-001", "F01", "estándares", "Transferir normas de proyecto", "Transfer project standards", "UPDATE", "tipos y estilos copiados del proyecto origen",
                    new[] { "TransferProjectStandards" }, "no se ha localizado una API pública equivalente al diálogo; candidato: ElementTransformUtils.CopyElements entre documentos para tipos (no equivale a todas las categorías del diálogo)"),
                Unknown("F01-DOC-REPAIR-001", "F01", "recuperación", "Reparar modelo central y restaurar copia de seguridad", "Repair central / restore backup", "RECOVER", "modelo recuperado con evidencia",
                    new[] { "RepairCentralModel", "RestoreBackup" }, "sin API pública identificada para ambos diálogos; requiere flujo manual"),
                NotApplicable("F01-UI-SESSION-001", "F01", "sesión", "Salir de Revit, opciones de la aplicación y archivos de ejemplo", "Session and application UI",
                    new[] { "ExitRevit", "Options", "OpenSampleFiles", "Close", "OpenBuildingComponent" }, "acciones de la aplicación o de la sesión del usuario; no son operaciones sobre el modelo (cerrar un documento ya está en F01-DOC-CLOSE-001)"),

                // ---------------- F02 Edición ----------------
                Dyn("F02-EDIT-PIN-001", "F02", "edición", "Fijar y liberar elementos", "Pin / unpin", "UPDATE", "elementos con Pinned según lo pedido",
                    new[] { DB + "Element::Pinned" }, new[] { "Pin", "Unpin" }, prio: "P2"),
                Dyn("F02-EDIT-PASTE-001", "F02", "edición", "Copiar al portapapeles y pegar alineado (vistas, niveles, mismo lugar)", "Paste aligned", "COPY", "copias en los niveles o vistas pedidos con el desplazamiento correcto",
                    new[] { DB + "ElementTransformUtils::CopyElements" }, new[] { "CopyToClipboard", "CutToClipboard", "PasteFromClipboard", "AlignedToCurrentView", "AlignedToPickedLevel", "AlignedToSamePlace", "AlignedToSelectedLevels", "AlignedToSelectedViews" },
                    "el portapapeles de Revit no tiene API: el equivalente es CopyElements con transformación (copy tipado cubre traslado y nivel)", "P1"),
                Dyn("F02-EDIT-CUT-001", "F02", "edición", "Cortar y unir geometría, cambiar orden de unión", "Cut / join geometry", "JOIN", "relaciones de corte/unión según lo pedido",
                    new[] { DB + "SolidSolidCutUtils::AddCutBetweenSolids", DB + "InstanceVoidCutUtils::AddInstanceVoidCut", DB + "JoinGeometryUtils::SwitchJoinOrder" }, new[] { "CutGeometry", "UncutGeometry", "SwitchJoinOrder", "CutBy" }),
                Unknown("F02-EDIT-TRIM-001", "F02", "edición", "Recortar/extender y dividir con hueco", "Trim / extend / split with gap", "UPDATE", "curvas de ubicación recortadas o extendidas",
                    new[] { "TrimOrExtendToCorner", "TrimOrExtendSingleElement", "TrimOrExtendMultipleElements", "SplitWithGap", "DisplaceElements" }, "no hay método público de recorte; posible vía LocationCurve.Curve por categoría, a validar por tipo de elemento"),
                WithLimits(Typed("F02-EDIT-MATCH-001", "F02", "edición", "Igualar propiedades de tipo", "Match type properties", "UPDATE", "change_type", "elementos con el tipo del elemento de referencia", plan, new[] { "MatchTypeProperties" }),
                    "change_type necesita el tipo explícito; el «pincel» de la cinta lo toma de un elemento elegido. Efecto en Revit pendiente de la campaña nativa"),
                WithLimits(Typed("F02-EDIT-SIMILAR-001", "F02", "edición", "Crear similar (colocar otro del mismo tipo)", "Create similar", "CREATE", "place", "instancia del mismo tipo en la posición pedida", plan, new[] { "CreateSimilar" }),
                    "place con el tipo del elemento de referencia; familias alojadas requieren anfitrión. Efecto en Revit pendiente de la campaña nativa"),
                Dyn("F02-SEL-FILTER-001", "F02", "selección", "Guardar, cargar y editar selecciones guardadas", "Saved selections", "QUERY", "SelectionFilterElement con los elementos pedidos",
                    new[] { DB + "SelectionFilterElement::Create" }, new[] { "SaveSelection", "LoadSelection", "EditSelection", "AddToSet" }, "los conjuntos con nombre de BIMBRIDGE ($conjunto) viven en la sesión; esto persiste en el modelo", "P2"),
                NotApplicable("F02-UI-SELECT-001", "F02", "selección", "Seleccionar por id, ids de la selección y deshacer/rehacer", "Selection UI and undo",
                    new[] { "SelectById", "IdsOfSelection", "Undo", "Redo", "ActivateControlsAndDimensions" }, "herramientas de la interfaz; BIMBRIDGE lee ids con query y deshace con un TransactionGroup por plan"),

                // ---------------- F03 Coordenadas y datums ----------------
                Dyn("F03-COORD-SHARED-001", "F03", "coordenadas", "Coordenadas compartidas: informar, especificar, reubicar y girar norte", "Shared coordinates", "UPDATE", "ProjectLocation/SiteLocation con la posición y el norte pedidos",
                    new[] { DB + "ProjectLocation", DB + "SiteLocation" }, new[] { "ReportSharedCoordinates", "SpecifyCoordinatesAtPoint", "ResetSharedCoordinates", "RelocateProject", "RotateProjectNorth", "RotateTrueNorth", "Location", "MirrorProject" }, prio: "P1"),
                Dyn("F03-WORKPLANE-001", "F03", "planos de trabajo", "Definir y mostrar plano de trabajo", "Work plane", "UPDATE", "SketchPlane activo según lo pedido",
                    new[] { DB + "SketchPlane::Create" }, new[] { "SetWorkPlane", "ShowWorkPlane", "PickAPlane", "FormWorkPlaneView" }),

                // ---------------- F04 Estándares ----------------
                Dyn("F04-STD-STYLES-001", "F04", "estilos", "Patrones de línea y relleno, estilos y grosores de línea, estilos de objeto", "Line/fill patterns and object styles", "UPDATE", "patrones y estilos creados o ajustados",
                    new[] { DB + "LinePatternElement::Create", DB + "FillPatternElement::Create", DB + "Category::SetLineWeight" }, new[] { "LinePatterns", "FillPatterns", "LineStyles", "LineWeights", "ObjectStyles", "Arrowheads" }),
                Dyn("F04-PROJ-INFO-001", "F04", "proyecto", "Información y unidades del proyecto", "Project information and units", "UPDATE", "ProjectInfo y Units según lo pedido",
                    new[] { DB + "ProjectInfo", DB + "Document::SetUnits" }, new[] { "ProjectInformation", "ProjectUnits" }, prio: "P1"),
                Dyn("F04-PARAM-PROJ-001", "F04", "parámetros", "Parámetros de proyecto y de categoría de familia", "Project parameters", "CREATE", "parámetros enlazados a las categorías pedidas",
                    new[] { DB + "ParameterElement" }, new[] { "ProjectParameters", "FamilyCategoryAndParameters", "ParametersService" }, prio: "P1"),
                Dyn("F04-KEYNOTE-001", "F04", "notas clave", "Configuración de notas clave", "Keynoting settings", "UPDATE", "tabla de notas clave cargada", new[] { DB + "KeynoteTable" }, new[] { "KeynotingSettings" }),
                Unknown("F04-MAT-ASSET-001", "F04", "materiales", "Recursos de apariencia y física de materiales", "Material assets", "UPDATE", "material con los recursos pedidos",
                    new[] { "MaterialAssets" }, "AppearanceAssetElement existe pero la edición de recursos tiene restricciones por versión; no verificado"),

                // ---------------- F05 Muros ----------------
                Dyn("F05-WALL-OPEN-001", "F05", "muros", "Hueco en muro", "Wall opening", "CREATE", "hueco rectangular en el muro", new[] { "Autodesk.Revit.Creation.Document::NewOpening" }, new[] { "WallOpening" }),
                Dyn("F05-WALL-JOINS-001", "F05", "muros", "Uniones de muros (permitir/impedir en extremos)", "Wall joins", "JOIN", "unión del extremo según lo pedido", new[] { DB + "WallUtils::DisallowWallJoinAtEnd" }, new[] { "WallJoins" }),
                Unknown("F05-WALL-SWEEP-001", "F05", "muros", "Barridos y telares de muro, muro por cara", "Wall sweeps / reveals / by face", "CREATE", "barrido o telar en el muro",
                    new[] { "SweepWall", "RevealWall", "WallByFaceWall" }, "WallSweep.Create existe en algunas versiones; muro por cara depende de masas; sin verificar"),

                // ---------------- F06 Suelos, cubiertas, huecos ----------------
                Dyn("F06-OPENING-001", "F06", "huecos", "Huecos verticales, de patinillo, buhardilla y por cara", "Shaft / vertical / dormer openings", "CREATE", "hueco con el contorno y los niveles pedidos",
                    new[] { "Autodesk.Revit.Creation.Document::NewOpening" }, new[] { "ShaftOpening", "VerticalOpening", "DormerOpening", "OpeningByFace", "Opening" }, prio: "P1"),
                Unknown("F06-EDGES-001", "F06", "bordes", "Impostas, canalones, sofitos y bordes de losa; suelo y cubierta por cara", "Fascia, gutter, soffit, slab edge, by face", "CREATE", "perfil barrido sobre los bordes pedidos",
                    new[] { "Fascia", "Gutter", "Soffit", "SlabEdgeFloor", "FloorByFaceFloor", "RoofByFace", "JoinOrUnjoinRoof" }, "API de creación de estos barridos no confirmada en el listado; requiere investigación por versión"),

                // ---------------- F07 Componentes ----------------
                Dyn("F07-MODELLINE-001", "F07", "líneas de modelo", "Líneas de modelo y simbólicas", "Model / symbolic lines", "CREATE", "curvas de modelo en el plano de trabajo pedido",
                    new[] { "Autodesk.Revit.Creation.ItemFactoryBase::NewModelCurve" }, new[] { "ModelLine", "SymbolicLine" }),
                Dyn("F07-MODELTEXT-001", "F07", "texto de modelo", "Texto de modelo 3D", "Model text", "CREATE", "texto de modelo con el tipo y el texto pedidos", new[] { DB + "ModelText" }, new[] { "ModelText" }, prio: "P3"),
                Unknown("F07-INPLACE-001", "F07", "familias in situ", "Modelar in situ y dibujar en cara/plano de trabajo", "Model in place", "CREATE", "familia in situ", new[] { "ModelInPlace", "DrawOnFace", "DrawOnWorkPlane", "ArchitecturalColumn", "RepeatComponent" },
                    "modelado in situ no tiene API pública de edición de familia in situ; colocación en cara/plano son opciones de place"),
                WithLimits(Typed("F16-MEP-PLACE-001", "F16", "MEP", "Colocar equipos, terminales, aparatos y dispositivos MEP (familia cargada)", "Place MEP equipment and devices", "CREATE", "place",
                    "instancia del tipo pedido en la posición y nivel pedidos", plan,
                    new[] { "AirTerminal", "MechanicalEquipment", "MechanicalControlDevice", "PlumbingFixture", "PlumbingEquipment", "ElectricalEquipment", "ElectricalFixture", "LightingFixture", "Communication", "Telephone", "NurseCall", "Security", "Control" }),
                    "colocación genérica: no conecta a sistemas ni a conectores, ni aloja en caras. Efecto en Revit pendiente de la campaña nativa"),

                // ---------------- F08 Circulación y cortinas ----------------
                Dyn("F08-STAIR-EDIT-001", "F08", "escaleras", "Recorrido, numeración y elementos sobre escaleras/rampas", "Stair path, numbering, place on stair", "CREATE", "anotación o elemento sobre la escalera",
                    new[] { DB + "StairsEditScope" }, new[] { "StairPath", "StairTreadOrRiserNumber", "PlaceOnStairOrRamp", "Ramp" }),
                Unknown("F08-CURTAIN-SYS-001", "F08", "muros cortina", "Sistema cortina por cara y montantes", "Curtain system by face, mullions", "CREATE", "sistema cortina con rejilla y montantes",
                    new[] { "CurtainSystemByFace", "CurtainWallMullion" }, "montantes vía CurtainGrid/Mullion; sistema por cara depende de masas; sin verificar"),

                // ---------------- F09 Habitaciones y áreas ----------------
                Dyn("F09-BOUNDARY-001", "F09", "límites", "Separadores de habitación y de espacio", "Room / space separators", "CREATE", "líneas de separación que delimitan el recinto",
                    new[] { "Autodesk.Revit.Creation.Document::NewRoomBoundaryLines", "Autodesk.Revit.Creation.Document::NewSpaceBoundaryLines" }, new[] { "RoomSeparator", "SpaceSeparator" }, prio: "P1"),
                Dyn("F09-AREA-001", "F09", "áreas", "Áreas, contornos de área y cálculo de áreas/volúmenes", "Areas and area boundaries", "CREATE", "área delimitada en el esquema pedido",
                    new[] { DB + "AreaScheme", "Autodesk.Revit.Creation.Document::NewAreaBoundaryLine", DB + "ViewPlan::CreateAreaPlan" }, new[] { "Area", "AreaBoundary", "AreaAndVolumeComputations" }, prio: "P1"),
                Dyn("F09-TAG-001", "F09", "etiquetas", "Etiquetas de habitación, área y espacio", "Room / area / space tags", "TAG", "etiqueta en la vista pedida sobre el recinto",
                    new[] { "Autodesk.Revit.Creation.Document::NewRoomTag", "Autodesk.Revit.Creation.Document::NewAreaTag" }, new[] { "RoomTag", "AreaTag", "SpaceTag" }, "tag tipado usa IndependentTag y no etiqueta recintos", "P1"),
                Dyn("F09-COLORFILL-001", "F09", "esquemas de color", "Esquemas de color y leyendas de relleno", "Color fill schemes", "UPDATE", "esquema de color aplicado y leyenda colocada",
                    new[] { DB + "ColorFillScheme" }, new[] { "ColorFillLegend", "ColorSchemes", "DuctLegend", "PipeLegend" }),
                Dyn("F09-SPACE-001", "F09", "espacios", "Espacios MEP y su nomenclatura", "MEP spaces", "CREATE", "espacio delimitado con nombre y número",
                    new[] { "Autodesk.Revit.Creation.Document::NewSpace" }, new[] { "Space", "SpaceNaming" }, prio: "P1"),

                // ---------------- F10–F11 Masas y emplazamiento ----------------
                Unknown("F10-MASS-002", "F10", "masas", "Masas conceptuales: crear, colocar, mostrar y suelos de masa", "Conceptual mass", "CREATE", "masa con su forma y suelos",
                    new[] { "NewConceptualMass", "PlaceMass", "ShowMassByViewSettings", "ShowMassFormAndFloors", "ShowMassSurfaceTypes", "ShowMassZonesAndShades", "SplitFace", "Paint", "RemovePaint", "PanelByBoundary", "PanelByExtrusion" },
                    "edición de formas conceptuales en familias de masa; algunas tienen API (FamilyItemFactory), otras sólo UI"),
                Dyn("F11-PAD-001", "F11", "emplazamiento", "Plataformas de construcción", "Building pad", "CREATE", "plataforma con contorno y nivel", new[] { DB + "Architecture.BuildingPad::Create" }, new[] { "BuildingPad" }),
                Unknown("F11-SITE-002", "F11", "emplazamiento", "Subregiones, líneas de propiedad, contornos, región explanada y componentes de emplazamiento", "Site tools", "CREATE", "elementos de emplazamiento",
                    new[] { "Subregion", "PropertyLine", "PropertyLineData", "LabelContours", "GradedRegion", "SplitSurface", "MergeSurfaces", "SiteComponent", "ParkingComponent", "ToposolidByFace", "ToposolidSmoothShading", "TopographyCutVoidStability", "LinkTopography" },
                    "superficies topográficas (2022–2023) y Toposolid (2024+) tienen APIs distintas; cada herramienta requiere su verificación"),

                // ---------------- F12–F15 Estructura ----------------
                Dyn("F12-BEAMSYS-001", "F12", "estructura", "Sistemas de vigas y cerchas", "Beam systems and trusses", "CREATE", "sistema de vigas o cercha con su tipo",
                    new[] { DB + "BeamSystem::Create", DB + "Structure.Truss::Create" }, new[] { "AutomaticBeamSystem", "StructuralTrusses", "BeamSystemSymbol", "TopChord", "BottomChord", "Web" }),
                Unknown("F12-JOINS-001", "F12", "estructura", "Uniones viga/pilar y anotaciones de viga", "Beam/column joins, beam annotations", "UPDATE", "uniones ajustadas",
                    new[] { "BeamOrColumnJoins", "BeamAnnotations", "Brace", "StructuralPlan", "Slab", "MajorSegment", "Miter" }, "parte en API de StructuralFramingUtils; sin verificar por herramienta"),
                Dyn("F13-STEEL-CONN-001", "F13", "acero", "Conexiones estructurales", "Structural connections", "CREATE", "conexión entre los elementos pedidos con su tipo",
                    new[] { DB + "Structure.StructuralConnectionHandler::Create" }, new[] { "StructuralConnection", "ConnectionSettings" }, prio: "P2"),
                Unknown("F13-STEEL-FAB-002", "F13", "acero", "Placas, pernos, soldaduras, anclajes, conectores, despuntes y cortes", "Steel fabrication elements", "CREATE", "elementos de fabricación de acero",
                    new[] { "Plate", "Bolts", "Welds", "Anchors", "ShearStuds", "Cope", "CopeSkewed", "ApplyCoping", "RemoveCoping", "CornerCut", "SawCutFlange", "SawCutWeb", "Holes", "Shorten", "ContourCut", "CutThrough", "CutProfile" },
                    "API de Advance Steel (Autodesk.Advance.*) fuera de RevitAPI.dll; no inventariada aquí"),
                Dyn("F14-REBAR-SETS-001", "F14", "armadura", "Armadura por área, camino, malla y acopladores; recubrimientos", "Area/path/fabric reinforcement, couplers, cover", "CREATE", "armadura con su tipo y recubrimiento",
                    new[] { DB + "Structure.AreaReinforcement::Create", DB + "Structure.PathReinforcement::Create", DB + "Structure.FabricArea::Create", DB + "Structure.FabricSheet::Create", DB + "Structure.RebarCoupler::Create", DB + "Structure.RebarCoverType::Create" },
                    new[] { "StructuralAreaReinforcement", "StructuralPathReinforcement", "StructuralFabricArea", "SingleFabricSheetPlacement", "StructuralRebarCoupler", "InsertCoupler", "RebarCoverSettings", "EditRebarCover", "ReinforcementSettings", "ReinforcementNumbers", "AreaReinforcementSymbol", "PathReinforcementSymbol", "FabricReinforcementSymbol", "AllowableBarTypes", "RebarBendingDetail", "AlignedMultiRebarAnnotation", "LinearMultiRebarAnnotation", "LoadRebarShapeIntoProjectAndClose" }),
                Dyn("F15-LOADS-001", "F15", "cargas", "Casos, combinaciones y cargas estructurales; vínculos analíticos", "Load cases, combinations, loads, analytical links", "CREATE", "cargas y combinaciones con su naturaleza",
                    new[] { DB + "Structure.LoadCase::Create", DB + "Structure.LoadCombination::Create", DB + "Structure.PointLoad::Create", DB + "Structure.AnalyticalLink::Create" },
                    new[] { "LoadCases", "LoadCombinations", "LoadCasesAndCombinations", "Loads", "LoadCasesDisplay", "LoadsScalingDisplay", "AnalyticalLink", "CreateAnalyticalLink", "BoundaryConditions", "BoundaryConditionsSettings", "AnalyticalMember", "AdjustAnalyticalModel", "ResetAnalyticalModel", "AnalyticalAutomation", "RunAnalyticalToPhysicalForBuildings", "RunPhysicalToAnalyticalForBuildings" }),

                // ---------------- F17–F22 MEP ----------------
                Dyn("F17-FLEX-001", "F17", "conductos", "Conducto flexible y conversión a flexible", "Flex duct", "CREATE", "conducto flexible conectado", new[] { DB + "Mechanical.FlexDuct::Create" }, new[] { "FlexDuct", "ConvertToFlexDuct" }, prio: "P1"),
                Dyn("F17-PLACEHOLDER-001", "F17", "conductos", "Marcadores de conducto y tubería y su conversión", "Duct / pipe placeholders", "CREATE", "marcador creado o convertido en conducto/tubería",
                    new[] { DB + "Mechanical.Duct::CreatePlaceholder", DB + "Mechanical.MechanicalUtils::ConvertDuctPlaceholders", DB + "Plumbing.Pipe::CreatePlaceholder", DB + "Plumbing.PlumbingUtils::ConvertPipePlaceholders" }, new[] { "DuctPlaceholder", "PipePlaceholder" }, prio: "P1"),
                Dyn("F17-INSUL-001", "F17", "aislamiento", "Aislamiento de conductos y tuberías", "Duct / pipe insulation", "CREATE", "aislamiento con tipo y espesor",
                    new[] { DB + "Mechanical.DuctInsulation::Create", DB + "Plumbing.PipeInsulation::Create" }, new[] { "Insulation" }),
                Unknown("F17-FITTINGS-001", "F17", "accesorios", "Accesorios, uniones y conectores de conducto/tubería/bandeja/tubo", "Fittings, accessories, connectors", "CREATE", "accesorio conectado en el punto pedido",
                    new[] { "DuctFitting", "DuctAccessory", "DuctConnector", "PipeFitting", "PipeAccessory", "PipeConnector", "CableTrayFitting", "CableTrayConnector", "ConduitFitting", "ConduitConnector", "ElectricalConnector", "ParallelPipes", "ParallelConduits", "MultiPointRouting", "OneWayIndicator" },
                    "Document.NewElbowFitting/NewTeeFitting y conectores de familia; requieren contrato de conectividad (F16) antes de tiparlos"),
                Unknown("F17-REPORTS-001", "F17", "análisis", "Informes de pérdida de presión y ajustes mecánicos", "Pressure loss reports and mechanical settings", "VERIFY", "informe con las secciones del sistema",
                    new[] { "DuctPressureLossReport", "PipePressureLossReport", "MechanicalSettings", "SystemsAnalysis", "SystemZone", "Zone", "ZoneBySketch", "ZoneBySpace", "BuildingOperatingSchedules", "OperatingSchedules", "BuildingOrSpaceTypeSettings" },
                    "API de informes de presión no identificada; zonas y análisis de sistemas cambian entre 2022 y 2026"),
                Dyn("F18-FLEX-001", "F18", "tuberías", "Tubería flexible", "Flex pipe", "CREATE", "tubería flexible conectada", new[] { DB + "Plumbing.FlexPipe::Create" }, new[] { "FlexPipe" }),
                Dyn("F20-SETTINGS-001", "F20", "electricidad", "Ajustes eléctricos, factores de demanda, clasificaciones de carga y cables", "Electrical settings", "UPDATE", "ajustes eléctricos según lo pedido",
                    new[] { DB + "Electrical.ElectricalSetting" }, new[] { "ElectricalSettings", "DemandFactors", "LoadClassifications", "ElectricalConductorAndCableSettings", "ElectricalAnalyticalLoadTypeSettings" }, prio: "P1"),
                Unknown("F20-ANALYTICAL-001", "F20", "electricidad", "Cargas de equipo y cargas por área (análisis eléctrico)", "Equipment and area-based loads", "CREATE", "carga analítica con su clasificación",
                    new[] { "EquipmentLoad", "CreateAnAreaBasedLoad", "AreaBasedLoadBoundary" }, "API de cargas analíticas eléctricas introducida en versiones recientes; verificar por versión"),
                Dyn("F22-FAB-PART-001", "F22", "fabricación", "Piezas de fabricación MEP y ajustes", "Fabrication parts", "CREATE", "pieza de fabricación con servicio y botón",
                    new[] { DB + "FabricationPart::Create" }, new[] { "FabricationPart", "FabricationSettings", "MEPFabricationDuctworkStiffener" }),
                Unknown("F22-PID-001", "F22", "P&ID", "P&ID Modeler", "P&ID Modeler", "CREATE", "modelo desde P&ID", new[] { "PAndIDModeler", "PAndIDSettings" }, "complemento de Autodesk con su propia API; no inventariado"),

                // ---------------- F23 Editor de familias ----------------
                Dyn("F23-FAM-GEOM-001", "F23", "geometría de familia", "Sólidos y vacíos: extrusión, fundido, revolución, barrido y barrido fundido", "Family solids and voids", "CREATE", "forma con su boceto y plano",
                    new[] { "Autodesk.Revit.Creation.FamilyItemFactory::NewExtrusion", "Autodesk.Revit.Creation.FamilyItemFactory::NewBlend", "Autodesk.Revit.Creation.FamilyItemFactory::NewRevolution", "Autodesk.Revit.Creation.FamilyItemFactory::NewSweep", "Autodesk.Revit.Creation.FamilyItemFactory::NewSweptBlend" },
                    new[] { "SolidExtrusion", "SolidBlend", "SolidRevolve", "SolidSweep", "SolidSweptBlend", "VoidExtrusion", "VoidBlend", "VoidRevolve", "VoidSweep", "VoidSweptBlend", "ReferenceLine", "Label" }, prio: "P1"),
                Dyn("F23-FAM-TYPES-001", "F23", "tipos de familia", "Tipos de familia", "Family types", "CREATE", "tipo nuevo con sus valores", new[] { DB + "FamilyManager::NewType" }, new[] { "FamilyTypes" }, prio: "P1"),
                Dyn("F23-FAM-NEW-001", "F23", "familias", "Nueva familia, cajetín o símbolo; guardar como familia de biblioteca", "New family / save as library", "CREATE", "archivo de familia nuevo",
                    new[] { DB + "Document::SaveAs" }, new[] { "NewFamily", "NewTitleBlock", "NewAnnotationSymbol", "SaveAsFamily", "SaveAsLibraryFamily", "SaveAsLibraryView", "SaveAsLibraryGroup", "LoadIntoProject", "LoadFamilyIntoProjectAndClose" }),
                Unknown("F23-FAM-CLOUD-001", "F23", "familias", "Cargar familia de Autodesk (contenido en la nube)", "Load Autodesk family", "CREATE", "familia descargada y cargada", new[] { "LoadAutodeskFamily" }, "servicio en la nube sin API pública conocida"),

                // ---------------- F24 Vistas ----------------
                Dyn("F24-VIEW-FILTER-001", "F24", "filtros", "Filtros de vista y sustitución por filtro", "View filters", "UPDATE", "filtro creado y aplicado con sus gráficos",
                    new[] { DB + "ParameterFilterElement::Create", DB + "View::SetFilterOverrides" }, new[] { "OverrideByFilter" }, prio: "P1"),
                Dyn("F24-VIEW-TEMPLATE-001", "F24", "plantillas de vista", "Crear plantilla desde la vista y aplicar sus parámetros", "View templates", "CREATE", "plantilla creada o aplicada",
                    new[] { DB + "View::CreateViewTemplate", DB + "View::ApplyViewTemplateParameters" }, new[] { "CreateTemplateFromCurrentView" }),
                Dyn("F24-VIEW-CAMERA-001", "F24", "vistas", "Cámara (vista 3D en perspectiva) y planta estructural", "Camera / structural plan", "CREATE", "vista con orientación y tipo pedidos",
                    new[] { DB + "View3D::CreatePerspective", DB + "ViewPlan::Create" }, new[] { "Camera" }),
                Unknown("F24-VIEW-MISC-001", "F24", "vistas", "Región de plano, líneas ocultas por elemento, recorrido y etiquetas de vista", "Plan region, hidden lines, walkthrough, view tags", "UPDATE", "vista con la configuración pedida",
                    new[] { "PlanRegion", "ShowHiddenLinesByElement", "RemoveHiddenLinesByElement", "Walkthrough", "SectionTags", "ElevationTags", "CalloutTags", "Linework", "HalftoneOrUnderlay", "ShowBoundaryOpenEnds", "HideBoundaryOpenEnds", "Isolated", "Multiplanar", "SharedViews" },
                    "cada herramienta tiene una API distinta o ninguna; sin verificar"),
                NotApplicable("F24-UI-001", "F24", "interfaz de vistas", "Navegación, ventanas, ViewCube y modos de visualización de la interfaz", "View UI",
                    new[] { "ViewCube", "NavigationBar", "TabViews", "TileViews", "CloseInactiveViews", "ThinLines", "ToggleRevealHiddenElementsMode", "ActivateView", "DeactivateView", "StartingView" },
                    "estado de la interfaz o de la ventana; no cambia datos del modelo que BIMBRIDGE deba planificar"),

                // ---------------- F25 Anotación ----------------
                Unknown("F25-DIM-002", "F25", "cotas", "Cotas angulares, radiales, de diámetro y de longitud de arco; cotas temporales", "Angular / radial / diameter / arc length dimensions", "CREATE", "cota con las referencias pedidas",
                    new[] { "AngularDimension", "RadialDimension", "DiameterDimension", "ArcLengthDimension", "AngularDimensionTypes", "RadialDimensionTypes", "DiameterDimensionTypes", "LinearDimensionTypes", "AlignmentStation", "AlignmentStationLabelTypes", "AlignmentStationSet" },
                    "NewDimension sólo crea cotas lineales; las demás necesitan API específica por versión"),
                Dyn("F25-SPOT-001", "F25", "cotas de punto", "Cotas y coordenadas de punto y pendiente", "Spot elevation / coordinate / slope", "CREATE", "cota de punto sobre la referencia pedida",
                    new[] { "Autodesk.Revit.Creation.Document::NewSpotElevation", "Autodesk.Revit.Creation.Document::NewSpotCoordinate" }, new[] { "SpotElevation", "SpotCoordinate", "SpotSlope", "SpotElevationTypes", "SpotCoordinateTypes", "SpotSlopeTypes" }),
                Unknown("F25-KEYNOTE-001", "F25", "notas clave", "Notas clave de elemento, material y usuario; leyenda de notas clave", "Keynotes", "TAG", "nota clave colocada con su valor",
                    new[] { "ElementKeynote", "MaterialKeynote", "UserKeynote", "KeynoteLegend", "MaterialTag", "MultiCategoryTag", "LoadedTagsAndSymbols", "SpanDirectionSymbol", "MultipleValuesIndication", "MeasureAlongAnElement", "MeasureBetweenTwoReferences" },
                    "IndependentTag cubre algunas; verificar tipos de etiqueta por categoría"),
                WithLimits(Typed("F25-TAG-ALL-001", "F25", "etiquetas", "Etiquetar todo lo no etiquetado (por vista)", "Tag all not tagged", "TAG", "tag", "un tag por elemento sin etiquetar en la vista", plan, new[] { "TagAllNotTagged" }),
                    "composición: query de lo no etiquetado + tag; no etiqueta recintos. Efecto en Revit pendiente de la campaña nativa"),
                WithLimits(Typed("F25-SYMBOL-001", "F25", "símbolos", "Colocar símbolo de anotación", "Place annotation symbol", "CREATE", "detail_component", "símbolo (familia basada en vista) en la vista", new[] { "Draw2DTests", "PlanTests" }, new[] { "Symbol" }),
                    "familias basadas en vista; sin directrices. Efecto en Revit pendiente de la campaña nativa"),
                Unknown("F25-DETAIL-002", "F25", "detalle", "Región de máscara, componente de detalle repetido, aislamiento de detalle, leyendas y componentes de leyenda", "Masking region, repeating detail, legends", "CREATE", "elemento de detalle en la vista",
                    new[] { "MaskingRegion", "RepeatingDetailComponent", "Legend", "LegendComponent", "Matchline" }, "región de máscara = FilledRegion con tipo de máscara (no verificado); leyendas sin API de creación directa"),

                // ---------------- F26–F27 Tablas, láminas, impresión ----------------
                Dyn("F26-SCHED-SPECIAL-001", "F26", "tablas", "Listas de láminas, de vistas, bloques de notas, tablas de revisiones, cómputo de materiales y tablas clave", "Special schedules", "SCHEDULE", "tabla especial con sus campos",
                    new[] { DB + "ViewSchedule::CreateSheetList", DB + "ViewSchedule::CreateViewList", DB + "ViewSchedule::CreateNoteBlock", DB + "ViewSchedule::CreateRevisionSchedule", DB + "ViewSchedule::CreateMaterialTakeoff", DB + "ViewSchedule::CreateKeySchedule" },
                    new[] { "SheetList", "ViewList", "NoteBlock", "RevisionSchedule", "MaterialTakeoff" }, "schedule_create tipado sólo crea tablas de categoría", "P1"),
                Unknown("F26-SCHED-GRAPH-001", "F26", "tablas", "Tabla gráfica de pilares", "Graphical column schedule", "SCHEDULE", "tabla gráfica", new[] { "GraphicalColumnSchedule" }, "sin API de creación identificada"),
                WithLimits(Typed("F27-SHEET-VIEW-001", "F27", "láminas", "Colocar vista en lámina", "Place view", "CREATE", "sheet_place", "viewport en la lámina", plan, new[] { "PlaceView" }), "efecto en Revit pendiente de la campaña nativa"),
                Dyn("F27-SHEET-PH-001", "F27", "láminas", "Láminas de marcador de posición y cajetín", "Placeholder sheets, title block", "CREATE", "lámina de marcador o cajetín colocado",
                    new[] { DB + "ViewSheet::CreatePlaceholder" }, new[] { "TitleBlock", "GuideGrid", "ViewReference" }),
                Dyn("F27-PRINT-001", "F27", "impresión", "Imprimir, configurar impresión y conjuntos", "Print / print setup", "EXPORT", "trabajo de impresión con la configuración pedida",
                    new[] { DB + "Document::Print", DB + "PrintManager", DB + "ViewSheetSet" }, new[] { "Print", "PrintSetup", "PrintPreview", "BatchPrint" }, "impresora del sistema: efecto externo sin rollback; doc pdf cubre PDF", "P2"),

                // ---------------- F28 Vínculos ----------------
                Dyn("F28-LINK-IFC-001", "F28", "vínculos", "Vincular y abrir IFC", "Link / open IFC", "CREATE", "vínculo IFC con su transformación",
                    new[] { DB + "RevitLinkType::CreateFromIFC" }, new[] { "LinkIFC", "OpenIFC", "OpenIFCOptions" }, prio: "P1"),
                Dyn("F28-IMAGE-PDF-001", "F28", "vínculos", "Vincular o importar imagen y PDF; gestionar imágenes", "Link / import image and PDF", "CREATE", "imagen o página PDF colocada en la vista",
                    new[] { DB + "ImageType::Create" }, new[] { "LinkImage", "ImportImage", "ManageImages", "LinkPDF", "ImportPDF" }, "PDF como ImageType según opciones por versión; sin verificar", "P1"),
                Dyn("F28-POINTCLOUD-001", "F28", "vínculos", "Nubes de puntos", "Point clouds", "CREATE", "instancia de nube de puntos", new[] { DB + "PointCloudInstance" }, new[] { "PointCloud" }),
                Unknown("F28-MANAGE-001", "F28", "vínculos", "Gestionar vínculos, modelos de coordinación, FormIt e insertar desde archivo", "Manage links, coordination models, insert from file", "UPDATE", "vínculos recargados o descargados",
                    new[] { "ManageLinks", "CoordinationModel", "CoordinationModelAutodeskDocs", "CoordinationModelChanges", "CoordinationModelLocal", "LinkFormit", "Insert2DElementsFromFile", "InsertViewsFromFile", "CreateFromImport", "ImportTypes", "ImportGBXML", "ManageConnectionToARevitServerAccelerator", "DecalTypes", "PlaceDecal" },
                    "RevitLinkType.Reload/Unload existen; modelos de coordinación y FormIt sin verificar"),

                // ---------------- F30–F31 Colaboración y organización ----------------
                Dyn("F30-OWN-001", "F30", "worksharing", "Liberar lo mío, recargar lo último y habilitar colaboración", "Relinquish / reload latest / enable worksharing", "SYNC", "propiedad liberada o modelo recargado",
                    new[] { DB + "WorksharingUtils::RelinquishOwnership", DB + "Document::ReloadLatest", DB + "Document::EnableWorksharing" }, new[] { "RelinquishAllMine", "ReloadLatest", "Collaborate" }, prio: "P1"),
                Unknown("F30-CLOUD-001", "F30", "worksharing", "Colaborar en la nube, solicitudes de edición, historial y publicación", "Cloud collaboration, editing requests, history", "SYNC", "estado de colaboración",
                    new[] { "CollaborateInCloud", "EditingRequests", "ShowHistory", "PublishSettings" }, "sin API pública identificada para estos diálogos"),
                Dyn("F31-PARTS-001", "F31", "piezas", "Crear piezas", "Create parts", "CREATE", "piezas de los elementos pedidos", new[] { DB + "PartUtils::CreateParts" }, new[] { "CreateParts" }),
                WithLimits(Typed("F31-DEMOLISH-001", "F31", "fases", "Derribar (fase de derribo)", "Demolish", "UPDATE", "set_param", "elementos con la fase de derribo pedida", plan, new[] { "Demolish" }),
                    "set_param sobre el parámetro de fase de derribo; la herramienta de la cinta usa la fase de la vista. Efecto en Revit pendiente de la campaña nativa"),
                WithLimits(Typed("F31-GROUP-DETAIL-001", "F31", "grupos", "Colocar grupo de detalle", "Place detail group", "CREATE", "group_place", "grupo colocado", plan, new[] { "PlaceDetailGroup" }), "efecto en Revit pendiente de la campaña nativa"),
                Unknown("F31-GROUP-LOAD-001", "F31", "grupos", "Cargar grupo en proyectos abiertos y asignar código de ensamblaje", "Load group into projects, assembly code", "CREATE", "grupo cargado en los proyectos pedidos",
                    new[] { "LoadAsGroupIntoOpenProjects", "AssemblyCode" }, "sin API equivalente verificada"),

                // ---------------- F32–F34 Análisis, visualización y exportación ----------------
                Dyn("F32-ENERGY-002", "F32", "análisis energético", "Crear y borrar modelo energético; generar análisis", "Energy model", "CREATE", "modelo de análisis energético",
                    new[] { DB + "Analysis.EnergyAnalysisDetailModel::Create" }, new[] { "CreateEnergyModel", "DeleteEnergyModel", "Generate", "GenerateAnalysis", "Optimize", "ViewAnalysis", "Lighting" }),
                Dyn("F32-PATH-001", "F32", "análisis espacial", "Recorrido de evacuación", "Path of travel", "CREATE", "recorrido entre dos puntos con su longitud", new[] { DB + "Analysis.PathOfTravel::Create" }, new[] { "PathOfTravel", "RevealObstacles", "SpatialGrid", "PeopleContent" }),
                Unknown("F33-RENDER-002", "F33", "visualización", "Renderizar, galería, nube y ajustes de sol", "Render and sun settings", "EXPORT", "imagen renderizada", new[] { "Render", "RenderInCloud", "RenderGallery", "SunSettings", "AnalysisDisplayStyles" },
                    "motor de render sin API pública de lanzamiento; SunAndShadowSettings sí existe para el sol"),
                Dyn("F34-EXPORT-OTHER-001", "F34", "exportación", "Exportar FBX, DWF/DWFx, DGN, STL, OBJ, SAT y gbXML", "Export FBX/DWF/DGN/STL/OBJ/SAT/gbXML", "EXPORT", "archivo del formato pedido",
                    new[] { DB + "FBXExportOptions", DB + "DWFExportOptions", DB + "DGNExportOptions", DB + "STLExportOptions", DB + "OBJExportOptions", DB + "SATExportOptions", DB + "GBXMLExportOptions" },
                    new[] { "ExportFBX", "ExportDWFOrDWFx", "ExportCADFormatsDGN", "ExportCADFormatsSTL", "ExportCADFormatsOBJ", "ExportCADFormatsACIS_SAT", "ExportGBXML", "ExportOptionsExportSetupsDGN" }, prio: "P2"),
                Dyn("F34-EXPORT-STEP-001", "F34", "exportación", "Exportar STEP (2025+)", "Export STEP", "EXPORT", "archivo STEP", new[] { DB + "STEPExportOptions" }, new[] { "ExportCADFormatsSTEP" }, "sólo Revit 2025–2026 (inventario)"),
                Unknown("F34-EXPORT-REPORTS-001", "F34", "exportación", "Informes de habitación/área, ODBC, animaciones y tipos", "Room/area report, ODBC, animations, export types", "EXPORT", "archivo del informe",
                    new[] { "ExportReportsRoomOrAreaReport", "ExportODBCDatabase", "ExportImagesandAnimationsSolarStudy", "ExportImagesandAnimationsWalkthrough", "ExportExportTypes", "DWFMarkup" }, "sin API pública identificada para estos asistentes"),

                // ---------------- F35 Automatización (otras herramientas) ----------------
                NotApplicable("F35-OTHER-TOOLS-001", "F35", "automatización", "Dynamo, macros, gestor de complementos y personalización", "Dynamo, macros, add-ins",
                    new[] { "Dynamo", "DynamoPlayer", "DynamoPlayerForSteel", "MacroManager", "MacroSecurity", "AddInsManager", "AutomationCustomization", "KeyboardShortcuts", "CanvasTheme" },
                    "otras herramientas de automatización o personalización de la interfaz; BIMBRIDGE tiene su propio motor (exec, tareas, planes)"),

                // ---------------- F36 QA/QC ----------------
                new CapabilityEntry
                {
                    Id = "F36-HEALTH-001", Domain = "F36", Subdomain = "model health", Name = "Salud del modelo: avisos, duplicados, parámetros vacíos y conectores MEP desconectados", NameEn = "Model health",
                    Operation = "VERIFY", Expected = "informe con avisos por tipo, duplicados en la misma posición, parámetros requeridos vacíos y conectores sin conectar, con ids",
                    Strategy = "query:check model_health", Status = CoverageStatus.IMPLEMENTED_UNVERIFIED,
                    Implementation = new[] { "src/PDI.RevitMcp.Engine/Qa/ModelHealth.cs", "src/PDI.RevitMcp.Addin/Revit/ModelHealthProbe.cs" }, Tests = new[] { "ModelHealthTests" },
                    PostableCommands = new[] { "ReviewWarnings", "ShowWarningsInViews", "ShowDisconnects", "CheckDuctSystems", "CheckPipeSystems", "AnalyticalConsistencyChecks", "CheckMemberSupports" },
                    Verifier = "reglas offline sobre el inventario leído (ModelHealthTests); lectura en Revit pendiente de campaña",
                    Limitations = "sólo lectura; las comprobaciones estructurales de Revit (apoyos, consistencia analítica) no se reproducen. Lectura en Revit pendiente de la campaña nativa", Priority = "P1",
                },
                Unknown("F37-CONTEXT-001", "F37", "modos contextuales", "Modos de edición y boceto contextuales (editar boceto, centrar referencia, estado de forma, rutas, sustituciones, reconciliar alojamiento)", "Contextual edit/sketch modes", "UPDATE",
                    "elemento editado en su modo contextual con verificación", new[] { "PickToEdit", "ReCenterReference", "ShapeStatus", "MultiplePaths", "SubstitutionSettings", "ReconcileHosting" },
                    "dependen del modo de edición activo de la interfaz; sin API pública equivalente identificada para cada uno"),
                Unknown("F02-EDIT-SCALE-001", "F02", "edición", "Escalar elementos", "Scale", "UPDATE", "elementos escalados respecto a un punto", new[] { "Scale" },
                    "Revit sólo escala ciertos elementos 2D, imágenes y vínculos; sin método genérico en la API"),
                Unknown("F25-TEXT-REPLACE-001", "F25", "texto", "Buscar y reemplazar texto", "Find / replace text", "UPDATE", "notas de texto con el texto reemplazado", new[] { "FindOrReplace" },
                    "factible con TextNote.Text + set_param, pero sin contrato ni verificador todavía"),
                Unknown("F15-SETTINGS-001", "F15", "estructura", "Ajustes estructurales", "Structural settings", "UPDATE", "ajustes estructurales según lo pedido", new[] { "StructuralSettings" },
                    "clase de ajustes estructurales a localizar en el listado por versión"),
                Engine("F35-HISTORY-001", "F35", "operaciones largas", "Historial de operaciones con filtros, cancelación y consejo de reintento seguro", "Operation history and safe retry", "QUERY",
                    "cada trabajo terminado queda persistido con estado, duración y si es reintentable o exige reconciliar; en curso con progreso",
                    new[] { "src/PDI.RevitMcp.Engine/OperationHistory.cs", "src/PDI.RevitMcp.Engine/Jobs.cs" }, new[] { "OperationHistoryTests" },
                    "historial y JobStore con trabajos sintéticos", "consulta por check name=historial; la vista en el panel de estado (#5) queda pendiente"),
                NotApplicable("F37-UI-002", "F37", "interfaz", "Paletas, barras de estado, temas y controles de la interfaz", "UI palettes and bars",
                    new[] { "ProjectBrowser", "SystemBrowser", "StatusBar", "StatusBarDesignOptions", "StatusBarWorksets", "TogglePropertiesPalette", "ToggleHome", "TogglePAndIDModelerBrowser", "LaunchAsApp", "LaunchInBrowser", "LaunchInDockedWindow", "Snaps", "TemporaryDimensions", "ShowWorksharingMakeEditableControls", "GrayInactiveWorksets", "WorksharingMonitor", "ShowLastReport", "CheckSpelling", "BrowserOrganization" },
                    "estado de la interfaz de Revit; no son operaciones del modelo"),
            });

            // Comandos de la cinta que corresponden a capacidades ya existentes (misma función; diferencias en sus limitaciones).
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
                if (e == null) throw new System.InvalidOperationException("Expand: no existe " + kv.Key);
                e.PostableCommands = e.PostableCommands.Concat(kv.Value).Distinct().ToArray();
            }
        }
    }
}
