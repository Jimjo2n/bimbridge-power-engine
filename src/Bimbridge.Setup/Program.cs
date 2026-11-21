// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Bimbridge.Setup
{
    /// <summary>
    /// Bimbridge.Setup.exe
    ///   (no arguments)            install window
    ///   /uninstall                uninstall window (used by Windows "Apps")
    ///   /quiet                    no window, for IT deployment. With:
    ///       /revit=2024,2025      versions (default: all detected)
    ///       /niveles=R|RP|RPW     AI permissions (default R)
    ///       /claude /codex        connect those AIs
    ///   /quiet /uninstall [/borrardatos]
    ///   /restaurar[=folder]       goes back to the latest install backup (or the given one). Requires Revit closed. Code 2 if it is open.
    ///   /actualizar [/permitir-anterior]   used by the ribbon Update button: same options as the
    ///                             previous install, no window, once all Revit instances are closed
    ///   /apply-pending            0.8.0 "Update from ZIP": waits for the USER to close Revit (never closes it),
    ///                             installs the prepared update, verifies it and, if it fails, goes back to the previous one
    ///   /reemplazar               /quiet over an existing install (creates a backup). Without it, code 5.
    /// Exit code: 0 ok, 1 error, 2 Revit open, 3 no previous install, 4 already up to date, 5 conflict with an existing install.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // Our own DLLs travel inside the .exe: a single file for the user.
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = "lib." + new AssemblyName(e.Name).Name + ".dll";
                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (st == null) return null;
                    var buf = new byte[st.Length];
                    st.Read(buf, 0, buf.Length);
                    return Assembly.Load(buf);
                }
            };
            return Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            var opts = CommandLine.Parse(args);
            if (opts.Quiet) return Quiet.Run(opts);
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
            var win = new SetupWindow(opts.Uninstall);
            return app.Run(win.Window);
        }
    }

    public sealed class CommandLine
    {
        public bool Quiet, Uninstall, Claude, Codex, RemoveData, Update, AllowOlder, Diagnose, ApplyPending, Restore, Replace;
        public string Levels, RestoreFrom;
        public string[] Versions;

        public static CommandLine Parse(string[] args)
        {
            var c = new CommandLine();
            foreach (var raw in args)
            {
                var a = raw.Trim();
                var low = a.ToLowerInvariant().TrimStart('/', '-');
                if (low == "quiet" || low == "silent" || low == "s") c.Quiet = true;
                else if (low == "uninstall" || low == "uninstall") c.Uninstall = true;
                else if (low == "update" || low == "update") { c.Update = true; c.Quiet = true; }
                else if (low == "diagnostics" || low == "diagnostics" || low == "diagnose") { c.Diagnose = true; c.Quiet = true; }
                else if (low == "restore" || low == "rollback") { c.Restore = true; c.Quiet = true; }
                else if (low.StartsWith("restore=")) { c.Restore = true; c.Quiet = true; c.RestoreFrom = a.TrimStart('/', '-').Substring("restore=".Length).Trim('"'); }
                else if (low == "apply-pending" || low == Bimbridge.Protocol.LegacyAliases.ApplyPendingSwitch) { c.ApplyPending = true; c.Quiet = true; }   // an older add-in launches the new installer with the legacy switch
                else if (low == "replace" || low == "replace") c.Replace = true;
                else if (low == "permitir-anterior" || low == "allow-older") c.AllowOlder = true;
                else if (low == "claude") c.Claude = true;
                else if (low == "codex") c.Codex = true;
                else if (low == "removedata" || low == "removedata") c.RemoveData = true;
                else if (low.StartsWith("levels=") || low.StartsWith("levels=")) c.Levels = low.Substring(low.IndexOf('=') + 1).ToUpperInvariant();
                else if (low.StartsWith("revit=")) c.Versions = low.Substring(6).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            }
            return c;
        }
    }
}
