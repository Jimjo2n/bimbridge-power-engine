// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// BIMBRIDGE Power Engine — versioned contracts between the C# core and the specialized engines
    /// (AgentHost C# ↔ Python Worker, AgentHost C# ↔ Rust Geometry Engine). It is NOT another orchestration protocol: it is a
    /// single-job envelope (one explicit job → one result), with no shared state and no Revit access.
    ///
    /// IPC transport: line-delimited JSON (one object per line, UTF-8) over the child process's stdin/stdout, the same
    /// <see cref="Json"/> as the rest of BIMBRIDGE. FFI transport (Rust): the same payloads in flat buffers.
    ///
    /// Request: { "pe": 1, "id": "&lt;correlation&gt;", "job": "...", "args": {...}, "limits": {...}, "paths": [...] }
    /// Response: { "pe": 1, "id": "&lt;same&gt;", "code": "OK|...", "result": {...}, "error": "...", "telemetry": {...} }
    /// Handshake: job "hola" with { "pe_min", "pe_max" } → { "motor", "version", "pe", "capacidades": [...] }.
    /// </summary>
    public static class PowerEngineContracts
    {
        public const int ProtocolVersion = 1;
        public const int MinSupported = 1;
        public const string Hello = "hola";
        public const string Shutdown = "salir";

        /// <summary>Result codes. Any other code is treated as ERROR_MOTOR.</summary>
        public static class Codes
        {
            public const string Ok = "OK", Invalid = "INVALIDO", Unsupported = "NO_SOPORTADO", Timeout = "TIEMPO_AGOTADO", Cancelled = "CANCELADO",
                TooLarge = "DEMASIADO_GRANDE", PathDenied = "RUTA_DENEGADA", NoContent = "SIN_CONTENIDO", EngineError = "ERROR_MOTOR",
                Unavailable = "NO_DISPONIBLE", Incompatible = "VERSION_INCOMPATIBLE", MemoryLimit = "LIMITE_MEMORIA";
            public static readonly string[] All = { Ok, Invalid, Unsupported, Timeout, Cancelled, TooLarge, PathDenied, NoContent, EngineError, Unavailable, Incompatible, MemoryLimit };
        }

        public const string EngineCSharp = "csharp", EngineRustFfi = "rust_ffi", EngineRustIpc = "rust_ipc", EnginePython = "python";

        /// <summary>Per-job limits. The C# client enforces them itself (it does not trust the engine to respect them).</summary>
        public sealed class Limits
        {
            public int TimeoutMs = 30000;
            /// <summary>Maximum size of one message (request or response) in bytes.</summary>
            public long MaxMessageBytes = 64L * 1024 * 1024;
            /// <summary>Maximum memory of the child process in MB (requested from the engine and monitored by the client).</summary>
            public int MaxMemoryMb = 1024;

            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["time_ms"] = (long)TimeoutMs, ["max_bytes"] = MaxMessageBytes, ["max_mem_mb"] = (long)MaxMemoryMb,
            };
        }

        public sealed class Request
        {
            public string Id = NewId();
            public string Job;
            public Dictionary<string, object> Args = new Dictionary<string, object>();
            public Limits Limits = new Limits();
            /// <summary>Paths authorized for THIS job (files or folders). The worker rejects any other.</summary>
            public List<string> Paths = new List<string>();

            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["pe"] = (long)ProtocolVersion, ["id"] = Id, ["job"] = Job, ["args"] = Args, ["limits"] = Limits.ToJson(),
                ["paths"] = Paths.Cast<object>().ToList(),
            };
        }

        public sealed class Telemetry
        {
            public string Engine;
            public double TotalMs, EngineMs, SerializeMs, IpcMs;
            public long BytesIn, BytesOut, PeakMemoryKb;

            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["power_engine.motor"] = Engine,
                ["power_engine.total_ms"] = Math.Round(TotalMs, 3), ["power_engine.motor_ms"] = Math.Round(EngineMs, 3),
                ["power_engine.serializacion_ms"] = Math.Round(SerializeMs, 3), ["power_engine.ipc_ms"] = Math.Round(IpcMs, 3),
                ["power_engine.bytes_entrada"] = BytesIn, ["power_engine.bytes_salida"] = BytesOut, ["power_engine.memoria_pico_kb"] = PeakMemoryKb,
            };
        }

        public sealed class Response
        {
            public string Id, Code = Codes.Ok, Error;
            public Dictionary<string, object> Result = new Dictionary<string, object>();
            public Telemetry Telemetry = new Telemetry();
            public bool Ok => Code == Codes.Ok;

            public static Response Fail(string id, string code, string error) => new Response { Id = id, Code = code, Error = error };

            public Dictionary<string, object> ToJson()
            {
                var d = new Dictionary<string, object> { ["pe"] = (long)ProtocolVersion, ["id"] = Id, ["codigo"] = Code, ["resultado"] = Result, ["telemetria"] = Telemetry.ToJson() };
                if (Error != null) d["error"] = Error;
                return d;
            }
        }

        public static string NewId() => "pe-" + Guid.NewGuid().ToString("N").Substring(0, 16);

        /// <summary>
        /// Validates a received response against the request: version, correlation, known code. Returns the typed response or an
        /// ERROR_MOTOR/VERSION_INCOMPATIBLE failure. Never throws: a broken engine does not bring down the core.
        /// </summary>
        public static Response ParseResponse(string expectedId, Dictionary<string, object> o)
        {
            if (o == null) return Response.Fail(expectedId, Codes.EngineError, "empty response");
            var v = Arg.GetLong(o, "pe");
            if (v == null || v < MinSupported || v > ProtocolVersion) return Response.Fail(expectedId, Codes.Incompatible, "contract version " + (v?.ToString(CultureInfo.InvariantCulture) ?? "(none)") + " outside [" + MinSupported + "," + ProtocolVersion + "]");
            var id = Arg.GetString(o, "id");
            if (!string.Equals(id, expectedId, StringComparison.Ordinal)) return Response.Fail(expectedId, Codes.EngineError, "correlation mismatch: expected " + expectedId + " and got " + (id ?? "(none)"));
            var code = Arg.GetString(o, "code") ?? Codes.EngineError;
            if (!Codes.All.Contains(code)) code = Codes.EngineError;
            var r = new Response { Id = id, Code = code, Error = Arg.GetString(o, "error") };
            if (o.TryGetValue("result", out var res) && res is Dictionary<string, object> rd) r.Result = rd;
            if (o.TryGetValue("telemetry", out var t) && t is Dictionary<string, object> td)
            {
                r.Telemetry.EngineMs = ToD(td, "motor_ms");
                r.Telemetry.PeakMemoryKb = (long)ToD(td, "memoria_pico_kb");
            }
            if (!r.Ok && string.IsNullOrEmpty(r.Error)) r.Error = "the engine returned " + code + " without explanation";
            return r;
        }

        private static double ToD(Dictionary<string, object> d, string k) =>
            d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;

        /// <summary>Handshake result: which engine it is, its version, the agreed contract and its capabilities.</summary>
        public sealed class HelloInfo
        {
            public string Engine, Version;
            public int Protocol;
            public List<string> Capabilities = new List<string>();

            public static HelloInfo From(Response r) => new HelloInfo
            {
                Engine = Arg.GetString(r.Result, "motor"), Version = Arg.GetString(r.Result, "version"),
                Protocol = (int)(Arg.GetLong(r.Result, "pe") ?? 0), Capabilities = Arg.GetStringList(r.Result, "capacidades") ?? new List<string>(),
            };

            /// <summary>The agreed contract is within the range we support.</summary>
            public bool Compatible => Protocol >= MinSupported && Protocol <= ProtocolVersion;
        }
    }
}
