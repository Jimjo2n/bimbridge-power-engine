// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Autoridad de capacidades independiente del agente (0.6.1, R03).
    ///
    /// Cuatro conjuntos distintos:
    /// 1. solicitadas: lo que el agente pide (argumento MCP <c>capacidades=</c>). NUNCA autoriza nada por sí solo.
    /// 2. autorizadas: concesiones en <see cref="GrantStore"/>, creadas sólo por una persona desde la interfaz de Revit
    ///    (no hay operación MCP que las cree), firmadas con HMAC, ligadas a tarea + documento (+ huella) + capacidades + ámbito + vencimiento.
    /// 3. usadas: lo que el análisis de Roslyn encontró en el código y las barreras de la tarea.
    /// 4. rechazadas: usadas o necesarias que no tienen concesión vigente.
    ///
    /// Se comprueba al iniciar Y en el punto de ejecución (antes de cada lote, justo antes de cada unidad y antes de cada barrera),
    /// así una revocación o un vencimiento cortan la tarea en la siguiente unidad.
    ///
    /// Límite declarado: la clave HMAC vive en el perfil del usuario. Un proceso del mismo usuario que lea esa clave (o código
    /// TrustedInteractive dentro de Revit) puede fabricar concesiones. Protege contra el agente que sólo habla MCP, no contra el usuario local.
    /// </summary>
    public static class Capability
    {
        /// <summary>Modificar el modelo (transacciones de la receta).</summary>
        public const string Modificar = "revit_modificar";
        public const string Guardar = "revit_guardar";
        public const string Sync = "revit_sync";
        public const string Exportar = "revit_exportar";
        public const string ArchivoEscritura = "archivo_escritura";
        /// <summary>Reconciliar como NO aplicada una unidad dudosa (provoca que se vuelva a ejecutar).</summary>
        public const string ReconciliarNoAplicada = "reconciliar_no_aplicada";
        /// <summary>Correr de forma autónoma una receta que NO es de las instaladas (archivo del usuario, codigo= en línea, script guardado).
        /// La concesión liga tarea + documento, y la tarea liga el SHA-256 exacto de la receta (huella de contenido): aprobar es aprobar ESE código.</summary>
        public const string RecetaExterna = "receta_externa";
        /// <summary>Referencias DLL fuera de la lista de confianza.</summary>
        public const string ReferenciaExterna = "referencia_externa";

        /// <summary>Lo único que corre sin concesión: lectura del modelo y artefactos de la propia tarea.</summary>
        public static readonly string[] LowRisk = { };

        public static readonly string[] Grantable = { Modificar, Guardar, Sync, Exportar, ArchivoEscritura, ReconciliarNoAplicada, RecetaExterna };

        public static string ForBarrier(string barrier)
        {
            switch (barrier)
            {
                case "guardar": return Guardar;
                case "sync": return Sync;
                case "pdf": return Exportar;
                default: return Exportar; // barreras declaradas por la receta producen archivos fuera del modelo
            }
        }
    }

    public sealed class Grant
    {
        public string Id;
        public string Task;
        public string DocKey;
        /// <summary>Huella del modelo (detecta otro archivo en la misma ruta). Vacía = cualquier huella de esa clave.</summary>
        public string DocPrint;
        public List<string> Capabilities = new List<string>();
        /// <summary>Ámbito opcional de elementos (UniqueId o id); vacío = la tarea entera.</summary>
        public List<string> Scope = new List<string>();
        public string Created;
        public string Expires;
        public string ApprovedBy;
        public string Revoked;
        public string Signature;

        public Dictionary<string, object> ToJson(bool withSignature = true)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id, ["tarea"] = Task, ["doc"] = DocKey, ["huella_doc"] = DocPrint ?? "",
                ["capacidades"] = Capabilities.OrderBy(x => x, StringComparer.Ordinal).Cast<object>().ToList(),
                ["ambito"] = Scope.Cast<object>().ToList(),
                ["creada"] = Created, ["vence"] = Expires, ["aprobada_por"] = ApprovedBy, ["revocada"] = Revoked ?? "",
            };
            if (withSignature) d["firma"] = Signature;
            return d;
        }

        public static Grant FromJson(Dictionary<string, object> o) => new Grant
        {
            Id = Arg.GetString(o, "id"), Task = Arg.GetString(o, "tarea"), DocKey = Arg.GetString(o, "doc"), DocPrint = Arg.GetString(o, "huella_doc"),
            Capabilities = Arg.GetStringList(o, "capacidades").ToList(), Scope = Arg.GetStringList(o, "ambito").ToList(),
            Created = Arg.GetString(o, "creada"), Expires = Arg.GetString(o, "vence"), ApprovedBy = Arg.GetString(o, "aprobada_por"),
            Revoked = string.IsNullOrEmpty(Arg.GetString(o, "revocada")) ? null : Arg.GetString(o, "revocada"), Signature = Arg.GetString(o, "firma"),
        };

        public bool ValidAt(DateTime now) =>
            Revoked == null && DateTime.TryParse(Expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var e) && e > now;
    }

    /// <summary>Solicitud de un agente pendiente de una persona.</summary>
    public sealed class GrantRequest
    {
        public string Id, Task, DocKey, DocPrint, DocTitle, Client, Created, Objective;
        public List<string> Capabilities = new List<string>();
    }

    /// <summary>Almacén de concesiones (%LOCALAPPDATA%\PDI.RevitMcp\autorizaciones). Escribe sólo <see cref="Approve"/>/<see cref="Revoke"/>,
    /// que el add-in llama desde un diálogo de Revit que responde una persona.</summary>
    public sealed class GrantStore
    {
        private readonly string _dir;
        private readonly byte[] _key;
        private readonly object _lock = new object();

        public GrantStore(string dir = null)
        {
            _dir = dir ?? Home.Sub("autorizaciones");
            Directory.CreateDirectory(_dir);
            Directory.CreateDirectory(Path.Combine(_dir, "solicitudes"));
            _key = LoadOrCreateKey(Path.Combine(_dir, "clave.bin"));
        }

        public string Dir => _dir;

        /// <summary>Cabecera del archivo de clave desde rc3. El contenido es la clave en Base64: Base64 es codificación, NO cifrado;
        /// la protección es la ACL del perfil del usuario (DPAPI queda para 0.7.0, ver DECISIONES_0.6.1.md).</summary>
        public const string KeyHeader = "PDI-GRANT-KEY v1 base64";
        public const int KeyBytes = 32;

        /// <summary>Error de la clave de concesiones. El add-in lo trata como "sin autoridad" (se deniegan escrituras), nunca regenera.</summary>
        public sealed class GrantKeyException : Exception { public GrantKeyException(string m) : base(m) { } }

        /// <summary>
        /// Política (rc3):
        /// - existe y es válida (formato v1 o el Base64 suelto de rc2) → se usa;
        /// - existe pero inválida/truncada → GrantKeyException (no se regenera: invalidaría o enmascararía concesiones);
        /// - no existe y hay concesiones firmadas → GrantKeyException (alguien la borró; una persona decide);
        /// - no existe y no hay concesiones → se crea de forma atómica (temporal + Move sin sobrescribir): si otro proceso gana la carrera,
        ///   se lee la suya. Nunca quedan dos claves.
        /// </summary>
        public static byte[] LoadOrCreateKey(string path)
        {
            if (File.Exists(path)) return ReadKey(path);
            var dir = Path.GetDirectoryName(path);
            // alpha3 (P1-A): causa raíz de la carrera de rc3: en Unix File.Move(sin sobrescribir) de .NET comprueba el destino y luego
            // llama a rename(), que SÍ sobrescribe; dos creadores podían "ganar" y quedar con claves distintas. Ahora la creación
            // se serializa con un candado exclusivo (FileShare.None; el SO lo libera si el proceso muere) y se relee dentro del candado.
            var lockPath = path + ".lock";
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                FileStream gate = null;
                try { gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(5); continue; }
                catch (IOException ex) { throw new GrantKeyException("no se pudo obtener el candado de la clave de concesiones: " + ex.Message); }
                using (gate)
                {
                    if (File.Exists(path)) return ReadKey(path);
                    if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "c_*.json").Any())
                        throw new GrantKeyException("falta " + path + " pero hay concesiones firmadas con ella: no se crea una clave nueva. " +
                                                    "Restaure la clave o borre las concesiones a mano (decisión de una persona).");
                    var key = new byte[KeyBytes];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
                    var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                    // Un fallo al publicar es un error controlado (sin autoridad), no una excepción que tumbe al proceso.
                    try
                    {
                        File.WriteAllText(tmp, KeyHeader + "\n" + Convert.ToBase64String(key) + "\n", new UTF8Encoding(false));
                        File.Move(tmp, path);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new GrantKeyException("no se pudo crear la clave de concesiones: " + ex.GetType().Name + " (" + (ex.HResult & 0xFFFF) + ")");
                    }
                    finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } }
                    return ReadKey(path);   // devuelve lo que quedó en disco, nunca una copia en memoria distinta
                }
            }
        }

        /// <summary>Espera máxima ante una violación de compartición transitoria al leer la clave (issue #21).</summary>
        public static TimeSpan ReadRetryWindow = TimeSpan.FromSeconds(3);

        /// <summary>ERROR_SHARING_VIOLATION (32) o ERROR_LOCK_VIOLATION (33): otro handle tiene el archivo abierto sin compartir.</summary>
        private static bool IsSharingViolation(IOException ex)
        {
            var code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33;
        }

        /// <summary>
        /// Issue #21 (causa raíz): en Windows, un archivo recién publicado puede estar abierto un instante por otro proceso sin compartir
        /// lectura (antivirus o indexador, o un handle de otro proceso de prueba). File.ReadAllText fallaba con violación de compartición
        /// y eso se convertía en GrantKeyException. En la prueba multihilo, la excepción en un Thread suelto abortaba el proceso de pruebas.
        /// Ahora: lectura con FileShare.ReadWrite|Delete (no bloquea a otros lectores) y espera acotada SÓLO ante violación de compartición.
        /// Cualquier otro error, o la violación que persiste más allá de la ventana, sigue siendo GrantKeyException (fail-closed).
        /// Nunca se regenera la clave y el mensaje nunca incluye su contenido.
        /// </summary>
        private static byte[] ReadKey(string path)
        {
            string text = null;
            var deadline = DateTime.UtcNow + ReadRetryWindow;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(f, Encoding.UTF8)) text = r.ReadToEnd();
                    break;
                }
                catch (IOException ex) when (!(ex is FileNotFoundException) && IsSharingViolation(ex) && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(Math.Min(10 * (attempt + 1), 100));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new GrantKeyException("clave de concesiones ilegible: " + ex.GetType().Name + " (" + (ex.HResult & 0xFFFF) + ")");
                }
            }
            var lines = text.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count > 0 && lines[0] == KeyHeader) lines.RemoveAt(0);   // v1; sin cabecera = formato rc2 (Base64 suelto)
            if (lines.Count != 1) throw new GrantKeyException("clave de concesiones inválida o truncada en " + path + " (no se regenera)");
            byte[] key;
            try { key = Convert.FromBase64String(lines[0]); }
            catch (FormatException) { throw new GrantKeyException("clave de concesiones inválida (Base64 incorrecto) en " + path + " (no se regenera)"); }
            if (key.Length != KeyBytes) throw new GrantKeyException("clave de concesiones de " + key.Length + " bytes en " + path + " (se esperaban " + KeyBytes + "; no se regenera)");
            return key;
        }

        private string Sign(Grant g)
        {
            using (var h = new HMACSHA256(_key))
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(OpSemantics.Canonical(g.ToJson(withSignature: false))))).Replace("-", "").ToLowerInvariant();
        }

        private string GrantPath(string id) => Path.Combine(_dir, "c_" + id + ".json");

        /// <summary>Crea una concesión. Llamar SÓLO desde la interfaz de Revit tras la respuesta de una persona.</summary>
        public Grant Approve(string task, string docKey, string docPrint, IEnumerable<string> caps, TimeSpan validFor, string approvedBy, DateTime now, IEnumerable<string> scope = null)
        {
            var list = caps.Distinct(StringComparer.Ordinal).ToList();
            var bad = list.Where(c => !Capability.Grantable.Contains(c)).ToList();
            if (bad.Count > 0) throw new ArgumentException("capacidades no concedibles: " + string.Join(", ", bad));
            var g = new Grant
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12), Task = task, DocKey = docKey, DocPrint = docPrint ?? "",
                Capabilities = list, Scope = (scope ?? Enumerable.Empty<string>()).ToList(),
                Created = now.ToString("o", CultureInfo.InvariantCulture), Expires = (now + validFor).ToString("o", CultureInfo.InvariantCulture), ApprovedBy = approvedBy,
            };
            g.Signature = Sign(g);
            lock (_lock) Home.WriteAtomic(GrantPath(g.Id), Json.Write(g.ToJson()));
            return g;
        }

        public bool Revoke(string grantId, DateTime now)
        {
            lock (_lock)
            {
                var g = Load(GrantPath(grantId));
                if (g == null) return false;
                g.Revoked = now.ToString("o", CultureInfo.InvariantCulture);
                g.Signature = Sign(g);
                Home.WriteAtomic(GrantPath(g.Id), Json.Write(g.ToJson()));
                return true;
            }
        }

        public int RevokeTask(string task, DateTime now) => All().Where(g => g.Task == task && g.Revoked == null).Count(g => Revoke(g.Id, now));

        private Grant Load(string path)
        {
            try { return Grant.FromJson(Json.ParseObject(File.ReadAllText(path))); }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException) { return null; }
        }

        /// <summary>Concesiones con firma válida (las alteradas se ignoran).</summary>
        public List<Grant> All()
        {
            var res = new List<Grant>();
            foreach (var f in Directory.GetFiles(_dir, "c_*.json"))
            {
                var g = Load(f);
                if (g != null && g.Signature != null && FixedTimeEquals(g.Signature, Sign(g))) res.Add(g);
            }
            return res;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var d = 0;
            for (var i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        /// <summary>null si (tarea, documento, huella, capacidad, ámbito) tiene una concesión vigente; si no, el motivo.</summary>
        public string Check(string task, string docKey, string docPrint, string capability, DateTime now, IEnumerable<string> scope = null)
        {
            var any = All().Where(g => g.Task == task && g.Capabilities.Contains(capability)).ToList();
            if (any.Count == 0) return "sin concesión de " + capability + " para la tarea " + task;
            // rc3: identidad estricta. La huella de la concesión y la actual deben ser iguales; que falte en un lado NUNCA amplía el alcance.
            var docOk = any.Where(g => g.DocKey == docKey && string.Equals(g.DocPrint ?? "", docPrint ?? "", StringComparison.Ordinal)).ToList();
            if (docOk.Count == 0)
            {
                if (any.Any(g => g.DocKey == docKey && !string.IsNullOrEmpty(g.DocPrint) && string.IsNullOrEmpty(docPrint)))
                    return "la concesión de " + capability + " exige la huella del documento y ahora no está disponible";
                if (any.Any(g => g.DocKey == docKey)) return "la concesión de " + capability + " es para otra identidad del documento (huella distinta: reemplazado o recreado)";
                return "la concesión de " + capability + " es para otro documento";
            }
            var live = docOk.Where(g => g.ValidAt(now)).ToList();
            if (live.Count == 0) return docOk.Any(g => g.Revoked != null) ? "la concesión de " + capability + " fue revocada" : "la concesión de " + capability + " venció";
            var sc = (scope ?? Enumerable.Empty<string>()).ToList();
            if (sc.Count > 0 && !live.Any(g => g.Scope.Count == 0 || sc.All(g.Scope.Contains))) return "elementos fuera del ámbito concedido para " + capability;
            return null;
        }

        // -------- solicitudes (las crea el agente; sólo informan) --------

        public GrantRequest Request(string task, string docKey, string docPrint, string docTitle, IEnumerable<string> caps, string client, string objective, DateTime now)
        {
            var r = new GrantRequest
            {
                Id = Home.Sha256Hex(task + "|" + docKey + "|" + string.Join(",", caps.OrderBy(x => x, StringComparer.Ordinal)), 12),
                Task = task, DocKey = docKey, DocPrint = docPrint, DocTitle = docTitle, Client = client, Objective = objective,
                Created = now.ToString("o", CultureInfo.InvariantCulture), Capabilities = caps.Distinct().ToList(),
            };
            Home.WriteAtomic(Path.Combine(_dir, "solicitudes", r.Id + ".json"), Json.Write(new Dictionary<string, object>
            {
                ["id"] = r.Id, ["tarea"] = task, ["doc"] = docKey, ["huella_doc"] = docPrint ?? "", ["titulo"] = docTitle, ["cliente"] = client,
                ["objetivo"] = objective, ["creada"] = r.Created, ["capacidades"] = r.Capabilities.Cast<object>().ToList(),
            }));
            return r;
        }

        public List<GrantRequest> PendingRequests()
        {
            var res = new List<GrantRequest>();
            foreach (var f in Directory.GetFiles(Path.Combine(_dir, "solicitudes"), "*.json"))
            {
                try
                {
                    var o = Json.ParseObject(File.ReadAllText(f));
                    res.Add(new GrantRequest
                    {
                        Id = Arg.GetString(o, "id"), Task = Arg.GetString(o, "tarea"), DocKey = Arg.GetString(o, "doc"), DocPrint = Arg.GetString(o, "huella_doc"),
                        DocTitle = Arg.GetString(o, "titulo"), Client = Arg.GetString(o, "cliente"), Objective = Arg.GetString(o, "objetivo"),
                        Created = Arg.GetString(o, "creada"), Capabilities = Arg.GetStringList(o, "capacidades").ToList(),
                    });
                }
                catch (Exception ex) when (ex is IOException || ex is FormatException) { }
            }
            return res;
        }

        public void CloseRequest(string id)
        {
            try { File.Delete(Path.Combine(_dir, "solicitudes", id + ".json")); } catch (IOException) { }
        }
    }

    /// <summary>Lo que el ejecutor de tareas consulta en el punto de ejecución.</summary>
    public interface ITaskAuthority
    {
        /// <summary>null = autorizado; si no, motivo estructurado.</summary>
        string Check(Tasks.TaskRecord task, string capability, DateTime now);
    }

    /// <summary>Por defecto del motor: niega toda capacidad (fail-safe si nadie configuró autoridad).</summary>
    public sealed class DenyWritesAuthority : ITaskAuthority
    {
        public static readonly DenyWritesAuthority Instance = new DenyWritesAuthority();
        public string Check(Tasks.TaskRecord task, string capability, DateTime now) => "no hay autoridad de concesiones configurada";
    }

    public sealed class GrantAuthority : ITaskAuthority
    {
        private readonly GrantStore _store;
        public GrantAuthority(GrantStore store) { _store = store; }
        public string Check(Tasks.TaskRecord task, string capability, DateTime now)
        {
            var d = task.MainDoc;
            return _store.Check(task.Id, d?.Key, d?.Print, capability, now);
        }
    }
}
