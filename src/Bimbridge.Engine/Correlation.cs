// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Issue #4: correlation identifiers of a request. They are set in PipeHost and travel through AsyncLocal (not through the arguments, so
    /// idempotency fingerprints and validations are not altered). The journal records them on every line.
    ///   agent_id       = stable fingerprint of (Windows principal, alias). Same on reconnect; different if the alias or the user changes.
    ///   session_id     = logical session instance (S-client-pid-&lt;64 random bits&gt;): never reused, not even when reconnecting with the same pid.
    ///   request_id     = session_id + "#" + id of the request on that connection (unique within the Revit run).
    ///   trace_id       = the one the client sends in _traza (AgentHost, bridge) or a new one; links processes.
    ///   job_id         = background job, if the response carries one.
    ///   transaction_id = id of the applied plan (name of the TransactionGroup in Revit), if the operation is a commit.
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

        /// <summary>Correlation of the current request (null outside a request).</summary>
        public static Correlation Current
        {
            get => _current.Value;
            internal set => _current.Value = value;
        }

        /// <summary>Creates the correlation and removes _traza from the arguments (it is transport, not content).</summary>
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

        /// <summary>Fills job_id and transaction_id with what is known at the end.</summary>
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

        /// <summary>null if the session has no alias: an unnamed agent has no agent identity.</summary>
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

        /// <summary>Acceptable trace: 8–64 characters [A-Za-z0-9-_.:]. Anything else is ignored and a new one is generated.</summary>
        public static string ValidTrace(string t)
        {
            if (string.IsNullOrEmpty(t) || t.Length < 8 || t.Length > 64) return null;
            foreach (var c in t)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_' && c != '.' && c != ':') return null;
            return t;
        }
    }

    /// <summary>
    /// Issue #3: which operations require a named agent: all except hello, ping and the registration itself (reads included).
    /// An unknown operation requires an alias (fail-closed). The alias is declared, not authentication: the principal is still the
    /// Windows user that the pipe ACL admits.
    /// </summary>
    public static class IdentityPolicy
    {
        // Coordinator review (PR #22): with identidad_obligatoria=true an unnamed agent does NOT read or modify anything. Before registration only
        // hello, ping and the registration itself (ctx agente=…) remain. A ctx without agente= would return model data: it also requires a name.
        // Single policy, applied in PipeHost before dispatch: no command reimplements it on its own.
        private static readonly HashSet<string> Handshake = new HashSet<string>(StringComparer.Ordinal) { Wire.OpHello, Wire.OpPing };

        /// <summary>Does the request require a named agent? Unknown = yes (fail-closed).</summary>
        public static bool RequiresIdentity(Request req)
        {
            if (req?.Op == null) return true;
            if (Handshake.Contains(req.Op)) return false;
            return !IsRegistration(req);
        }

        /// <summary>Identity registration: ctx with agente= (or desregistrar=true, which only removes the name).</summary>
        public static bool IsRegistration(Request req) =>
            req.Op == Wire.OpContext && req.Args != null &&
            (!string.IsNullOrWhiteSpace(Arg.GetString(req.Args, "agente")) || Arg.GetBool(req.Args, "desregistrar") == true);

        public static string RefusalMessage(Session s) =>
            "this session has no agent name" + (s?.IdentityNote != null ? " (" + s.IdentityNote + ")" : "") +
            ": register with revit ctx agente=\"Name\" or set BIMBRIDGE_AGENT in the bridge. Without a name no reads or changes are allowed.";
    }
}
