// SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bimbridge.Bridge
{
    /// <summary>
    /// Bimbridge.Bridge.exe — launched by Claude Code or Codex. stdout is reserved for MCP;
    /// logs go to stderr. It never prompts the user.
    ///
    /// Claude Code:  claude mcp add bimbridge -- "C:\path\Bimbridge.Bridge.exe"
    /// Codex:        [mcp_servers.bimbridge]  command = "C:\\path\\Bimbridge.Bridge.exe"
    /// </summary>
    public static class Program
    {
        public const string Instructions =
            "Tools to read and, if the user enabled it, change the open Revit model. Start with revit_context and catalog. " +
            "Use query with group_by or count for summaries and request rows only when needed; several reads at once with batch. " +
            "Save results with save_as and reuse them with ids=$name instead of repeating ids. " +
            "$sel-xxxxxxxxxxxx = elements the user selected in Revit (Copy selection ID button): use it wherever ids or a set are accepted. " +
            "To see a view use capture (color_by=categories tells lighting fixtures apart from receptacles and switches); to point elements out to the user, show. " +
            "To change something: plan → commit (typed, one Undo), exec (C# against the Revit API) or fam/doc_op (families, connectors, save, sync, PDF). " +
            "If the user authorized the task, apply without asking for confirmation per batch; otherwise show the diff before the commit. key= avoids repeating a write. " +
            "status=held means the change is waiting in the Revit queue for the user to apply it: do not repeat it. " +
            "If a response has 'status', read 'warning' and follow it. Details for any tool: guide.";

        /// <summary>
        /// Profile: compact (default: 3 tools, one-line commands, compact text responses) or full
        /// (14 tools with JSON). BIMBRIDGE_PROFILE=complete or the --complete argument (the legacy switch is still accepted).
        /// </summary>
        public static bool Full =>
            string.Equals(System.Environment.GetEnvironmentVariable("BIMBRIDGE_PROFILE"), "complete", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(Bimbridge.Protocol.LegacyAliases.Token(System.Environment.GetEnvironmentVariable("BIMBRIDGE_PROFILE")), "complete", System.StringComparison.OrdinalIgnoreCase)
            || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--complete") >= 0
            || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), Bimbridge.Protocol.LegacyAliases.FullProfileSwitch) >= 0;

        /// <summary>Bridge version (the assembly's, same as the installer's).</summary>
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
                    o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "BIMBRIDGE", Version = Version };
                    o.ServerInstructions = full ? Instructions : CompactTools.Instructions;
                })
                .WithStdioServerTransport()
                // Legacy (0.9.3) tool argument names are renamed before binding. Remove after 0.9.5.
                .WithRequestFilters(f => f.AddCallToolFilter(next => (ctx, ct) => { LegacyToolArgs.Normalize(ctx.Params); return next(ctx, ct); }));
            if (full) mcp.WithTools<RevitTools>(); else mcp.WithTools<CompactTools>().WithTools<PowerTools>();

            await builder.Build().RunAsync();
        }
    }
}
