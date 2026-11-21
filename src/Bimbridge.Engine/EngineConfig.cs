// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Configuration (section 14). Lives in %APPDATA%\Bimbridge\config.json.
    /// If the file does not exist these defaults are used. Nothing is ever asked on screen.
    /// </summary>
    public sealed class EngineConfig
    {
        /// <summary>Kill switch: false = the add-in does not open the pipe.</summary>
        public bool Enabled = true;
        /// <summary>Active levels: R read, P plan, W write, D document.</summary>
        public string Levels = "R";
        /// <summary>
        /// General C# runner (exec): "auto" = query with R and run with W (like every other change); "query" = query only;
        /// "off" = no exec. Decided by the user or IT in config.json; the AI cannot change it.
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
        /// <summary>Extra folders to look for new installers in (e.g. a network or OneDrive folder that IT publishes).</summary>
        public List<string> UpdateFolders = new List<string>();
        /// <summary>0.8.0: authorized family folders (fam buscar / cargar_archivo never leave them).</summary>
        public List<string> FamilyFolders = new List<string>();
        /// <summary>0.8.0: trusted public keys for "Update from ZIP" ("RSA:modulus:exponent"). If any is set, the signature is mandatory.</summary>
        public List<string> UpdateKeys = new List<string>();
        /// <summary>0.8.0: explicit permissions per class (leer, modificar_proyecto, modificar_familia, cargar_archivo, guardar, sincronizar,
        /// exportar, codigo_dinamico, interfaz). Empty = derived from the levels, as before.</summary>
        public List<string> PermissionList = new List<string>();
        /// <summary>0.9.0: operating profile (produccion | laboratorio) and its policy. Production by default.</summary>
        public ProfilePolicy Profile = new ProfilePolicy();
        /// <summary>0.9.0: document folders (PDF/DOCX) that the Power Engine may open. Empty = none (power pdf is rejected).</summary>
        public List<string> DocumentFolders = new List<string>();
        /// <summary>0.9.0: Power Engine paths. Empty = looked up next to the Bridge (power_engine\...); if missing, controlled degradation.</summary>
        public string PythonExe, PythonWorkerScript, RustWorkerExe;
        public string Geometry = "auto";
        /// <summary>"Block C# code injection". Default false: exec allowed for authorized local agents.</summary>
        public bool BlockDynamicCode;
        /// <summary>
        /// Issue #3: operations that change something require a named agent (config "identity_required", default true).
        /// false only for legacy integrations that IT decides to tolerate; every anonymous operation is flagged in the journal (sin_nombre_modo_heredado).
        /// </summary>
        public bool IdentityRequired = true;
        /// <summary>
        /// AUD-13: explicit user authorization (config "shared_history": true) for an agent to see in the history the
        /// operations of OTHER sessions and agents. Default false: each agent sees only its own.
        /// </summary>
        public bool SharedHistory;
        /// <summary>
        /// 0.9.1: online update from public GitHub Releases. "manual" (default): connects only when a
        /// person clicks "Check GitHub"; "no": disabled (corporate networks that do not allow it). There is never an automatic connection.
        /// </summary>
        public string OnlineUpdate = "manual";
        /// <summary>owner/name repository the releases are read from (config "update_repo").</summary>
        public string UpdateRepo;
        /// <summary>Accept prereleases in the online update (config "update_prerelease", default false).</summary>
        public bool UpdatePrerelease;
        private HashSet<string> _perms;
        public HashSet<string> EffectivePermissions => _perms ?? (_perms = Permissions.Effective(PermissionList, Levels));

        public bool Allows(char level) => Levels.IndexOf(char.ToUpperInvariant(level)) >= 0;

        /// <summary>Save, sync, PDF and close: level D, or W (whoever can change the model can save it).</summary>
        public bool AllowsDocOps => Allows('D') || Allows('W');

        public bool AllowsExec(bool execute) =>
            Runner != "off" && (execute ? Runner == "auto" && Allows('W') : Allows('R'));

        public static string DefaultPath
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("BIMBRIDGE_CONFIG");
                if (!string.IsNullOrEmpty(root)) return root;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bimbridge", "config.json");
            }
        }

        public static EngineConfig Load(string path = null, Action<string> warn = null)
        {
            var c = new EngineConfig();
            path = path ?? DefaultPath;
            if (!File.Exists(path)) return c;
            try
            {
                var o = LegacyAliases.NormalizeConfig(Json.ParseObject(File.ReadAllText(path)));
                c.Enabled = Arg.GetBool(o, "enabled") ?? c.Enabled;
                c.Levels = (Arg.GetString(o, "levels") ?? c.Levels).ToUpperInvariant();
                c.Runner = (Arg.GetString(o, "runner") ?? c.Runner).Trim().ToLowerInvariant();
                if (c.Runner != "auto" && c.Runner != "query" && c.Runner != "off") c.Runner = "auto";
                c.SubBatchSize = Clamp(Arg.GetLong(o, "sub_batch"), c.SubBatchSize, 1, 5000);
                c.MainThreadBudgetMs = Clamp(Arg.GetLong(o, "main_thread_budget_ms"), c.MainThreadBudgetMs, 20, 5000);
                c.PlanExpiryMinutes = Clamp(Arg.GetLong(o, "plan_expiry_min"), c.PlanExpiryMinutes, 1, 240);
                c.QueueWaitMinutes = Clamp(Arg.GetLong(o, "queue_wait_min"), c.QueueWaitMinutes, 0, 120);
                c.BusyAfterMs = Clamp(Arg.GetLong(o, "busy_after_ms"), c.BusyAfterMs, 500, 120000);
                c.HeartbeatTimeoutMs = Clamp(Arg.GetLong(o, "heartbeat_timeout_ms"), c.HeartbeatTimeoutMs, 5000, 600000);
                c.JournalDays = Clamp(Arg.GetLong(o, "journal_days"), c.JournalDays, 1, 365);
                c.UpdateFolders = Arg.GetStringList(o, "update_folders");
                c.FamilyFolders = Arg.GetStringList(o, "family_folders");
                c.UpdateKeys = Arg.GetStringList(o, "update_keys");
                c.PermissionList = Arg.GetStringList(o, "permissions");
                c.DocumentFolders = Arg.GetStringList(o, "document_folders");
                c.BlockDynamicCode = Arg.GetBool(o, DynamicCodePolicy.ConfigKey) ?? false;
                c.IdentityRequired = Arg.GetBool(o, "identity_required") ?? true;
                c.SharedHistory = Arg.GetBool(o, "shared_history") ?? false;
                var online = (Arg.GetString(o, "online_update") ?? "manual").Trim().ToLowerInvariant();
                c.OnlineUpdate = online == "no" ? "no" : "manual";
                c.UpdateRepo = Arg.GetString(o, "update_repo");
                c.UpdatePrerelease = Arg.GetBool(o, "update_prerelease") ?? false;
                // 0.9.0: "power_engine": { "python": "C:\\…\\python.exe", "worker_python": "…\\run_worker.py", "rust_worker": "…exe", "geometry": "auto" }
                if (o.TryGetValue("power_engine", out var po) && po is Dictionary<string, object> pe)
                {
                    c.PythonExe = Arg.GetString(pe, "python");
                    c.PythonWorkerScript = Arg.GetString(pe, "worker_python");
                    c.RustWorkerExe = Arg.GetString(pe, "rust_worker");
                    var g = (Arg.GetString(pe, "geometry") ?? "auto").Trim().ToLowerInvariant();
                    c.Geometry = g == "csharp" || g == "rust_ffi" || g == "rust_ipc" ? g : "auto";
                }
                // 0.9.0: profile. "lab": { "folders": [...], "unsigned_updates": false }; "authorized_code": ["sha256[@key]"]
                var lab = o.TryGetValue("lab", out var lo) ? lo as Dictionary<string, object> : null;
                c.Profile = ProfilePolicy.Parse(Arg.GetString(o, "profile"), lab == null ? null : Arg.GetStringList(lab, "folders"),
                    lab != null && (Arg.GetBool(lab, "unsigned_updates") ?? false), Arg.GetStringList(o, "authorized_code"));
            }
            catch (Exception ex)
            {
                // A broken configuration must not bring Revit down: warn and turn the MCP off for safety.
                warn?.Invoke("config.json unreadable (" + ex.Message + "); MCP disabled");
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
            ["profile"] = Profile.Name,
            [DynamicCodePolicy.ConfigKey] = BlockDynamicCode,
        };
    }
}
