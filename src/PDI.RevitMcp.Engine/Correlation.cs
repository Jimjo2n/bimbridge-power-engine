// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Issue #4: identificadores de correlación de una petición. Se fijan en PipeHost y viajan por AsyncLocal (no por los argumentos, para
    /// no alterar huellas de idempotencia ni validaciones). El diario los registra en cada línea.
    ///   agent_id       = huella estable de (principal de Windows, alias). Igual al reconectar; distinto si cambia el alias o el usuario.
    ///   session_id     = instancia de sesión lógica (S-cliente-pid-&lt;64 bits aleatorios&gt;): nunca se reutiliza, tampoco al reconectar con el mismo pid.
    ///   request_id     = session_id + "#" + id de la petición en esa conexión (único en la ejecución de Revit).
    ///   trace_id       = el que trae el cliente en _traza (AgentHost, puente) o uno nuevo; une procesos.
    ///   job_id         = trabajo de fondo, si la respuesta lo trae.
    ///   transaction_id = id del plan aplicado (nombre del TransactionGroup en Revit), si la operación es un commit.
    /// </summary>
    public sealed class Correlation
    {
        public const string TraceArg = "_traza";

        public string AgentId { get; private set; }
        public string SessionId { get; private set; }
        public string RequestId { get; private set; }
        public string TraceId { get; private set; }
        public string JobId { get; set; }
        public string TransactionId { get; set; }

        private static readonly AsyncLocal<Correlation> _current = new AsyncLocal<Correlation>();

        /// <summary>Correlación de la petición en curso (null fuera de una petición).</summary>
        public static Correlation Current
        {
            get => _current.Value;
            internal set => _current.Value = value;
        }

        /// <summary>Crea la correlación y quita _traza de los argumentos (es transporte, no contenido).</summary>
        public static Correlation For(Session s, Request req)
        {
            string trace = null;
            if (req.Args != null && req.Args.TryGetValue(TraceArg, out var t))
            {
                req.Args.Remove(TraceArg);
                trace = ValidTrace(t as string);
            }
            return new Correlation
            {
                AgentId = AgentIdOf(s.Principal, s.AgentName),
                SessionId = s.Id,
                RequestId = s.Id + "#" + req.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TraceId = trace ?? NewTraceId(),
            };
        }

        /// <summary>Completa job_id y transaction_id con lo que se sabe al terminar.</summary>
        public void Complete(Request req, Response resp)
        {
            if (resp?.Datos is Dictionary<string, object> d)
            {
                if (JobId == null) JobId = Arg.GetString(d, "job_id");
                if (req.Op == Wire.OpCommit && TransactionId == null) TransactionId = Arg.GetString(d, "plan_id");
            }
            if (req.Op == Wire.OpCommit && TransactionId == null) TransactionId = Arg.GetString(req.Args, "plan_id");
        }

        public void WriteTo(Dictionary<string, object> journal)
        {
            journal["agent_id"] = AgentId;
            journal["session_id"] = SessionId;
            journal["request_id"] = RequestId;
            journal["trace_id"] = TraceId;
            if (JobId != null) journal["job_id"] = JobId;
            if (TransactionId != null) journal["transaction_id"] = TransactionId;
        }

        /// <summary>null si la sesión no tiene alias: un agente sin nombre no tiene identidad de agente.</summary>
        public static string AgentIdOf(string principal, string alias)
        {
            if (string.IsNullOrEmpty(alias)) return null;
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes((principal ?? "").ToLowerInvariant() + "\n" + alias.Trim().ToLowerInvariant()));
                return "A-" + BitConverter.ToString(h, 0, 5).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string NewTraceId() => Guid.NewGuid().ToString("N").Substring(0, 16);

        /// <summary>Traza aceptable: 8–64 caracteres [A-Za-z0-9-_.:]. Lo demás se ignora y se genera una nueva.</summary>
        public static string ValidTrace(string t)
        {
            if (string.IsNullOrEmpty(t) || t.Length < 8 || t.Length > 64) return null;
            foreach (var c in t)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_' && c != '.' && c != ':') return null;
            return t;
        }
    }

    /// <summary>
    /// Issue #3: qué operaciones exigen un agente con nombre. Las de lectura y el registro quedan libres para que un agente sin nombre
    /// pueda orientarse y registrarse; todo lo que cambia el modelo, la interfaz, ejecuta código o autoriza tareas exige alias.
    /// Una operación desconocida exige alias (fail-closed). El alias es declarado, no autenticación: el principal sigue siendo el
    /// usuario de Windows que admite la ACL de la tubería.
    /// </summary>
    public static class IdentityPolicy
    {
        // Revisión del coordinador (PR #22): con identidad_obligatoria=true un agente sin nombre NO lee ni modifica nada. Antes del registro sólo
        // quedan el saludo, el latido y el propio registro (ctx agente=…). Un ctx sin agente= devolvería datos del modelo: también exige nombre.
        // Única política, aplicada en PipeHost antes de despachar: ningún comando la reimplementa por su cuenta.
        private static readonly HashSet<string> Handshake = new HashSet<string>(StringComparer.Ordinal) { Wire.OpHello, Wire.OpPing };

        /// <summary>¿La petición exige un agente con nombre? Desconocida = sí (fail-closed).</summary>
        public static bool RequiresIdentity(Request req)
        {
            if (req?.Op == null) return true;
            if (Handshake.Contains(req.Op)) return false;
            return !IsRegistration(req);
        }

        /// <summary>Registro de identidad: ctx con agente= (o desregistrar=true, que sólo quita el nombre).</summary>
        public static bool IsRegistration(Request req) =>
            req.Op == Wire.OpContext && req.Args != null &&
            (!string.IsNullOrWhiteSpace(Arg.GetString(req.Args, "agente")) || Arg.GetBool(req.Args, "desregistrar") == true);

        public static string RefusalMessage(Session s) =>
            "esta sesión no tiene nombre de agente" + (s?.IdentityNote != null ? " (" + s.IdentityNote + ")" : "") +
            ": regístrese con revit ctx agente=\"Nombre\" o configure BIMBRIDGE_AGENTE en el puente. Sin nombre no se permiten lecturas ni cambios.";
    }
}
