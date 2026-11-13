// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// 0.9.1 (Bloque I): historial de operaciones largas terminadas, persistente y consultable (filtros, paginación) para el panel (#5)
    /// y para los agentes. Cada final queda en un JSONL (una línea, append) y en un anillo en memoria; al arrancar se recargan las últimas
    /// <see cref="MaxInMemory"/>. Incluye el consejo de reintento: nunca se aconseja repetir lo que pudo haber quedado aplicado.
    /// </summary>
    public sealed class OperationHistory
    {
        public const int MaxInMemory = 500;
        public const long MaxFileBytes = 4 * 1024 * 1024;

        public sealed class Record
        {
            public string JobId, Kind, Session, Agent, Task, Estado, Error;
            public DateTime CreatedUtc, FinishedUtc;
            public long Ms;
            public string Retry;

            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["job_id"] = JobId, ["tipo"] = Kind, ["sesion"] = Session, ["agente"] = Agent, ["tarea"] = Task, ["estado"] = Estado, ["error"] = Error,
                ["inicio"] = CreatedUtc.ToString("o", CultureInfo.InvariantCulture), ["fin"] = FinishedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["ms"] = Ms, ["reintento"] = Retry,
            };

            public static Record FromJson(Dictionary<string, object> d) => new Record
            {
                JobId = Arg.GetString(d, "job_id"), Kind = Arg.GetString(d, "tipo"), Session = Arg.GetString(d, "sesion"), Agent = Arg.GetString(d, "agente"), Task = Arg.GetString(d, "tarea"),
                Estado = Arg.GetString(d, "estado"), Error = Arg.GetString(d, "error"), Retry = Arg.GetString(d, "reintento"), Ms = Arg.GetLong(d, "ms") ?? 0,
                CreatedUtc = DateTime.Parse(Arg.GetString(d, "inicio"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                FinishedUtc = DateTime.Parse(Arg.GetString(d, "fin"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            };
        }

        /// <summary>
        /// Consejo de reintento por estado final. "reintentable": no se aplicó nada (rechazo, conflicto, validación, ocupado, vencido).
        /// "reconciliar_antes": el resultado es incierto o pudo quedar aplicado en parte. "no_necesario": terminó bien.
        /// "revisar_diferencias" (AUD-14): se aplicó, pero la verificación no coincide con lo pedido; hay que revisar o reconciliar las
        /// diferencias antes de darlo por bueno, y NO repetir la operación (ya está aplicada).
        /// </summary>
        public static string RetryAdvice(string estado)
        {
            switch (estado)
            {
                case Status.Ok: return "no_necesario";
                case Status.ConDiferencias: return "revisar_diferencias";
                case Status.Rechazado: case Status.Conflicto: case Status.Invalido: case Status.Vencido: case Status.Revertido: return "reintentable";
                case Status.Cancelado: return "reconciliar_antes";   // pudo cancelarse a mitad
                default: return "reconciliar_antes";
            }
        }

        private readonly string _path;
        private readonly object _lock = new object();
        private readonly LinkedList<Record> _ring = new LinkedList<Record>();
        public readonly List<string> LoadErrors = new List<string>();

        public OperationHistory(string path)
        {
            _path = path;
            if (path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path)) return;
            var n = 0;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                n++;
                if (line.Trim().Length == 0) continue;
                try { Push(Record.FromJson(Json.ParseObject(line))); }
                catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is InvalidOperationException)
                { if (LoadErrors.Count < 20) LoadErrors.Add("línea " + n + ": " + ex.GetType().Name); }
            }
        }

        private void Push(Record r)
        {
            _ring.AddFirst(r);
            while (_ring.Count > MaxInMemory) _ring.RemoveLast();
        }

        public Record Add(Job j)
        {
            var r = new Record
            {
                JobId = j.Id, Kind = j.Kind, Session = j.SessionId, Agent = j.AgentId, Task = j.Task, Estado = j.Estado, Error = j.Error,
                CreatedUtc = j.CreatedUtc, FinishedUtc = j.FinishedUtc ?? DateTime.UtcNow,
            };
            r.Ms = (long)(r.FinishedUtc - r.CreatedUtc).TotalMilliseconds;
            r.Retry = RetryAdvice(r.Estado);
            lock (_lock)
            {
                Push(r);
                if (_path != null)
                {
                    // Rotación simple: al superar el tamaño, se conserva el anillo en memoria y se reescribe (las más antiguas se pierden a propósito).
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaxFileBytes)
                        Home.WriteAtomic(_path, string.Join("\n", _ring.Reverse().Select(x => Json.Write(x.ToJson()))) + "\n");
                    else
                        using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        {
                            var b = Encoding.UTF8.GetBytes(Json.Write(r.ToJson()) + "\n");
                            fs.Write(b, 0, b.Length);
                        }
                }
            }
            return r;
        }

        /// <summary>
        /// AUD-13: quién pregunta. Un registro es visible si lo lanzó la MISMA sesión o el MISMO agente con nombre (la sesión cambia al
        /// reconectar; el agente —principal de Windows + alias— no). El nombre de tarea (tarea=) NO da visibilidad: lo declara el
        /// cliente y otro podría adivinarlo. Ver lo de otros exige <see cref="SeeAll"/>, que sólo se activa con la autorización
        /// explícita del usuario (config historial_compartido). Sin identidad alguna no se ve nada.
        /// </summary>
        public sealed class Scope
        {
            public string SessionId, AgentId;
            public bool SeeAll;

            public bool CanSee(Record r)
            {
                if (SeeAll) return true;
                if (!string.IsNullOrEmpty(SessionId) && string.Equals(r.Session, SessionId, StringComparison.Ordinal)) return true;
                if (!string.IsNullOrEmpty(AgentId) && string.Equals(r.Agent, AgentId, StringComparison.Ordinal)) return true;
                return false;
            }
        }

        /// <summary>
        /// Ámbito de una consulta. Pedir «todos» sin la autorización del usuario (<paramref name="sharedAuthorized"/>) se RECHAZA
        /// (devuelve null y el motivo): no se degrada en silencio a «lo propio» ni se concede por pedirlo.
        /// </summary>
        public static Scope ScopeFor(string sessionId, string agentId, bool wantAll, bool sharedAuthorized, out string refusal)
        {
            refusal = null;
            if (wantAll && !sharedAuthorized)
            {
                refusal = "ver operaciones de otras sesiones exige que el usuario lo autorice (config historial_compartido=true). Sin eso sólo se ve lo propio.";
                return null;
            }
            return new Scope { SessionId = sessionId, AgentId = agentId, SeeAll = wantAll };
        }

        /// <summary>Consulta con ámbito obligatorio, filtros (cualquiera puede ser null) y paginación; más reciente primero.
        /// Los filtros sólo estrechan lo visible: pedir sesion= de otro no lo muestra.</summary>
        public (List<Record> Page, int Total) Query(Scope scope, string estado, string tipo, string sesion, DateTime? desde, string texto, int page = 1, int pageSize = 20)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            List<Record> all;
            lock (_lock) all = _ring.ToList();
            IEnumerable<Record> q = all.Where(scope.CanSee);
            if (!string.IsNullOrWhiteSpace(estado)) q = q.Where(r => string.Equals(r.Estado, estado.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(tipo)) q = q.Where(r => (r.Kind ?? "").IndexOf(tipo.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(sesion)) q = q.Where(r => string.Equals(r.Session, sesion.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(r.Task, sesion.Trim(), StringComparison.OrdinalIgnoreCase));
            if (desde.HasValue) q = q.Where(r => r.FinishedUtc >= desde.Value);
            if (!string.IsNullOrWhiteSpace(texto)) q = q.Where(r => ((r.Kind ?? "") + " " + (r.Error ?? "") + " " + r.JobId).IndexOf(texto.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            var list = q.ToList();
            var size = Math.Max(1, Math.Min(100, pageSize));
            return (list.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), list.Count);
        }

        public int Count { get { lock (_lock) return _ring.Count; } }
    }
}
