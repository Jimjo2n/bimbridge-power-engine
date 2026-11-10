// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Modelo BIM simulado y DURABLE (AH-02). Es el "Revit" del AgentHost en 0.7.0-alpha1: sobrevive a la muerte del proceso como un
    /// modelo real sobrevive a la del agente. Autoridad única en disco (carpeta del modelo):
    /// - <c>modelo.json</c>: instantánea con esquema versionado, sello sha256 y número de generación <c>gen</c>.
    /// - <c>modelo.&lt;gen&gt;.wal</c>: transacciones confirmadas posteriores (ChainLog). Una transacción = UNA línea; está confirmada sólo
    ///   cuando esa línea está entera en disco. Lo que se aplicó en memoria y no llegó a la línea se pierde al morir el proceso (igual que
    ///   una transacción de Revit sin Commit).
    /// No hay más representaciones: las aplicaciones por operación y el fencing máximo por plan se DERIVAN de la instantánea + el WAL.
    /// <see cref="Compact"/> lleva el WAL a una nueva instantánea; tras compactar, el detalle de cada transacción (qué cambió) se pierde y
    /// sólo queda el recuento por operación: es el caso real de "el efecto está probado pero la evidencia no se puede reconstruir".
    /// </summary>
    public sealed class DurableSimModel
    {
        public const int SchemaVersion = 1;
        private readonly object _lock = new object();
        private readonly string _dir;
        private ChainLog _wal;
        private long _gen;

        public string DocKey { get; private set; }
        public string DocPrint { get; private set; }
        // estado materializado (derivado de instantánea + WAL)
        private readonly SortedDictionary<long, string> _cat = new SortedDictionary<long, string>();
        private readonly Dictionary<long, Dictionary<string, object>> _params = new Dictionary<long, Dictionary<string, object>>();
        private readonly Dictionary<string, long> _compactedTx = new Dictionary<string, long>(StringComparer.Ordinal);      // op|plan → transacciones compactadas
        private readonly Dictionary<string, long> _maxFencing = new Dictionary<string, long>(StringComparer.Ordinal);      // plan → fencing máximo visto
        private readonly List<Dictionary<string, object>> _walTx = new List<Dictionary<string, object>>();
        public Dictionary<string, object> Config { get; private set; } = new Dictionary<string, object>(StringComparer.Ordinal);
        public List<Diagnostic> OpenDiagnostics { get; } = new List<Diagnostic>();

        private DurableSimModel(string dir) { _dir = dir; }

        public string Dir => _dir;
        private string SnapshotPath => Path.Combine(_dir, "modelo.json");
        private string WalPath(long gen) => Path.Combine(_dir, "modelo." + gen.ToString(CultureInfo.InvariantCulture) + ".wal");

        public sealed class ModelCorruptException : IOException { public ModelCorruptException(string m) : base(m) { } }
        public sealed class StaleFencingException : InvalidOperationException { public StaleFencingException(string m) : base(m) { } }

        /// <summary>Crea un modelo nuevo. <paramref name="elements"/>: "Categoria=N" o "Categoria=N@Nivel".</summary>
        public static DurableSimModel Create(string dir, string docKey, string docPrint, IEnumerable<string> elements, Dictionary<string, object> config = null)
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(Path.Combine(dir, "modelo.json"))) throw new IOException("ya existe un modelo en " + dir);
            var m = new DurableSimModel(dir) { DocKey = docKey, DocPrint = docPrint, Config = config ?? new Dictionary<string, object>(StringComparer.Ordinal) };
            long next = 1000;
            foreach (var spec in elements)
            {
                var eq = spec.IndexOf('=');
                var cat = spec.Substring(0, eq);
                var rest = spec.Substring(eq + 1);
                string level = null;
                var at = rest.IndexOf('@');
                if (at >= 0) { level = rest.Substring(at + 1); rest = rest.Substring(0, at); }
                var n = int.Parse(rest, CultureInfo.InvariantCulture);
                for (var i = 0; i < n; i++)
                {
                    var id = next++;
                    m._cat[id] = cat;
                    m._params[id] = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (level != null) m._params[id]["nivel"] = level;
                }
            }
            m._gen = 1;
            m.WriteSnapshot(0, ChainLog.Genesis);
            m._wal = ChainLog.Open(m.WalPath(1), true, new List<Dictionary<string, object>>(), new List<Diagnostic>(), "wal");
            return m;
        }

        /// <summary>Abre un modelo desde disco (nada en memoria se reutiliza). Lanza ModelCorruptException si no es fiable.</summary>
        public static DurableSimModel Open(string dir)
        {
            var m = new DurableSimModel(dir);
            m.Load();
            return m;
        }

        private void Load()
        {
            if (!File.Exists(SnapshotPath)) throw new ModelCorruptException("no hay modelo.json en " + _dir);
            Dictionary<string, object> s;
            try { s = Json.ParseObject(File.ReadAllText(SnapshotPath)); }
            catch (FormatException ex) { throw new ModelCorruptException("modelo.json ilegible: " + ex.Message); }
            if (Arg.GetLong(s, "esquema") != SchemaVersion) throw new ModelCorruptException("esquema de modelo no soportado: " + Arg.GetString(s, "esquema"));
            var seal = Arg.GetString(s, "sello");
            var body = new Dictionary<string, object>(s, StringComparer.Ordinal); body.Remove("sello");
            if (seal != Home.Sha256Hex(OpSemantics.Canonical(body), 32)) throw new ModelCorruptException("modelo.json alterado (sello)");
            DocKey = Arg.GetString(s, "clave"); DocPrint = Arg.GetString(s, "huella");
            _gen = Arg.GetLong(s, "gen") ?? 1;
            if (s.TryGetValue("config", out var c) && c is Dictionary<string, object> cd) Config = cd;
            foreach (var e in ((List<object>)s["elementos"]).Cast<List<object>>())
            {
                var id = Convert.ToInt64(e[0], CultureInfo.InvariantCulture);
                _cat[id] = (string)e[1];
                _params[id] = new Dictionary<string, object>((Dictionary<string, object>)e[2], StringComparer.Ordinal);
            }
            if (s.TryGetValue("tx_por_op", out var t) && t is Dictionary<string, object> td) foreach (var kv in td) _compactedTx[kv.Key] = Convert.ToInt64(kv.Value, CultureInfo.InvariantCulture);
            if (s.TryGetValue("fencing", out var f) && f is Dictionary<string, object> fd) foreach (var kv in fd) _maxFencing[kv.Key] = Convert.ToInt64(kv.Value, CultureInfo.InvariantCulture);

            // restos de generaciones anteriores (compactación interrumpida tras escribir la instantánea): se ignoran y se borran
            foreach (var w in Directory.GetFiles(_dir, "modelo.*.wal"))
                if (w != WalPath(_gen)) { OpenDiagnostics.Add(Diagnostic.Info("wal_antiguo_descartado", Path.GetFileName(w))); try { File.Delete(w); } catch (IOException) { } }
            foreach (var tmp in Directory.GetFiles(_dir, "*.tmp-*")) { try { File.Delete(tmp); } catch (IOException) { } }

            // alpha3 (P1-C): validar todo antes de aplicar nada. 1ª pasada sólo valida (memoria acotada); 2ª aplica en streaming.
            _wal = ChainLog.Open(WalPath(_gen), true, _ => true, OpenDiagnostics, "wal");
            if (Diagnostic.AnyError(OpenDiagnostics)) throw new ModelCorruptException("WAL del modelo no fiable: " + string.Join("; ", OpenDiagnostics.Where(d => d.Severity == "error")));
            var second = new List<Diagnostic>();
            var applied = 0L;
            ChainLog.Open(WalPath(_gen), false, r => { if (applied >= _wal.LastSeq) return false; ApplyRecord(r); applied++; return true; }, second, "wal");
            if (Diagnostic.AnyError(second) || applied != _wal.LastSeq) throw new ModelCorruptException("el WAL cambió entre validación y aplicación");
        }

        private void ApplyRecord(Dictionary<string, object> r)
        {
            if (r.TryGetValue("cambios", out var ch) && ch is IEnumerable<object> list)
                foreach (var x in list.Cast<IList<object>>())
                {
                    var id = Convert.ToInt64(x[0], CultureInfo.InvariantCulture);
                    if (!_cat.ContainsKey(id)) throw new ModelCorruptException("el WAL cambia un elemento inexistente: " + id);
                    var p = (string)x[1];
                    if (x[3] == null) _params[id].Remove(p); else _params[id][p] = x[3];
                }
            if (r.TryGetValue("nuevos", out var nu) && nu is IEnumerable<object> nl)
                foreach (var x in nl.Cast<IList<object>>())
                {
                    var id = Convert.ToInt64(x[0], CultureInfo.InvariantCulture);
                    _cat[id] = (string)x[1];
                    _params[id] = x.Count > 2 && x[2] is Dictionary<string, object> pd ? new Dictionary<string, object>(pd, StringComparer.Ordinal) : new Dictionary<string, object>(StringComparer.Ordinal);
                }
            var plan = Arg.GetString(r, "plan");
            var f = Arg.GetLong(r, "f");
            if (plan != null && f != null) _maxFencing[plan] = Math.Max(_maxFencing.TryGetValue(plan, out var cur) ? cur : 0, f.Value);
            _walTx.Add(r);
        }

        private void WriteSnapshot(long walSeq, string walHash)
        {
            var body = new Dictionary<string, object>
            {
                ["esquema"] = (long)SchemaVersion, ["clave"] = DocKey, ["huella"] = DocPrint, ["gen"] = _gen,
                ["elementos"] = _cat.Select(kv => (object)new List<object> { kv.Key, kv.Value, new Dictionary<string, object>(_params[kv.Key], StringComparer.Ordinal) }).ToList(),
                ["tx_por_op"] = _compactedTx.ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.Ordinal),
                ["fencing"] = _maxFencing.ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.Ordinal),
                ["config"] = Config,
            };
            body["sello"] = Home.Sha256Hex(OpSemantics.Canonical(body), 32);
            Home.WriteAtomic(SnapshotPath, Json.Write(body));
        }

        /// <summary>Lleva el WAL a una instantánea nueva (generación +1). Tras esto, el detalle de las transacciones ya no existe.</summary>
        public void Compact()
        {
            lock (_lock)
            {
                foreach (var r in _walTx)
                {
                    var op = Arg.GetString(r, "op");
                    if (op == null) continue;
                    var key = op + "|" + Arg.GetString(r, "plan");
                    _compactedTx[key] = (_compactedTx.TryGetValue(key, out var n) ? n : 0) + 1;
                }
                var old = WalPath(_gen);
                _gen++;
                WriteSnapshot(0, ChainLog.Genesis);       // punto de confirmación de la compactación
                try { File.Delete(old); } catch (IOException) { }
                _walTx.Clear();
                _wal = ChainLog.Open(WalPath(_gen), true, new List<Dictionary<string, object>>(), new List<Diagnostic>(), "wal");
            }
        }

        // ---------- lectura ----------

        public List<long> Targets(IReadOnlyDictionary<string, object> scope)
        {
            lock (_lock)
            {
                string cat = null, lvl = null;
                if (scope != null && scope.TryGetValue("categoria", out var c)) cat = Convert.ToString(c, CultureInfo.InvariantCulture);
                if (scope != null && scope.TryGetValue("nivel", out var l)) lvl = Convert.ToString(l, CultureInfo.InvariantCulture);
                HashSet<long> ids = null;   // rc1: alcance.ids también en el simulador
                if (scope != null && scope.TryGetValue("ids", out var iv) && iv is System.Collections.IEnumerable ie && !(iv is string))
                    ids = new HashSet<long>(ie.Cast<object>().Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)));
                return _cat.Where(kv => (ids == null || ids.Contains(kv.Key)) && (cat == null || kv.Value == cat) && (lvl == null || (_params[kv.Key].TryGetValue("nivel", out var v) && Convert.ToString(v, CultureInfo.InvariantCulture) == lvl)))
                           .Select(kv => kv.Key).ToList();
            }
        }

        public int CountCategory(string cat) { lock (_lock) return _cat.Count(kv => kv.Value == cat); }

        public object Get(long id, string param) { lock (_lock) return _params.TryGetValue(id, out var p) && p.TryGetValue(param, out var v) ? v : null; }

        /// <summary>Transacciones confirmadas de una operación de un plan: recuento total y registros con detalle (los no compactados).</summary>
        public (long Count, List<Dictionary<string, object>> Detailed) TxFor(string op, string plan)
        {
            lock (_lock)
            {
                var det = _walTx.Where(r => Arg.GetString(r, "op") == op && Arg.GetString(r, "plan") == plan).ToList();
                var compacted = _compactedTx.TryGetValue(op + "|" + plan, out var n) ? n : 0;
                return (compacted + det.Count, det);
            }
        }

        public long MaxFencing(string plan) { lock (_lock) return _maxFencing.TryGetValue(plan, out var f) ? f : 0; }

        /// <summary>Huella del conjunto de ids (para saber si los objetivos cambiaron desde una transacción).</summary>
        public static string IdsHash(IEnumerable<long> ids) => Home.Sha256Hex(string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))), 16);

        // ---------- escritura ----------

        /// <summary>
        /// Confirma una transacción: comprueba el fencing (un dueño con fencing menor que el máximo visto para el plan es un zombi y se
        /// rechaza), escribe UNA línea en el WAL con fsync y sólo entonces aplica en memoria. Devuelve el id de transacción.
        /// </summary>
        public string Commit(string op, string plan, long fencing, int targets, string targetsHash, List<object[]> changes, string origin = "agenthost")
        {
            lock (_lock)
            {
                if (plan != null)
                {
                    var max = _maxFencing.TryGetValue(plan, out var f) ? f : 0;
                    if (fencing < max) throw new StaleFencingException("fencing_obsoleto: " + fencing + " < " + max + " (otro proceso es dueño del plan)");
                }
                var rec = new Dictionary<string, object>
                {
                    ["tx"] = "tx-" + _gen.ToString(CultureInfo.InvariantCulture) + "-" + (_wal.LastSeq + 1).ToString(CultureInfo.InvariantCulture),
                    ["op"] = op, ["plan"] = plan, ["f"] = fencing, ["obj"] = (long)targets, ["objs_h"] = targetsHash, ["origen"] = origin,
                    ["cambios"] = changes.Select(c => (object)c.ToList()).ToList(),
                };
                var written = _wal.Append(rec);
                ApplyRecord(written);
                return (string)written["tx"];
            }
        }

        /// <summary>Cambio hecho por "otra persona" en el modelo (pruebas): no pertenece a ningún plan.</summary>
        public string CommitExternal(List<object[]> changes, List<object[]> newElements = null)
        {
            lock (_lock)
            {
                var rec = new Dictionary<string, object>
                {
                    ["tx"] = "tx-" + _gen.ToString(CultureInfo.InvariantCulture) + "-" + (_wal.LastSeq + 1).ToString(CultureInfo.InvariantCulture),
                    ["op"] = null, ["plan"] = null, ["origen"] = "externo",
                    ["cambios"] = changes.Select(c => (object)c.ToList()).ToList(),
                };
                if (newElements != null) rec["nuevos"] = newElements.Select(c => (object)c.ToList()).ToList();
                var written = _wal.Append(rec);
                ApplyRecord(written);
                return (string)written["tx"];
            }
        }

        public long NextElementId() { lock (_lock) return (_cat.Count == 0 ? 1000 : _cat.Keys.Max()) + 1; }
    }
}
