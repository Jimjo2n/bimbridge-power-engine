// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BimBridge.PowerEngine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Bridge
{
    /// <summary>
    /// 0.9.0: herramienta "power" del perfil compacto. BIMBRIDGE Power Engine fuera de Revit (C# núcleo, Python sólo documentos,
    /// Rust sólo geometría). No cambia el modelo: lo que habría que escribir sale como ops para plan→commit.
    /// </summary>
    [McpServerToolType]
    public sealed class PowerTools : IDisposable
    {
        private readonly Lazy<(PowerEngineDispatcher pe, List<string> folders)> _engine = new Lazy<(PowerEngineDispatcher, List<string>)>(() =>
        {
            var cfg = EngineConfig.Load();
            return (new PowerEngineDispatcher(PowerCommands.OptionsFrom(cfg, AppContext.BaseDirectory)), PowerCommands.AuthorizedFolders(cfg));
        });

        [McpServerTool(Name = "power", ReadOnly = true, Idempotent = true, OpenWorld = false)]
        [Description(PowerCommands.Help)]
        public Task<CallToolResult> Power([Description("Órdenes, p. ej. \"cap pdf\" o \"pdf symbols ruta=… paginas=1\"")] string c, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lines = Command.SplitLines(c);
            var res = new CallToolResult { Content = new List<ContentBlock>() };
            if (lines.Count == 0 || lines.Count > 8) { res.IsError = true; res.Content.Add(new TextContentBlock { Text = "1–8 órdenes. " + PowerCommands.Help }); return Task.FromResult(res); }
            var (pe, folders) = _engine.Value;
            var outs = new List<string>();
            var err = false;
            foreach (var l in lines)
            {
                ct.ThrowIfCancellationRequested();
                var d = PowerCommands.Run(pe, folders, l);
                err |= d.TryGetValue("codigo", out var code) && code is string s && s != "OK";
                outs.Add(Json.Write(d));
            }
            var text = ResultStore.Shorten(string.Join("\n", outs), CompactTools.MaxTextChars, "b", out _);
            res.Content.Add(new TextContentBlock { Text = text });
            res.IsError = err && lines.Count == 1;
            UsageMetrics.Record(null, "power", Command.Parse(lines[0])?.Verb ?? "", c?.Length ?? 0, text.Length, sw.ElapsedMilliseconds, err ? "error" : "ok");
            return Task.FromResult(res);
        }

        public void Dispose() { if (_engine.IsValueCreated) _engine.Value.pe.Dispose(); }
    }
}
