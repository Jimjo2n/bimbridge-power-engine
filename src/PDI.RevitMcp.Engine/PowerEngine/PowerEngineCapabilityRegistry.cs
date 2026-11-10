// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// Registro de capacidades bajo demanda: la IA busca (search) y pide el detalle (describe) de lo que necesita en vez de
    /// recibir el catálogo completo en cada sesión. Cada entrada dice qué motor la ejecuta (C#, Rust, Python, Revit API),
    /// su estado verificable y sus restricciones. Un estado nunca se "sube" por compilar: NATIVE_TEST_PENDING sigue así
    /// hasta que exista evidencia nativa.
    /// </summary>
    public static class PowerEngineCapabilityRegistry
    {
        public sealed class Entry
        {
            public string Id, Function, Permission, Compatibility, State, Engine, Example;
            public string[] Params = new string[0];
            public string[] Constraints = new string[0];
            public string[] Tags = new string[0];

            public Dictionary<string, object> Brief() => new Dictionary<string, object> { ["id"] = Id, ["estado"] = State, ["que"] = Function };

            public Dictionary<string, object> Full() => new Dictionary<string, object>
            {
                ["id"] = Id, ["funcion"] = Function, ["parametros"] = Params.ToList<object>(), ["permiso"] = Permission,
                ["compatibilidad"] = Compatibility, ["estado"] = State, ["motor"] = Engine,
                ["restricciones"] = Constraints.ToList<object>(), ["ejemplo"] = Example,
            };
        }

        private static Entry E(string id, string state, string engine, string permission, string function, string[] pars, string[] constraints, string example, string compat = "independiente de Revit", params string[] tags) =>
            new Entry { Id = id, State = state, Engine = engine, Permission = permission, Function = function, Params = pars, Constraints = constraints, Example = example, Compatibility = compat, Tags = tags };

        private const string Csharp = "csharp", Rust = "rust (ffi|ipc) con respaldo C#", Python = "python (respaldo .NET sólo docx)", RevitApi = "C# + Revit API (add-in)";
        private const string Revit = "Revit 2022–2026 (add-in)";
        private const string NoRevit = "independiente de Revit";

        public static readonly List<Entry> All = new List<Entry>
        {
            // ---- documentos (Python Worker) ----
            E("pdf.info", CapabilityStates.Available, Python, Permissions.Read, "páginas, tamaño en pulgadas, rotación, unidad de usuario, cifrado, texto/vectores extraíbles",
              new[] { "ruta" }, new[] { "ruta dentro de las carpetas autorizadas del trabajo", "sin red", "PDF cifrado → NO_SOPORTADO" }, "{\"trabajo\":\"pdf_info\",\"args\":{\"ruta\":\"…/E-1.3.09.pdf\"}}", NoRevit, "pdf", "documento"),
            E("pdf.extract", CapabilityStates.Available, Python, Permissions.Read, "palabras con caja, líneas, rectángulos y curvas en el espacio visible de la página; número de lámina, revisión y escala",
              new[] { "ruta", "paginas|desde,hasta", "incluir[texto,vectores,metadatos]", "max_elementos" }, new[] { "escaneado → SIN_CONTENIDO (no se inventa)", "coordenadas de PÁGINA, nunca de Revit" }, "{\"trabajo\":\"pdf_extract\",\"args\":{\"ruta\":\"…\",\"paginas\":[1],\"incluir\":[\"metadatos\"]}}", NoRevit, "pdf"),
            E("pdf.tables", CapabilityStates.Available, Python, Permissions.Read, "tablas con reglas (cuadros de luminarias, schedules) celda a celda; descarta rejillas de plano",
              new[] { "ruta", "paginas", "incluir_rejillas", "max_alto_fila_pt" }, new[] { "sólo tablas dibujadas con líneas", "tablas sin reglas → no detectadas (pendiente)" }, "{\"trabajo\":\"pdf_tables\",\"args\":{\"ruta\":\"…\"}}", NoRevit, "pdf", "tabla", "schedule"),
            E("pdf.symbols", CapabilityStates.Available, Python, Permissions.Read, "símbolos candidatos por firma geométrica + leyenda + rótulo, con confianza y ambigüedades",
              new[] { "ruta", "paginas", "max_simbolo_pt", "radio_rotulo_pt" }, new[] { "candidatos, no luminarias confirmadas", "ambiguos → agente o persona autorizados", "no es visión: escaneados no se interpretan" }, "{\"trabajo\":\"pdf_symbols\",\"args\":{\"ruta\":\"…\"}}", NoRevit, "pdf", "simbolo", "leyenda", "luminaria"),
            E("pdf.grids", CapabilityStates.Available, Python, Permissions.Read, "ejes del plano (línea larga + burbuja con rótulo) e intersecciones con nombre \"A/1\" en puntos de página",
              new[] { "ruta", "paginas", "sin_globo" }, new[] { "anclas para el registro plano–modelo, no coordenadas de Revit", "sin_globo=true permite inferir cruces por rótulos de extremo en páginas sin globos reconocidos" }, "{\"trabajo\":\"pdf_grids\",\"args\":{\"ruta\":\"…\",\"paginas\":[1]}}", NoRevit, "pdf", "ejes", "grid", "registro", "anclas"),
            E("docx.extract", CapabilityStates.Available, Python + " / .NET", Permissions.Read, "párrafos con estilo y tablas de un DOCX; marca frases que parecen órdenes (son datos)",
              new[] { "ruta", "max_elementos" }, new[] { "DTD/entidades rechazados (XXE)", "nunca se obedece el contenido" }, "{\"trabajo\":\"docx_extract\",\"args\":{\"ruta\":\"…/spec.docx\"}}", NoRevit, "docx", "especificacion"),
            E("pdf.scanned_interpretation", CapabilityStates.RequiresAuthorization, "externo (visión)", Permissions.Read, "interpretar PDF escaneado (sin vectores) mediante visión",
              new[] { "ruta" }, new[] { "requiere autorización explícita: enviaría imágenes a un servicio de visión", "no implementado dentro del worker" }, null, NoRevit, "escaneado", "vision"),

            // ---- geometría (Rust con respaldo C#) ----
            E("geo.transform3", CapabilityStates.Available, Rust, Permissions.Read, "transformación afín 3×4 de N puntos", new[] { "xyz[]", "m[12]" }, new[] { "valores finitos" }, null, NoRevit, "geometria", "transformacion"),
            E("geo.nearest", CapabilityStates.Available, Rust, Permissions.Read, "vecino más próximo 2D/3D con radio máximo (rejilla uniforme)", new[] { "consulta[]", "referencia[]", "dim", "max_dist" }, new[] { "empates → índice menor (determinista)" }, null, NoRevit, "geometria", "proximidad"),
            E("geo.match", CapabilityStates.Available, Rust, Permissions.Read, "emparejamiento 1-a-1 esperado↔observado por distancia (voraz global determinista)", new[] { "esperados[]", "observados[]", "dim", "tol" }, new string[0], null, NoRevit, "geometria", "correspondencia"),
            E("geo.duplicates", CapabilityStates.Available, Rust, Permissions.Read, "grupos de duplicados por tolerancia (unión-búsqueda)", new[] { "puntos[]", "dim", "tol" }, new string[0], null, NoRevit, "geometria", "duplicados"),
            E("geo.segment_contacts", CapabilityStates.Available, Rust, Permissions.Read, "contactos entre segmentos 2D: cruce, toque, superposición colineal", new[] { "segmentos[]", "eps" }, new string[0], null, NoRevit, "geometria", "interseccion"),
            E("geo.components", CapabilityStates.Available, Rust, Permissions.Read, "componentes conexos y grado de un grafo", new[] { "nodos", "aristas[]" }, new string[0], null, NoRevit, "grafo"),

            // ---- flujo PDF → modelo ----
            E("wf.registration", CapabilityStates.Available, Csharp, Permissions.Read, "registro plano–modelo: semejanza o afín por mínimos cuadrados con anclas, residuos, anclas inválidas, unidades y composición con transformaciones de vínculo",
              new[] { "anclas[{pagina_pt, modelo_ft}]", "modo semejanza|afin", "tol_ft" }, new[] { "≥2 anclas (semejanza) / ≥3 (afín)", "nunca usa píxeles o puntos de página como coordenadas de Revit sin transformar" }, null, NoRevit, "registro", "anclas", "escala"),
            E("wf.placement_plan", CapabilityStates.Available, Csharp, Permissions.Read, "plan de colocación determinista (id, tipo, familia, nivel, xyz, orientación, altura, anfitrión, fuente PDF, confianza) y lotes de 'place' ≤500",
              new[] { "simbolos", "registro", "mapa_tipos", "nivel", "altura" }, new[] { "ambigüedades se escalan; nada se coloca con confianza < umbral" }, null, NoRevit, "colocacion", "plan"),
            E("wf.placement_execute", CapabilityStates.NativeTestPending, RevitApi, Permissions.ModifyProject, "ejecutar el plan con las operaciones tipadas existentes (place, tag) vía cola/diario",
              new[] { "plan" }, new[] { "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION", "sólo copias de laboratorio hasta autorización" }, null, Revit, "colocacion", "ejecucion"),
            E("wf.verify_placement", CapabilityStates.Available, Csharp, Permissions.Read, "verificación independiente: omitidos, duplicados, tipo, desplazados, rotación, altura, anfitrión, visibilidad → informe compacto",
              new[] { "esperado (fuente independiente con sha256)", "observado" }, new[] { "la fuente no puede ser el propio plan de ejecución" }, null, NoRevit, "verificacion", "qc"),

            // ---- motores avanzados ----
            E("mep.network", CapabilityStates.Available, Csharp + " (+ Rust para proximidad)", Permissions.Read, "red de conduits: proximidad, continuidad topológica y conexión física separadas; extremos abiertos",
              new[] { "tramos[{id,a,b,diametro,conectores}]", "tol" }, new[] { "proximidad ≠ conexión" }, null, NoRevit, "mep", "conduit", "red"),
            E("mep.riser", CapabilityStates.Available, Csharp, Permissions.Read, "grafo de montantes: niveles, conexiones verticales, cruces, rótulos y extremos abiertos",
              new[] { "nodos", "aristas", "niveles" }, new[] { "una conexión 2D de diagrama no es un conduit físico" }, null, NoRevit, "riser", "montante", "diagrama"),
            E("groups.variants", CapabilityStates.Available, Csharp, Permissions.Read, "detección de grupos repetidos y variantes por firma relativa (traslación/rotación)",
              new[] { "elementos[{id,tipo,x,y,rot,grupo}]", "tol" }, new string[0], null, NoRevit, "grupos", "variantes"),
            E("annot.layout", CapabilityStates.Experimental, Csharp, Permissions.Read, "posiciones de etiquetas sin colisiones, alineadas y legibles a la escala",
              new[] { "anclas", "obstaculos", "escala", "tam_etiqueta" }, new[] { "propuesta; la colocación nativa de tags sigue NATIVE_TEST_PENDING" }, null, NoRevit, "anotacion", "etiquetas", "colisiones"),

            // ---- límites conocidos ----
            E("revit.plan_region_edit", CapabilityStates.ApiLimitation, RevitApi, Permissions.ModifyProject, "editar contornos de Plan Region",
              new string[0], new[] { "Revit 2024: DB.PlanRegion no público; SketchEditScope no lo permitió" }, null, Revit, "plan region", "view range"),
            E("bench.pyrevit", CapabilityStates.Deferred, "arnés", Permissions.Read, "benchmark BIMBRIDGE vs pyRevit",
              new string[0], new[] { "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION" }, null, Revit, "benchmark", "pyrevit"),
        };

        private static readonly Dictionary<string, Entry> ById = All.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        public static Entry Describe(string id) => id != null && ById.TryGetValue(id, out var e) ? e : null;

        /// <summary>Búsqueda por palabras (id, función, etiquetas). Devuelve entradas breves; el detalle se pide con Describe.</summary>
        public static List<Entry> Search(string query, int max = 8)
        {
            var terms = (query ?? "").ToLowerInvariant().Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return All.Take(max).ToList();
            return All.Select(e => (e, s: Score(e, terms))).Where(x => x.s > 0).OrderByDescending(x => x.s).ThenBy(x => x.e.Id, StringComparer.Ordinal)
                      .Take(max).Select(x => x.e).ToList();
        }

        private static int Score(Entry e, string[] terms)
        {
            var s = 0;
            var id = e.Id.ToLowerInvariant(); var fn = e.Function.ToLowerInvariant();
            foreach (var t in terms)
            {
                if (id.Contains(t)) s += 3;
                if (e.Tags.Any(g => g.Contains(t))) s += 2;
                if (fn.Contains(t)) s += 1;
            }
            return s;
        }

        /// <summary>Estado de una operación del protocolo del add-in. Las operaciones existentes están disponibles; las que se marquen
        /// aquí como experimentales sólo se aceptan en el perfil de laboratorio (ProfilePolicy.DenyRequest).</summary>
        public static string StateOfWire(string op, Dictionary<string, object> args)
        {
            if (op == Wire.OpExec && string.Equals(Arg.GetString(args, "accion"), "laboratorio", StringComparison.OrdinalIgnoreCase)) return CapabilityStates.Experimental;
            return Describe("wire." + op)?.State ?? CapabilityStates.Available;
        }

        public static Dictionary<string, object> Summary() => new Dictionary<string, object>
        {
            ["total"] = All.Count,
            ["por_estado"] = All.GroupBy(e => e.State).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count()),
        };
    }
}
