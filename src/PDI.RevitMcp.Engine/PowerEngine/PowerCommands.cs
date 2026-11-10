// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using BimBridge.PowerEngine.Workflows;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// Órdenes de una línea de la herramienta compacta "power" (Bridge). No tocan Revit: documentos autorizados, geometría y
    /// planificación. Lo que cambia el modelo sale como "ops" para plan→commit (mismas protecciones de siempre).
    ///   cap [texto|id]         descubrimiento bajo demanda (breve; con un id exacto, ficha completa)
    ///   estado                 motores, versiones, calibración, carpetas autorizadas y métricas power_engine.*
    ///   pdf info|extract|tables|symbols|grids ruta= [paginas=[1]] [max_elementos=]
    ///   docx ruta= [max=]
    ///   flujo ruta= pagina= nivel= reglas=[…] ejes=[…] [existentes=[…]] [tol=0.25] [conf=0.9] [modo=semejanza|afin]
    /// </summary>
    public static class PowerCommands
    {
        public const string Help =
            "power (no cambia el modelo): cap [texto|id] | estado | pdf info|extract|tables|symbols|grids ruta= paginas=[1] | docx ruta= | " +
            "flujo ruta= pagina= nivel= reglas=[{codigo,tipo,montaje|elev,ws}] ejes=[{nombre,p:[x0,y0,x1,y1]}] (pies) → plan + ops para plan→commit | " +
            "red ruta=tramos.json (proximidad ≠ continuidad ≠ conexión física). " +
            "Coordenadas de página nunca son de Revit: el flujo registra con anclas y escala ambigüedades.";

        private static readonly Dictionary<string, string> PdfJobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["info"] = "pdf_info", ["extract"] = "pdf_extract", ["tables"] = "pdf_tables", ["symbols"] = "pdf_symbols", ["grids"] = "pdf_grids",
        };

        /// <summary>Carpetas que un trabajo puede abrir: las de documentos y, en laboratorio, también las de laboratorio.</summary>
        public static List<string> AuthorizedFolders(EngineConfig cfg)
        {
            var l = new List<string>(cfg.DocumentFolders ?? new List<string>());
            if (cfg.Profile != null && cfg.Profile.IsLab) l.AddRange(cfg.Profile.LabFolders);
            return l.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Opciones del dispatcher desde config.json, con búsqueda junto al ejecutable si no se indican rutas.</summary>
        public static PowerEngineOptions OptionsFrom(EngineConfig cfg, string baseDir)
        {
            var win = Path.DirectorySeparatorChar == '\\';
            string Near(params string[] parts) { var p = Path.Combine(new[] { baseDir, "power_engine" }.Concat(parts).ToArray()); return File.Exists(p) ? p : null; }
            Calibration cal = null;
            var calPath = Path.Combine(baseDir, "power_engine", "calibracion.json");
            if (File.Exists(calPath))
                try { cal = Calibration.FromJson(Json.ParseObject(File.ReadAllText(calPath))); } catch (Exception) { cal = null; /* archivo roto → valores por defecto */ }
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
            if (c == null) return Err("orden vacía", Help);
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
                        if (!PdfJobs.TryGetValue(sub, out var job)) return Err("pdf " + sub + " desconocido", "pdf info|extract|tables|symbols|grids ruta=");
                        return Doc(pe, folders, job, c.Args);
                    }
                    case "docx": return Doc(pe, folders, "docx_extract", c.Args);
                    case "flujo": return Flow(pe, folders, c.Args);
                    case "red": return Network(pe, folders, c.Args);
                    case "guide": case "ayuda": return new Dictionary<string, object> { ["ayuda"] = Help };
                    default: return Err("orden desconocida: " + c.Verb, Help);
                }
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is KeyNotFoundException || ex is FormatException || ex is ArgumentException || ex is NullReferenceException)
            {
                return Err("argumentos inválidos: " + ex.Message, Help);
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
            if (folders.Count == 0) return Err("sin carpetas de documentos autorizadas", "IT o el usuario las fijan en config.json → \"carpetas_documentos\"; la IA no puede cambiarlo");
            var a = new Dictionary<string, object>(args, StringComparer.Ordinal);
            if (a.TryGetValue("paginas", out var p) && p is long one) a["paginas"] = new List<object> { one };
            var r = pe.Document(job, a, folders);
            var d = new Dictionary<string, object> { ["codigo"] = r.Code, ["motor"] = r.Telemetry.Engine, ["ms"] = Math.Round(r.Telemetry.TotalMs, 1) };
            if (!r.Ok) d["error"] = r.Error; else d["resultado"] = r.Result;
            return d;
        }

        private static Dictionary<string, object> Flow(PowerEngineDispatcher pe, IList<string> folders, Dictionary<string, object> a)
        {
            if (folders.Count == 0) return Err("sin carpetas de documentos autorizadas", "config.json → \"carpetas_documentos\"");
            var ruta = Arg.GetString(a, "ruta");
            if (ruta == null) return Err("falta ruta=", Help);
            if (!(a.TryGetValue("ejes", out var ej) && ej is List<object> ejes) || ejes.Count < 2)
                return Err("faltan los ejes del modelo (ejes=[{nombre,p:[x0,y0,x1,y1]}] en pies)", "léalos con revit y páselos tal cual; sin ejes no hay registro y no se coloca nada");
            if (!(a.TryGetValue("reglas", out var rg) && rg is List<object> reglas)) return Err("faltan reglas=[{codigo,tipo,montaje|elev,ws}]", "sin regla un símbolo queda ambiguo (sin_tipo), nunca se adivina");
            var q = new PdfToPlan.Request
            {
                PdfPath = ruta, Page = (int)(Arg.GetLong(a, "pagina") ?? 1), Level = Arg.GetString(a, "nivel"),
                Rules = PdfToPlan.RulesFromJson(reglas), ModelGrids = PdfToPlan.GridsFromJson(ejes),
                TolFt = Num(a, "tol") ?? 0.25, MinConfidence = Num(a, "conf") ?? 0.9, Mode = Arg.GetString(a, "modo") ?? "semejanza",
            };
            if (a.TryGetValue("existentes", out var ex) && ex is List<object> exl)
                q.Existing = exl.Cast<Dictionary<string, object>>().Select(e =>
                {
                    var p = (List<object>)e["p"];
                    return new ExistingElement { Id = Arg.GetLong(e, "id") ?? 0, FamilyType = Arg.GetString(e, "tipo"), X = Convert.ToDouble(p[0], System.Globalization.CultureInfo.InvariantCulture), Y = Convert.ToDouble(p[1], System.Globalization.CultureInfo.InvariantCulture) };
                }).ToList();
            return PdfToPlan.Run(pe, q, folders);
        }

        /// <summary>red ruta=conductos.json: tramos leídos de Revit (tools/aceptacion/0.9.0/conductos_090.cs) → análisis en tres capas.</summary>
        private static Dictionary<string, object> Network(PowerEngineDispatcher pe, IList<string> folders, Dictionary<string, object> a)
        {
            var ruta = Arg.GetString(a, "ruta");
            if (ruta == null) return Err("falta ruta= (JSON de tramos)", Help);
            var full = Path.GetFullPath(ruta);
            if (!PowerEngineDispatcher.PathAllowed(full, folders.Select(Path.GetFullPath).ToList()))
                return new Dictionary<string, object> { ["codigo"] = PowerEngineContracts.Codes.PathDenied, ["error"] = "ruta no autorizada: " + ruta };
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > 64L << 20) return Err("no existe o pasa de 64 MB: " + ruta, Help);
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
            }).ToList() : throw new FormatException("se esperaba una lista de tramos");
            return MepNetwork.Analyze(segs, Num(a, "contacto") ?? 1.0 / 192.0, Num(a, "proximidad") ?? 0.5, pe);
        }

        private static double? Num(Dictionary<string, object> a, string k) => a.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : (double?)null;

        private static Dictionary<string, object> Err(string error, string hint) => new Dictionary<string, object> { ["codigo"] = "INVALIDO", ["error"] = error, ["pista"] = hint };
    }
}
