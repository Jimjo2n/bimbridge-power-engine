// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PDI.RevitMcp.Setup
{
    /// <summary>
    /// PDI.RevitMcp.Setup.exe
    ///   (sin argumentos)          ventana de instalación
    ///   /uninstall                ventana de desinstalación (la usa "Aplicaciones" de Windows)
    ///   /quiet                    sin ventana, para despliegue por IT. Con:
    ///       /revit=2024,2025      versiones (por defecto: todas las detectadas)
    ///       /niveles=R|RP|RPW     permisos de la IA (por defecto R)
    ///       /claude /codex        conectar esas IAs
    ///   /quiet /uninstall [/borrardatos]
    ///   /restaurar[=carpeta]      vuelve al último respaldo de instalación (o al indicado). Exige Revit cerrado. Código 2 si está abierto.
    ///   /actualizar [/permitir-anterior]   lo usa el botón Actualizar de la cinta: mismas opciones que la
    ///                             instalación anterior, sin ventana, con Revit abierto (entra al reiniciar)
    ///   /aplicar-pendiente        0.8.0 "Actualizar desde ZIP": espera a que el USUARIO cierre Revit (nunca lo cierra),
    ///                             instala la actualización preparada, la verifica y, si falla, vuelve a la anterior
    ///   /reemplazar               /quiet sobre una instalación existente (crea respaldo). Sin él, código 5.
    /// Código de salida: 0 bien, 1 error, 2 Revit abierto, 3 sin instalación previa, 4 ya está al día, 5 conflicto con una instalación existente.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // Las DLL propias viajan dentro del .exe: un solo archivo para el usuario.
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
                else if (low == "uninstall" || low == "desinstalar") c.Uninstall = true;
                else if (low == "actualizar" || low == "update") { c.Update = true; c.Quiet = true; }
                else if (low == "diagnostico" || low == "diagnóstico" || low == "diagnose") { c.Diagnose = true; c.Quiet = true; }
                else if (low == "restaurar" || low == "rollback") { c.Restore = true; c.Quiet = true; }
                else if (low.StartsWith("restaurar=")) { c.Restore = true; c.Quiet = true; c.RestoreFrom = a.TrimStart('/', '-').Substring("restaurar=".Length).Trim('"'); }
                else if (low == "aplicar-pendiente") { c.ApplyPending = true; c.Quiet = true; }
                else if (low == "reemplazar" || low == "replace") c.Replace = true;
                else if (low == "permitir-anterior" || low == "allow-older") c.AllowOlder = true;
                else if (low == "claude") c.Claude = true;
                else if (low == "codex") c.Codex = true;
                else if (low == "borrardatos" || low == "removedata") c.RemoveData = true;
                else if (low.StartsWith("niveles=") || low.StartsWith("levels=")) c.Levels = low.Substring(low.IndexOf('=') + 1).ToUpperInvariant();
                else if (low.StartsWith("revit=")) c.Versions = low.Substring(6).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            }
            return c;
        }
    }
}
