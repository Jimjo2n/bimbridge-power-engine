// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// BIMBRIDGE Power Engine — contratos versionados entre el núcleo C# y los motores especializados
    /// (AgentHost C# ↔ Python Worker, AgentHost C# ↔ Rust Geometry Engine). NO es otro protocolo de orquestación: es un
    /// sobre de trabajo único (un trabajo explícito → un resultado), sin estado compartido ni acceso a Revit.
    ///
    /// Transporte IPC: JSON por líneas (un objeto por línea, UTF-8) sobre stdin/stdout del proceso hijo, el mismo
    /// <see cref="Json"/> que el resto de BIMBRIDGE. Transporte FFI (Rust): mismas cargas útiles en búferes planos.
    ///
    /// Petición: { "pe": 1, "id": "&lt;correlación&gt;", "trabajo": "...", "args": {...}, "limites": {...}, "rutas": [...] }
    /// Respuesta: { "pe": 1, "id": "&lt;misma&gt;", "codigo": "OK|...", "resultado": {...}, "error": "...", "telemetria": {...} }
    /// Saludo: trabajo "hola" con { "pe_min", "pe_max" } → { "motor", "version", "pe", "capacidades": [...] }.
    /// </summary>
    public static class PowerEngineContracts
    {
        public const int ProtocolVersion = 1;
        public const int MinSupported = 1;
        public const string Hello = "hola";
        public const string Shutdown = "salir";

        /// <summary>Códigos de resultado. Cualquier otro se trata como ERROR_MOTOR.</summary>
        public static class Codes
        {
            public const string Ok = "OK", Invalid = "INVALIDO", Unsupported = "NO_SOPORTADO", Timeout = "TIEMPO_AGOTADO", Cancelled = "CANCELADO",
                TooLarge = "DEMASIADO_GRANDE", PathDenied = "RUTA_DENEGADA", NoContent = "SIN_CONTENIDO", EngineError = "ERROR_MOTOR",
                Unavailable = "NO_DISPONIBLE", Incompatible = "VERSION_INCOMPATIBLE", MemoryLimit = "LIMITE_MEMORIA";
            public static readonly string[] All = { Ok, Invalid, Unsupported, Timeout, Cancelled, TooLarge, PathDenied, NoContent, EngineError, Unavailable, Incompatible, MemoryLimit };
        }

        public const string EngineCSharp = "csharp", EngineRustFfi = "rust_ffi", EngineRustIpc = "rust_ipc", EnginePython = "python";

        /// <summary>Límites por trabajo. El cliente C# los impone por su cuenta (no confía en que el motor los respete).</summary>
        public sealed class Limits
        {
            public int TimeoutMs = 30000;
            /// <summary>Tamaño máximo de un mensaje (petición o respuesta) en bytes.</summary>
            public long MaxMessageBytes = 64L * 1024 * 1024;
            /// <summary>Memoria máxima del proceso hijo en MB (se pide al motor y el cliente la vigila).</summary>
            public int MaxMemoryMb = 1024;

            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["tiempo_ms"] = (long)TimeoutMs, ["max_bytes"] = MaxMessageBytes, ["max_mem_mb"] = (long)MaxMemoryMb,
            };
        }

        public sealed class Request
        {
            public string Id = NewId();
            public string Job;
            public Dictionary<string, object> Args = new Dictionary<string, object>();
            public Limits Limits = new Limits();
            /// <summary>Rutas autorizadas para ESTE trabajo (archivos o carpetas). El worker rechaza cualquier otra.</summary>
            public List<string> Paths = new List<string>();

            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["pe"] = (long)ProtocolVersion, ["id"] = Id, ["trabajo"] = Job, ["args"] = Args, ["limites"] = Limits.ToJson(),
                ["rutas"] = Paths.Cast<object>().ToList(),
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
        /// Valida una respuesta recibida contra la petición: versión, correlación, código conocido. Devuelve la respuesta tipada o un
        /// fallo ERROR_MOTOR/VERSION_INCOMPATIBLE. Nunca lanza: un motor roto no tumba al núcleo.
        /// </summary>
        public static Response ParseResponse(string expectedId, Dictionary<string, object> o)
        {
            if (o == null) return Response.Fail(expectedId, Codes.EngineError, "respuesta vacía");
            var v = Arg.GetLong(o, "pe");
            if (v == null || v < MinSupported || v > ProtocolVersion) return Response.Fail(expectedId, Codes.Incompatible, "versión de contrato " + (v?.ToString(CultureInfo.InvariantCulture) ?? "(ninguna)") + " fuera de [" + MinSupported + "," + ProtocolVersion + "]");
            var id = Arg.GetString(o, "id");
            if (!string.Equals(id, expectedId, StringComparison.Ordinal)) return Response.Fail(expectedId, Codes.EngineError, "correlación distinta: se esperaba " + expectedId + " y llegó " + (id ?? "(ninguna)"));
            var code = Arg.GetString(o, "codigo") ?? Codes.EngineError;
            if (!Codes.All.Contains(code)) code = Codes.EngineError;
            var r = new Response { Id = id, Code = code, Error = Arg.GetString(o, "error") };
            if (o.TryGetValue("resultado", out var res) && res is Dictionary<string, object> rd) r.Result = rd;
            if (o.TryGetValue("telemetria", out var t) && t is Dictionary<string, object> td)
            {
                r.Telemetry.EngineMs = ToD(td, "motor_ms");
                r.Telemetry.PeakMemoryKb = (long)ToD(td, "memoria_pico_kb");
            }
            if (!r.Ok && string.IsNullOrEmpty(r.Error)) r.Error = "el motor devolvió " + code + " sin explicación";
            return r;
        }

        private static double ToD(Dictionary<string, object> d, string k) =>
            d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;

        /// <summary>Resultado del saludo: qué motor es, su versión, el contrato acordado y sus capacidades.</summary>
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

            /// <summary>El contrato acordado está en el rango que soportamos.</summary>
            public bool Compatible => Protocol >= MinSupported && Protocol <= ProtocolVersion;
        }
    }
}
