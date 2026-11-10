// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Text;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Una sesión = una conexión de un puente (Claude Code, Codex…). Es lo que el operador llamó "puerto".
    /// La IA nunca ve este identificador; sólo lo usa el add-in para la cola, los conflictos y el diario.
    /// </summary>
    public sealed class Session
    {
        public string Id { get; internal set; }
        public string ClientName { get; internal set; }
        public int ClientPid { get; internal set; }
        public DateTime ConnectedUtc { get; internal set; }
        public DateTime LastSeenUtc { get; internal set; }

        // ---- Identidad nominal (revisión consolidada). Ver docs/0.9.0/AGENT_IDENTITY_PROTOCOL.md ----
        /// <summary>Alias del agente ("Integration client"). Declarado: nunca se infiere ni se hereda de otra sesión. Null = sin nombre.</summary>
        public string AgentName { get; internal set; }
        public string ClientVersion { get; internal set; }
        public string Conversation { get; internal set; }
        public string Model { get; internal set; }
        /// <summary>De dónde salió el nombre: configuracion (variable del puente), registro (ctx agente=), hello o ninguno.</summary>
        public string IdentitySource { get; internal set; } = AgentIdentity.SourceNone;
        /// <summary>Usuario de Windows: verificado por la ACL de la tubería (sólo ese usuario puede conectarse). El alias NO está verificado.</summary>
        public string Principal { get; internal set; }
        /// <summary>Issue #3: por qué la sesión no tiene nombre (p. ej. alias en uso por otra sesión). Null si no hay nada que decir.</summary>
        public string IdentityNote { get; internal set; }
        /// <summary>Issue #4: huella estable del agente (principal + alias). Null sin alias.</summary>
        public string AgentId => Correlation.AgentIdOf(Principal, AgentName);

        // ---- Contadores (los escribe PipeHost; se leen desde la ventana sin bloquear Revit) ----
        internal long _requests, _errors, _active, _totalMs;
        public long Requests => System.Threading.Interlocked.Read(ref _requests);
        public long Errors => System.Threading.Interlocked.Read(ref _errors);
        public long Active => System.Threading.Interlocked.Read(ref _active);
        public long TotalMs => System.Threading.Interlocked.Read(ref _totalMs);
        public string LastOp { get; internal set; }
        public string LastDoc { get; internal set; }

        public string DisplayName => AgentName ?? "(sin nombre) · " + ClientName;

        /// <summary>Conjuntos con nombre ($l5_perimetro): ids guardados por esta sesión.</summary>
        public NamedSets Sets { get; } = new NamedSets();

        public override string ToString() => Id;
    }

    /// <summary>Reglas de identidad de agentes: validación del alias y estado de presencia comprobable.</summary>
    public static class AgentIdentity
    {
        public const string SourceNone = "ninguno";
        public const string SourceConfig = "configuracion";
        public const string SourceRegister = "registro";
        public const string SourceHello = "hello";
        public const int MaxName = 80;

        /// <summary>Alias aceptable: 1..80 caracteres imprimibles, sin saltos de línea. Devuelve null si no vale.</summary>
        public static string Clean(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var sb = new StringBuilder();
            foreach (var c in name.Trim()) if (!char.IsControl(c)) sb.Append(c);
            var s = sb.ToString().Trim();
            if (s.Length == 0) return null;
            return s.Length > MaxName ? s.Substring(0, MaxName) : s;
        }

        /// <summary>Estado de una sesión abierta: ejecutando, activo (petición en los últimos 60 s) o inactivo. Nunca "conectado" por inferencia.</summary>
        public static string Presence(Session s, DateTime now) =>
            s.Active > 0 ? "ejecutando" : (now - s.LastSeenUtc).TotalSeconds <= 60 ? "activo" : "inactivo";
    }

    /// <summary>
    /// Ciclo de vida de la sesión frente al umbral de inactividad (issue #1). La sesión lógica no muere por la duración de una solicitud:
    /// sólo expira por inactividad cuando no tiene solicitudes en curso. Una conexión rota (EOF/IOException) se cierra siempre; lo que
    /// estuviera escribiendo conserva su propia evidencia (intención durable, estado desconocido, reconciliación) y nunca se reintenta solo.
    /// </summary>
    public static class SessionLifecycle
    {
        public static bool ShouldExpireOnIdle(long activeRequests) => activeRequests <= 0;
    }

    public enum RegisterResult { Ok, Invalid, Duplicate }

    /// <summary>Sesión ya cerrada, para el historial del panel (acotado).</summary>
    public sealed class ClosedSession
    {
        public string Id, AgentName, ClientName, IdentitySource, Reason;
        public DateTime ConnectedUtc, ClosedUtc;
        public long Requests, Errors;
    }

    public sealed class SessionRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private readonly Func<DateTime> _now;

        public SessionRegistry(Func<DateTime> now = null) { _now = now ?? (() => DateTime.UtcNow); }

        public Session Open(string clientName, int clientPid)
        {
            lock (_lock)
            {
                // Revisión PR #22: identificador único por INSTANCIA de sesión. Antes "S-cliente-pid" se reutilizaba al reconectar con el mismo
                // pid y, como request_id = session_id#contador y el contador reinicia, dos solicitudes distintas podían compartir request_id.
                // Se conserva el prefijo legible (cliente y pid, útil en el diario) y se añade un sufijo aleatorio de 64 bits.
                var baseId = "S-" + Slug(clientName) + "-" + (clientPid > 0 ? clientPid.ToString() : "0");
                string id;
                do id = baseId + "-" + NewInstanceSuffix();
                while (_sessions.ContainsKey(id) || _issued.Contains(id));
                _issued.Add(id);
                var t = _now();
                var s = new Session
                {
                    Id = id,
                    ClientName = string.IsNullOrWhiteSpace(clientName) ? "cliente" : clientName.Trim(),
                    ClientPid = clientPid,
                    ConnectedUtc = t,
                    LastSeenUtc = t,
                };
                _sessions[id] = s;
                return s;
            }
        }

        public void Touch(Session s)
        {
            lock (_lock) s.LastSeenUtc = _now();
        }

        /// <summary>Datos del saludo: versión del cliente y, si el puente lo trae configurado, el alias del agente.</summary>
        public void Describe(Session s, string clientVersion, string agent, string conversation, string model, string source, string principal)
        {
            lock (_lock)
            {
                s.ClientVersion = AgentIdentity.Clean(clientVersion);
                s.Principal = principal;
                var a = AgentIdentity.Clean(agent);
                if (a != null)
                {
                    var holder = HolderOf(s, a);
                    if (holder == null) { s.AgentName = a; s.IdentitySource = source ?? AgentIdentity.SourceHello; s.IdentityNote = null; }
                    else s.IdentityNote = "el alias \"" + a + "\" ya lo usa la sesión activa " + holder.Id;
                }
                s.Conversation = AgentIdentity.Clean(conversation);
                s.Model = AgentIdentity.Clean(model);
            }
        }

        /// <summary>Registro explícito (ctx agente=…). Sólo cambia ESTA sesión. Devuelve false si el alias no es válido o está en uso.</summary>
        public bool Register(Session s, string agent, string conversation, string model) =>
            TryRegister(s, agent, conversation, model, out _) == RegisterResult.Ok;

        /// <summary>
        /// Issue #3: registro con detección de duplicados. Un alias que ya usa OTRA sesión abierta de otro proceso cliente se rechaza
        /// (evita suplantación accidental y reasignación indebida). La misma instancia del puente (mismo cliente y pid) que reconecta
        /// mientras su sesión anterior aún no se cerró sí puede reutilizarlo.
        /// </summary>
        public RegisterResult TryRegister(Session s, string agent, string conversation, string model, out Session holder)
        {
            holder = null;
            var a = AgentIdentity.Clean(agent);
            if (a == null) return RegisterResult.Invalid;
            lock (_lock)
            {
                holder = HolderOf(s, a);
                if (holder != null) return RegisterResult.Duplicate;
                s.AgentName = a;
                s.IdentitySource = AgentIdentity.SourceRegister;
                s.IdentityNote = null;
                var c = AgentIdentity.Clean(conversation); if (c != null) s.Conversation = c;
                var m = AgentIdentity.Clean(model); if (m != null) s.Model = m;
            }
            return RegisterResult.Ok;
        }

        /// <summary>Desregistro (ctx desregistrar=true): la sesión vuelve a "sin nombre" y pierde lecturas y cambios hasta registrarse de nuevo.</summary>
        public void Unregister(Session s)
        {
            lock (_lock)
            {
                s.AgentName = null;
                s.IdentitySource = AgentIdentity.SourceNone;
                s.IdentityNote = "desregistrada";
            }
        }

        /// <summary>Otra sesión abierta que ya usa ese alias (ignorando mayúsculas), salvo la misma instancia del cliente que reconecta.</summary>
        private Session HolderOf(Session s, string alias)
        {
            foreach (var o in _sessions.Values)
            {
                if (ReferenceEquals(o, s) || !string.Equals(o.AgentName, alias, StringComparison.OrdinalIgnoreCase)) continue;
                var sameInstance = o.ClientPid > 0 && o.ClientPid == s.ClientPid && string.Equals(o.ClientName, s.ClientName, StringComparison.Ordinal)
                                   && string.Equals(o.Principal, s.Principal, StringComparison.OrdinalIgnoreCase);
                if (!sameInstance) return o;
            }
            return null;
        }

        public void RequestStarted(Session s, string op, string doc)
        {
            System.Threading.Interlocked.Increment(ref s._active);
            lock (_lock) { s.LastOp = op; if (!string.IsNullOrEmpty(doc)) s.LastDoc = doc; }
        }

        public void RequestFinished(Session s, long ms, bool error)
        {
            System.Threading.Interlocked.Decrement(ref s._active);
            System.Threading.Interlocked.Increment(ref s._requests);
            System.Threading.Interlocked.Add(ref s._totalMs, ms);
            if (error) System.Threading.Interlocked.Increment(ref s._errors);
        }

        private readonly LinkedList<ClosedSession> _closed = new LinkedList<ClosedSession>();
        public const int ClosedMax = 50;

        public bool Close(Session s, string reason = "desconectado")
        {
            lock (_lock)
            {
                if (s == null || !_sessions.Remove(s.Id)) return false;
                _closed.AddLast(new ClosedSession
                {
                    Id = s.Id, AgentName = s.AgentName, ClientName = s.ClientName, IdentitySource = s.IdentitySource, Reason = reason,
                    ConnectedUtc = s.ConnectedUtc, ClosedUtc = _now(), Requests = s.Requests, Errors = s.Errors,
                });
                if (_closed.Count > ClosedMax) _closed.RemoveFirst();
                return true;
            }
        }

        /// <summary>Sesiones cerradas en esta ejecución de Revit (las más recientes al final). Un reinicio las borra: nada de agentes fantasma.</summary>
        public List<ClosedSession> Closed()
        {
            lock (_lock) return new List<ClosedSession>(_closed);
        }

        public List<Session> All()
        {
            lock (_lock) return new List<Session>(_sessions.Values);
        }

        public int Count { get { lock (_lock) return _sessions.Count; } }

        /// <summary>Ids emitidos en esta ejecución (abiertos o cerrados): nunca se vuelve a emitir uno.</summary>
        private readonly HashSet<string> _issued = new HashSet<string>(StringComparer.Ordinal);

        private static string NewInstanceSuffix()
        {
            var b = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        internal static string Slug(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "cliente";
            var sb = new StringBuilder();
            foreach (var c in name.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 24) break;
            }
            var s = sb.ToString().Trim('-');
            return s.Length == 0 ? "cliente" : s;
        }
    }

    /// <summary>
    /// Conjuntos de ids con nombre, por sesión. Evitan reenviar cientos de ids en cada llamada.
    /// Desde 0.5.0 cada conjunto queda atado al documento donde se calculó (handle dN): un conjunto de A nunca resuelve elementos de B.
    /// </summary>
    public sealed class NamedSets
    {
        private sealed class Entry
        {
            public long[] Ids;
            public string Doc;
            public string DocTitle;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _sets = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Límite de conjuntos por sesión (los más antiguos no se borran solos: se avisa).</summary>
        public const int MaxSets = 256;

        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("nombre de conjunto vacío");
            var n = name.Trim();
            if (!n.StartsWith("$")) n = "$" + n;
            for (var i = 1; i < n.Length; i++)
            {
                var c = n[i];
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                    throw new ArgumentException("nombre de conjunto inválido: use letras, números, _ o -");
            }
            if (n.Length < 2 || n.Length > 48) throw new ArgumentException("nombre de conjunto de 1 a 47 caracteres");
            return n;
        }

        /// <param name="doc">Handle del documento (dN) donde valen esos ids; null = sin documento (compatibilidad).</param>
        public void Save(string name, IEnumerable<long> ids, string doc = null, string docTitle = null)
        {
            var key = Normalize(name);
            var arr = new List<long>(ids).ToArray();
            lock (_lock)
            {
                if (!_sets.ContainsKey(key) && _sets.Count >= MaxSets)
                    throw new InvalidOperationException("demasiados conjuntos (" + MaxSets + "); borre alguno con sets drop");
                _sets[key] = new Entry { Ids = arr, Doc = doc, DocTitle = docTitle };
            }
        }

        /// <summary>Sin comprobar documento (sólo para listados y compatibilidad).</summary>
        public bool TryGet(string name, out long[] ids)
        {
            lock (_lock)
            {
                if (_sets.TryGetValue(Normalize(name), out var e)) { ids = e.Ids; return true; }
                ids = null;
                return false;
            }
        }

        /// <summary>
        /// Resuelve el conjunto para el documento <paramref name="doc"/>. Si se guardó en otro documento devuelve false con el motivo.
        /// </summary>
        public bool TryGet(string name, string doc, out long[] ids, out string error)
        {
            ids = null;
            error = null;
            lock (_lock)
            {
                if (!_sets.TryGetValue(Normalize(name), out var e))
                {
                    error = "no existe el conjunto " + name + "; use sets para ver los guardados";
                    return false;
                }
                if (doc != null && e.Doc != null && !string.Equals(doc, e.Doc, StringComparison.OrdinalIgnoreCase))
                {
                    error = "el conjunto " + Normalize(name) + " es del documento " + e.Doc + (e.DocTitle != null ? " (" + e.DocTitle + ")" : "") +
                            ", no de " + doc + ": sus ids no valen aquí. Vuelva a consultarlo en este documento o pase doc=" + e.Doc;
                    return false;
                }
                ids = e.Ids;
                return true;
            }
        }

        /// <summary>Documento al que pertenece el conjunto (null si no consta).</summary>
        public string DocOf(string name)
        {
            lock (_lock) return _sets.TryGetValue(Normalize(name), out var e) ? e.Doc : null;
        }

        public bool Remove(string name)
        {
            lock (_lock) return _sets.Remove(Normalize(name));
        }

        /// <summary>Borra los conjuntos de un documento que se cerró.</summary>
        public int RemoveDoc(string doc)
        {
            lock (_lock)
            {
                var gone = new List<string>();
                foreach (var kv in _sets) if (doc != null && string.Equals(kv.Value.Doc, doc, StringComparison.OrdinalIgnoreCase)) gone.Add(kv.Key);
                foreach (var k in gone) _sets.Remove(k);
                return gone.Count;
            }
        }

        public Dictionary<string, int> List()
        {
            lock (_lock)
            {
                var d = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var kv in _sets) d[kv.Key] = kv.Value.Ids.Length;
                return d;
            }
        }

        /// <summary>Nombre → (cantidad, documento).</summary>
        public List<object[]> Describe()
        {
            lock (_lock)
            {
                var rows = new List<object[]>();
                foreach (var kv in _sets) rows.Add(new object[] { kv.Key, (long)kv.Value.Ids.Length, kv.Value.Doc ?? "" });
                return rows;
            }
        }

        public void Clear() { lock (_lock) _sets.Clear(); }
    }
}
