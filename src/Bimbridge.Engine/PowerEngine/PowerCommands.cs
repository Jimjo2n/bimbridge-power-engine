// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using BimBridge.PowerEngine.Workflows;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// One-line commands of the compact "power" tool (Bridge). They do not touch Revit: authorized documents, geometry and
    /// planning. Whatever changes the model comes out as "ops" for plan→commit (the usual protections).
    ///   cap [text|id]          on-demand discovery (brief; with an exact id, full card)
    ///   estado                 engines, versions, calibration, authorized folders and power_engine.* metrics
    ///   pdf info|extract|tables|symbols|grids|verify ruta= [paginas=[1]] [max_elementos=]
    ///   docx ruta= [max=]
    ///   flujo ruta= pagina= nivel= reglas=[…] ejes=[…] [existentes=[…]] [tol=0.25] [conf=0.9] [modo=semejanza|afin]
    /// </summary>
    public static class PowerCommands
    {
        public const string Help =
            "power (does not change the model): cap [text|id] | estado | pdf info|extract|tables|symbols|grids ruta= paginas=[1] | pdf verify ruta= esperado=[{textos,papel_pt,rgb_vectorial}] [anterior=] [sha256_esperado=] | docx ruta= | " +
            "flujo ruta= pagina= nivel= reglas=[{codigo,tipo,montaje|elev,ws}] ejes=[{nombre,p:[x0,y0,x1,y1]}] (feet) [decisiones=[{id_simbolo,decision,evidencia,codigo}]] → plan + ops for plan→commit | " +
            "red ruta=tramos.json (proximity ≠ continuity ≠ physical connection). " +
            "terminales ruta= pagina= nivel= parametro_sentido= anclas=[{nombre,pdf,modelo}] items=[{ref,pt,tipo,sentido,elev}] → terminal plans. " +
            "equipos ruta= pagina= nivel= anclas=[{nombre,pdf,modelo}] reglas=[{ambito,codigo,clase,tipo}] items=[{ref,pt,ambito,zona,codigo,elev,evidencia}] → equipment plans. " +
            "riser pdf= xlsx= hoja= [columnas= fila_encabezado= fila_inicio= fila_final=] pdf_aristas=[{id,origen,destino,pagina,evidencia}] vista= estilo= nodos=[…] trazado=[…]. riser_audit rereads the sources and compares observado=[…]. " +
            "Page coordinates are never Revit coordinates: the workflow registers with anchors and escalates ambiguities.";

        private static readonly Dictionary<string, string> PdfJobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["info"] = "pdf_info", ["extract"] = "pdf_extract", ["tables"] = "pdf_tables", ["symbols"] = "pdf_symbols", ["grids"] = "pdf_grids",
            ["verify"] = "pdf_verify",
        };

        /// <summary>Folders a job may open: the document folders and, in lab, the lab folders as well.</summary>
        public static List<string> AuthorizedFolders(EngineConfig cfg)
        {
            var l = new List<string>(cfg.DocumentFolders ?? new List<string>());
            if (cfg.Profile != null && cfg.Profile.IsLab) l.AddRange(cfg.Profile.LabFolders);
            return l.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Dispatcher options from config.json, searching next to the executable when no paths are given.</summary>
        public static PowerEngineOptions OptionsFrom(EngineConfig cfg, string baseDir)
        {
            var win = Path.DirectorySeparatorChar == '\\';
            string Near(params string[] parts) { var p = Path.Combine(new[] { baseDir, "power_engine" }.Concat(parts).ToArray()); return File.Exists(p) ? p : null; }
            Calibration cal = null;
            var calPath = Path.Combine(baseDir, "power_engine", "calibracion.json");
            if (File.Exists(calPath))
                try { cal = Calibration.FromJson(Json.ParseObject(File.ReadAllText(calPath))); } catch (Exception) { cal = null; /* broken file → default values */ }
            return new PowerEngineOptions
            {
                Calibration = cal ?? Calibration.Default(),
                PythonWorkerScript = cfg.PythonWorkerScript ?? Near("python", "run_worker.py"),
                PythonExe = cfg.PythonExe ?? (win ? "python.exe" : "python3"),
                RustWorkerExe = cfg.RustWorkerExe ?? Near(win ? "bimbridge-power-engine.exe" : "bimbridge-power-engine"),
                Geometry = cfg.Geometry ?? "auto",
            };
        }

        public static Dictionary<string, object> Run(PowerEngineDispatcher pe, IList<string> folders, string line)
        {
            var c = Command.Parse(line);
            if (c == null) return Err("empty command", Help);
            try
            {
                switch (c.Verb)
                {
                    case "cap": return Cap(string.Join(" ", c.Positional));
                    case "estado":
                        return new Dictionary<string, object>
                        {
                            ["motores"] = pe.Status(), ["carpetas_documentos"] = (long)folders.Count, ["capacidades"] = PowerEngineCapabilityRegistry.Summary(),
                            ["metricas"] = pe.Metrics(),
                        };
                    case "pdf":
                    {
                        var sub = c.Positional.FirstOrDefault() ?? "";
                        if (!PdfJobs.TryGetValue(sub, out var job)) return Err("unknown pdf " + sub, "pdf info|extract|tables|symbols|grids|verify ruta=");
                        return Doc(pe, folders, job, c.Args);
                    }
                    case "docx": return Doc(pe, folders, "docx_extract", c.Args);
                    case "flujo": return Flow(pe, folders, c.Args);
                    case "terminales": return Markup(pe, folders, c.Args, equipment: false);
                    case "equipos": return Markup(pe, folders, c.Args, equipment: true);
                    case "riser": return Riser(pe, folders, c.Args, audit: false);
                    case "riser_audit": return Riser(pe, folders, c.Args, audit: true);
                    case "red": return Network(pe, folders, c.Args);
                    case "guide": case "ayuda": return new Dictionary<string, object> { ["ayuda"] = Help };
                    default: return Err("unknown command: " + c.Verb, Help);
                }
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is KeyNotFoundException || ex is FormatException || ex is ArgumentException || ex is NullReferenceException || ex is QueryException)
            {
                return Err("invalid arguments: " + ex.Message, Help);
            }
        }

        private static Dictionary<string, object> Cap(string q)
        {
            var exact = PowerEngineCapabilityRegistry.Describe(q.Trim());
            if (exact != null) return exact.Full();
            var hits = PowerEngineCapabilityRegistry.Search(q, 8);
            return new Dictionary<string, object> { ["capacidades"] = hits.Select(h => (object)h.Brief()).ToList(), ["detalle"] = "power \"cap <id>\"" };
        }

        private static Dictionary<string, object> Doc(PowerEngineDispatcher pe, IList<string> folders, string job, Dictionary<string, object> args)
        {
            if (folders.Count == 0) return Err("no authorized document folders", "IT or the user sets them in config.json → \"carpetas_documentos\"; the AI cannot change this");
            var a = new Dictionary<string, object>(args, StringComparer.Ordinal);
            if (a.TryGetValue("paginas", out var p) && p is long one) a["paginas"] = new List<object> { one };
            var r = pe.Document(job, a, folders);
            var d = new Dictionary<string, object> { ["codigo"] = r.Code, ["motor"] = r.Telemetry.Engine, ["ms"] = Math.Round(r.Telemetry.TotalMs, 1) };
            if (!r.Ok) d["error"] = r.Error; else d["resultado"] = r.Result;
            return d;
        }

        private static Dictionary<string, object> Flow(PowerEngineDispatcher pe, IList<string> folders, Dictionary<string, object> a)
        {
            if (folders.Count == 0) return Err("no authorized document folders", "config.json → \"carpetas_documentos\"");
            var path = Arg.GetString(a, "ruta");
            if (path == null) return Err("missing ruta=", Help);
            if (!(a.TryGetValue("ejes", out var gridsArg) && gridsArg is List<object> grids) || grids.Count < 2)
                return Err("model grids are missing (ejes=[{nombre,p:[x0,y0,x1,y1]}] in feet)", "read them with revit and pass them as is; without grids there is no registration and nothing is placed");
            if (!(a.TryGetValue("reglas", out var rg) && rg is List<object> rules)) return Err("missing reglas=[{codigo,tipo,montaje|elev,ws}]", "without a rule a symbol stays ambiguous (sin_tipo); it is never guessed");
            var q = new PdfToPlan.Request
            {
                PdfPath = path, Page = (int)(Arg.GetLong(a, "pagina") ?? 1), Level = Arg.GetString(a, "nivel"),
                Rules = PdfToPlan.RulesFromJson(rules), ModelGrids = PdfToPlan.GridsFromJson(grids),
                TolFt = Num(a, "tol") ?? 0.25, MinConfidence = Num(a, "conf") ?? 0.9, Mode = Arg.GetString(a, "modo") ?? "semejanza",
            };
            if (a.TryGetValue("decisiones", out var decisions))
            {
                if (!(decisions is List<object> list)) return Err("decisiones requires a JSON list", Help);
                q.SymbolDecisions = list;
            }
            if (a.TryGetValue("existentes", out var ex) && ex is List<object> exl)
                q.Existing = exl.Cast<Dictionary<string, object>>().Select(e =>
                {
                    var p = (List<object>)e["p"];
                    return new ExistingElement
                    {
                        Id = Arg.GetLong(e, "id") ?? 0, FamilyType = Arg.GetString(e, "tipo"), Level = Arg.GetString(e, "nivel"),
                        X = Convert.ToDouble(p[0], System.Globalization.CultureInfo.InvariantCulture),
                        Y = Convert.ToDouble(p[1], System.Globalization.CultureInfo.InvariantCulture),
                    };
                }).ToList();
            return PdfToPlan.Run(pe, q, folders);
        }

        private static Dictionary<string, object> Markup(PowerEngineDispatcher pe, IList<string> folders, Dictionary<string, object> args, bool equipment)
        {
            var path = Arg.GetString(args, "ruta");
            if (string.IsNullOrWhiteSpace(path)) return Err("missing ruta to the markup PDF", Help);
            var info = Doc(pe, folders, "pdf_info", new Dictionary<string, object> { ["ruta"] = path });
            if (!(info.TryGetValue("resultado", out var value) && value is Dictionary<string, object> pdf)) return info;
            var page = Arg.GetLong(args, "pagina") ?? 1;
            var pages = (List<object>)pdf["detalle_paginas"];
            if (page < 1 || page > pages.Count) return Err("the markup page does not exist", Help);
            var detail = (Dictionary<string, object>)pages[(int)page - 1];
            var sha = Arg.GetString(pdf, "sha256");
            var source = Path.GetFileName(path) + "#p" + page;
            return MarkupPlacement.Plan(args, sha, source, Num(detail, "unidad_usuario") ?? 1, equipment);
        }

        private static Dictionary<string, object> Riser(PowerEngineDispatcher pe, IList<string> folders, Dictionary<string, object> args, bool audit)
        {
            if (!(args.TryGetValue("pdf_aristas", out var raw) && raw is List<object> rows && rows.Count > 0))
                return Err("provide pdf_aristas with the page and textual evidence of each feeder", Help);
            var declared = rows.Cast<Dictionary<string, object>>().ToList();
            var pages = declared.Select(r => Arg.GetLong(r, "pagina") ?? 0).Distinct().OrderBy(x => x).ToList();
            if (pages.Any(p => p <= 0)) return Err("every PDF feeder requires a positive page", Help);
            var workbook = new Dictionary<string, object> { ["ruta"] = Arg.GetString(args, "xlsx"), ["hoja"] = Arg.GetString(args, "hoja") };
            foreach (var option in new[] { "columnas", "fila_encabezado", "fila_inicio", "fila_final" })
                if (args.TryGetValue(option, out var value)) workbook[option] = value;
            var excel = Doc(pe, folders, "xlsx_feeders", workbook);
            if (!(excel.TryGetValue("resultado", out var sheet) && sheet is Dictionary<string, object> excelResult)) return excel;
            var pdf = Doc(pe, folders, "pdf_extract", new Dictionary<string, object>
            {
                ["ruta"] = Arg.GetString(args, "pdf"), ["paginas"] = pages.Cast<object>().ToList(), ["incluir"] = new List<object> { "texto" },
            });
            if (!(pdf.TryGetValue("resultado", out var document) && document is Dictionary<string, object> pdfResult)) return pdf;
            if (Arg.GetBool(pdfResult, "truncado") == true) return Err("the PDF read was truncated; narrow down the riser pages", Help);
            static string Normalize(string text) => string.Join(" ", (text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            var textByPage = ((List<object>)pdfResult["paginas"]).Cast<Dictionary<string, object>>().ToDictionary(
                page => Arg.GetLong(page, "numero").Value,
                page => Normalize(string.Join(" ", ((List<object>)page["palabras"]).Cast<Dictionary<string, object>>()
                    .Select(word => Arg.GetString(word, "texto")))));
            foreach (var row in declared)
            {
                var evidence = Normalize(Arg.GetString(row, "evidencia"));
                if (evidence.Length == 0 || !textByPage.TryGetValue(Arg.GetLong(row, "pagina").Value, out var text) ||
                    text.IndexOf(evidence, StringComparison.OrdinalIgnoreCase) < 0)
                    return Err("the page does not support the textual evidence of " + Arg.GetString(row, "id"), Help);
            }
            var result = RiserSourceWorkflow.Build(args, (List<object>)excelResult["aristas"], rows, audit);
            result["sha256_excel"] = excelResult["sha256"];
            result["sha256_pdf"] = pdfResult["sha256"];
            return result;
        }

        /// <summary>red ruta=conductos.json: runs read from Revit (tools/acceptance/0.9.0/conduits_090.cs) → three-layer analysis.</summary>
        private static Dictionary<string, object> Network(PowerEngineDispatcher pe, IList<string> folders, Dictionary<string, object> a)
        {
            var path = Arg.GetString(a, "ruta");
            if (path == null) return Err("missing ruta= (runs JSON)", Help);
            var full = Path.GetFullPath(path);
            if (!PowerEngineDispatcher.PathAllowed(full, folders.Select(Path.GetFullPath).ToList()))
                return new Dictionary<string, object> { ["codigo"] = PowerEngineContracts.Codes.PathDenied, ["error"] = "path not authorized: " + path };
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > 64L << 20) return Err("does not exist or exceeds 64 MB: " + path, Help);
            double D(object o) => Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);
            var segs = Json.Parse(File.ReadAllText(full)) is List<object> l ? l.Cast<Dictionary<string, object>>().Select(t =>
            {
                var p = (List<object>)t["a"]; var q = (List<object>)t["b"];
                return new RunSegment
                {
                    Id = Arg.GetString(t, "id"), Ax = D(p[0]), Ay = D(p[1]), Az = D(p[2]), Bx = D(q[0]), By = D(q[1]), Bz = D(q[2]),
                    DiameterFt = t.TryGetValue("diam", out var dm) && dm != null ? D(dm) : 0, System = Arg.GetString(t, "sistema"),
                    ConnA = Arg.GetString(t, "con_a"), ConnB = Arg.GetString(t, "con_b"),
                };
            }).ToList() : throw new FormatException("expected a list of runs");
            return MepNetwork.Analyze(segs, Num(a, "contacto") ?? 1.0 / 192.0, Num(a, "proximidad") ?? 0.5, pe);
        }

        private static double? Num(Dictionary<string, object> a, string k) => a.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : (double?)null;

        private static Dictionary<string, object> Err(string error, string hint) => new Dictionary<string, object> { ["codigo"] = "INVALIDO", ["error"] = error, ["pista"] = hint };
    }
}
