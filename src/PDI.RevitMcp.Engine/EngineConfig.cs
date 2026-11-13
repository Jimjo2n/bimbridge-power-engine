// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Configuración (sección 14). Vive en %APPDATA%\PDI.RevitMcp\config.json.
    /// Si el archivo no existe se usan estos valores por defecto. Nunca se pregunta nada en pantalla.
    /// </summary>
    public sealed class EngineConfig
    {
        /// <summary>Interruptor de apagado: false = el add-in no abre el pipe.</summary>
        public bool Enabled = true;
        /// <summary>Niveles activos: R lectura, P plan, W escritura, D documento.</summary>
        public string Levels = "R";
        /// <summary>
        /// Runner general de C# (exec): "auto" = consulta con R y ejecución con W (como el resto de cambios); "consulta" = sólo consulta;
        /// "apagado" = sin exec. Lo decide el usuario o IT en config.json; la IA no puede cambiarlo.
        /// </summary>
        public string Runner = "auto";
        public int SubBatchSize = 200;
        public int MainThreadBudgetMs = 250;
        public int PlanExpiryMinutes = 15;
        public int QueueWaitMinutes = 10;
        public int BusyAfterMs = 5000;
        public int HeartbeatTimeoutMs = 30000;
        public int JournalDays = 14;
        public int MaxPageSize = QuerySpec.MaxPageSize;
        /// <summary>Carpetas extra donde buscar instaladores nuevos (p. ej. una carpeta de red o de OneDrive que publique IT).</summary>
        public List<string> UpdateFolders = new List<string>();
        /// <summary>0.8.0: carpetas de familias autorizadas (fam buscar / cargar_archivo nunca salen de aquí).</summary>
        public List<string> FamilyFolders = new List<string>();
        /// <summary>0.8.0: claves públicas de confianza para "Actualizar desde ZIP" ("RSA:módulo:exponente"). Con alguna, la firma es obligatoria.</summary>
        public List<string> UpdateKeys = new List<string>();
        /// <summary>0.8.0: permisos explícitos por clase (leer, modificar_proyecto, modificar_familia, cargar_archivo, guardar, sincronizar,
        /// exportar, codigo_dinamico, interfaz). Vacío = se derivan de los niveles, como antes.</summary>
        public List<string> PermissionList = new List<string>();
        /// <summary>0.9.0: perfil de funcionamiento (produccion | laboratorio) y su política. Por defecto, producción.</summary>
        public ProfilePolicy Profile = new ProfilePolicy();
        /// <summary>0.9.0: carpetas de documentos (PDF/DOCX) que el Power Engine puede abrir. Vacío = ninguna (power pdf se rechaza).</summary>
        public List<string> DocumentFolders = new List<string>();
        /// <summary>0.9.0: rutas del Power Engine. Vacías = se buscan junto al Bridge (power_engine\...); si no están, degradación controlada.</summary>
        public string PythonExe, PythonWorkerScript, RustWorkerExe;
        public string Geometry = "auto";
        /// <summary>«Bloquear inyección de código C#». Por defecto false: exec permitido a los agentes locales autorizados.</summary>
        public bool BlockDynamicCode;
        /// <summary>
        /// Issue #3: las operaciones que cambian algo exigen un agente con nombre (config "identidad_obligatoria", por defecto true).
        /// false sólo para integraciones antiguas que IT decida tolerar; cada operación anónima queda marcada en el diario (sin_nombre_modo_heredado).
        /// </summary>
        public bool IdentityRequired = true;
        /// <summary>
        /// AUD-13: autorización explícita del usuario (config "historial_compartido": true) para que un agente vea en el historial las
        /// operaciones de OTRAS sesiones y agentes. Por defecto false: cada agente sólo ve lo suyo.
        /// </summary>
        public bool SharedHistory;
        /// <summary>
        /// 0.9.1: actualización en línea desde las Releases públicas de GitHub. "manual" (por defecto): sólo se conecta cuando una
        /// persona pulsa «Buscar en GitHub»; "no": deshabilitada (redes corporativas que no lo permitan). Nunca hay conexión automática.
        /// </summary>
        public string OnlineUpdate = "manual";
        /// <summary>Repositorio propietario/nombre del que se leen las releases (config "repositorio_actualizacion").</summary>
        public string UpdateRepo;
        /// <summary>Aceptar prereleases en la actualización en línea (config "actualizacion_prerelease", por defecto false).</summary>
        public bool UpdatePrerelease;
        private HashSet<string> _perms;
        public HashSet<string> EffectivePermissions => _perms ?? (_perms = Permissions.Effective(PermissionList, Levels));

        public bool Allows(char level) => Levels.IndexOf(char.ToUpperInvariant(level)) >= 0;

        /// <summary>Guardar, sincronizar, PDF y cerrar: nivel D, o W (quien puede cambiar el modelo puede guardarlo).</summary>
        public bool AllowsDocOps => Allows('D') || Allows('W');

        public bool AllowsExec(bool execute) =>
            Runner != "apagado" && (execute ? Runner == "auto" && Allows('W') : Allows('R'));

        public static string DefaultPath
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("PDI_REVITMCP_CONFIG");
                if (!string.IsNullOrEmpty(root)) return root;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDI.RevitMcp", "config.json");
            }
        }

        public static EngineConfig Load(string path = null, Action<string> warn = null)
        {
            var c = new EngineConfig();
            path = path ?? DefaultPath;
            if (!File.Exists(path)) return c;
            try
            {
                var o = Json.ParseObject(File.ReadAllText(path));
                c.Enabled = Arg.GetBool(o, "enabled") ?? c.Enabled;
                c.Levels = (Arg.GetString(o, "levels") ?? c.Levels).ToUpperInvariant();
                c.Runner = (Arg.GetString(o, "runner") ?? c.Runner).Trim().ToLowerInvariant();
                if (c.Runner != "auto" && c.Runner != "consulta" && c.Runner != "apagado") c.Runner = "auto";
                c.SubBatchSize = Clamp(Arg.GetLong(o, "sub_batch"), c.SubBatchSize, 1, 5000);
                c.MainThreadBudgetMs = Clamp(Arg.GetLong(o, "main_thread_budget_ms"), c.MainThreadBudgetMs, 20, 5000);
                c.PlanExpiryMinutes = Clamp(Arg.GetLong(o, "plan_expiry_min"), c.PlanExpiryMinutes, 1, 240);
                c.QueueWaitMinutes = Clamp(Arg.GetLong(o, "queue_wait_min"), c.QueueWaitMinutes, 0, 120);
                c.BusyAfterMs = Clamp(Arg.GetLong(o, "busy_after_ms"), c.BusyAfterMs, 500, 120000);
                c.HeartbeatTimeoutMs = Clamp(Arg.GetLong(o, "heartbeat_timeout_ms"), c.HeartbeatTimeoutMs, 5000, 600000);
                c.JournalDays = Clamp(Arg.GetLong(o, "journal_days"), c.JournalDays, 1, 365);
                c.UpdateFolders = Arg.GetStringList(o, "carpetas_actualizacion");
                c.FamilyFolders = Arg.GetStringList(o, "carpetas_familias");
                c.UpdateKeys = Arg.GetStringList(o, "claves_actualizacion");
                c.PermissionList = Arg.GetStringList(o, "permisos");
                c.DocumentFolders = Arg.GetStringList(o, "carpetas_documentos");
                c.BlockDynamicCode = Arg.GetBool(o, DynamicCodePolicy.ConfigKey) ?? false;
                c.IdentityRequired = Arg.GetBool(o, "identidad_obligatoria") ?? true;
                c.SharedHistory = Arg.GetBool(o, "historial_compartido") ?? false;
                var online = (Arg.GetString(o, "actualizacion_online") ?? "manual").Trim().ToLowerInvariant();
                c.OnlineUpdate = online == "no" ? "no" : "manual";
                c.UpdateRepo = Arg.GetString(o, "repositorio_actualizacion");
                c.UpdatePrerelease = Arg.GetBool(o, "actualizacion_prerelease") ?? false;
                // 0.9.0: "power_engine": { "python": "C:\\…\\python.exe", "worker_python": "…\\run_worker.py", "rust_worker": "…exe", "geometria": "auto" }
                if (o.TryGetValue("power_engine", out var po) && po is Dictionary<string, object> pe)
                {
                    c.PythonExe = Arg.GetString(pe, "python");
                    c.PythonWorkerScript = Arg.GetString(pe, "worker_python");
                    c.RustWorkerExe = Arg.GetString(pe, "rust_worker");
                    var g = (Arg.GetString(pe, "geometria") ?? "auto").Trim().ToLowerInvariant();
                    c.Geometry = g == "csharp" || g == "rust_ffi" || g == "rust_ipc" ? g : "auto";
                }
                // 0.9.0: perfil. "laboratorio": { "carpetas": [...], "actualizacion_sin_firma": false }; "codigo_autorizado": ["sha256[@clave]"]
                var lab = o.TryGetValue("laboratorio", out var lo) ? lo as Dictionary<string, object> : null;
                c.Profile = ProfilePolicy.Parse(Arg.GetString(o, "perfil"), lab == null ? null : Arg.GetStringList(lab, "carpetas"),
                    lab != null && (Arg.GetBool(lab, "actualizacion_sin_firma") ?? false), Arg.GetStringList(o, "codigo_autorizado"));
            }
            catch (Exception ex)
            {
                // Una configuración rota no debe tumbar Revit: se avisa y se apaga el MCP por seguridad.
                warn?.Invoke("config.json ilegible (" + ex.Message + "); MCP desactivado");
                c.Enabled = false;
            }
            return c;
        }

        private static int Clamp(long? v, int fallback, int min, int max)
        {
            if (!v.HasValue) return fallback;
            return (int)Math.Max(min, Math.Min(max, v.Value));
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["enabled"] = Enabled,
            ["levels"] = Levels,
            ["runner"] = Runner,
            ["sub_batch"] = SubBatchSize,
            ["main_thread_budget_ms"] = MainThreadBudgetMs,
            ["plan_expiry_min"] = PlanExpiryMinutes,
            ["queue_wait_min"] = QueueWaitMinutes,
            ["busy_after_ms"] = BusyAfterMs,
            ["perfil"] = Profile.Name,
            [DynamicCodePolicy.ConfigKey] = BlockDynamicCode,
        };
    }
}
