// SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PDI.RevitMcp.Bridge
{
    /// <summary>
    /// PDI.RevitMcp.Bridge.exe — lo arranca Claude Code o Codex. stdout queda reservado para MCP;
    /// los registros van a stderr. No pide nada al usuario.
    ///
    /// Claude Code:  claude mcp add pdi-revit -- "C:\ruta\PDI.RevitMcp.Bridge.exe"
    /// Codex:        [mcp_servers.pdi-revit]  command = "C:\\ruta\\PDI.RevitMcp.Bridge.exe"
    /// </summary>
    public static class Program
    {
        public const string Instructions =
            "Herramientas para leer y, si el usuario lo habilitó, cambiar el modelo de Revit abierto. Empiece con revit_context y catalog. " +
            "Use query con group_by o count para resúmenes y pida filas sólo si hacen falta; varias lecturas a la vez con lote. " +
            "Guarde resultados con save_as y reutilícelos con ids=$nombre en vez de repetir ids. " +
            "Para ver una vista use capture (colorear=categorias distingue luminarias de tomas e interruptores); para señalar elementos al usuario, show. " +
            "Para cambiar algo: plan → commit (tipado, un Deshacer), exec (C# sobre la API de Revit) o fam/doc_op (familias, conectores, guardar, sync, PDF). " +
            "Si el usuario autorizó la tarea, aplique sin pedir confirmación por lote; si no, muestre el diff antes del commit. clave= evita repetir una escritura. " +
            "estado=retenido significa que el cambio espera en la cola de Revit a que el usuario lo aplique: no lo repita. " +
            "Si una respuesta trae 'estado', lea 'aviso' y siga su indicación. Detalle de cualquier herramienta: guide.";

        /// <summary>
        /// Perfil: compacto (por defecto: 3 herramientas, órdenes de una línea, respuestas en texto compacto) o completo
        /// (14 herramientas con JSON). PDI_REVITMCP_PERFIL=completo o argumento --completo.
        /// </summary>
        public static bool Full =>
            string.Equals(System.Environment.GetEnvironmentVariable("PDI_REVITMCP_PERFIL"), "completo", System.StringComparison.OrdinalIgnoreCase)
            || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--completo") >= 0;

        /// <summary>Versión del puente (la del ensamblado, la misma que el instalador).</summary>
        public static readonly string Version = VersionOf(typeof(Program).Assembly);

        private static string VersionOf(System.Reflection.Assembly a)
        {
            var info = (System.Reflection.AssemblyInformationalVersionAttribute)System.Attribute.GetCustomAttribute(a, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            var v = info?.InformationalVersion ?? a.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus > 0 ? v.Substring(0, plus) : v;
        }

        public static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--probe")
            {
                System.Environment.ExitCode = await Probe.RunAsync();
                return;
            }
            if (args.Length > 0 && args[0] == "--probe-write")
            {
                System.Environment.ExitCode = await Probe.RunWriteAsync();
                return;
            }
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.Services.AddSingleton<RevitConnection>();
            var full = Full;
            var mcp = builder.Services
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "BIMBRIDGE - Power Engine", Version = Version };
                    o.ServerInstructions = full ? Instructions : CompactTools.Instructions;
                })
                .WithStdioServerTransport();
            if (full) mcp.WithTools<RevitTools>(); else mcp.WithTools<CompactTools>().WithTools<PowerTools>();

            await builder.Build().RunAsync();
        }
    }
}
