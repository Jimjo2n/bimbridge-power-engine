// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Simulated, DURABLE BIM model (AH-02). It is the AgentHost's "Revit" in 0.7.0-alpha1: it survives the death of the process just as a
    /// real model survives the death of the agent. Single authority on disk (model folder):
    /// - <c>modelo.json</c>: snapshot with versioned schema, sha256 seal and generation number <c>gen</c>.
    /// - <c>modelo.&lt;gen&gt;.wal</c>: subsequent committed transactions (ChainLog). One transaction = ONE line; it is committed only
    ///   when that line is fully on disk. Whatever was applied in memory and did not reach the line is lost when the process dies (just
    ///   like a Revit transaction without Commit).
    /// There are no other representations: per-operation applications and the maximum fencing per plan are DERIVED from the snapshot + WAL.
    /// <see cref="Compact"/> folds the WAL into a new snapshot; after compaction, the detail of each transaction (what changed) is lost and
    /// only the per-operation count remains: this is the real case of "the effect is proven but the evidence cannot be reconstructed".
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
        // materialized state (derived from snapshot + WAL)
        private readonly SortedDictionary<long, string> _cat = new SortedDictionary<long, string>();
        private readonly Dictionary<long, Dictionary<string, object>> _params = new Dictionary<long, Dictionary<string, object>>();
        private readonly Dictionary<string, long> _compactedTx = new Dictionary<string, long>(StringComparer.Ordinal);      // op|plan → compacted transactions
        private readonly Dictionary<string, long> _maxFencing = new Dictionary<string, long>(StringComparer.Ordinal);      // plan → maximum fencing seen
        private readonly List<Dictionary<string, object>> _walTx = new List<Dictionary<string, object>>();
        public Dictionary<string, object> Config { get; private set; } = new Dictionary<string, object>(StringComparer.Ordinal);
        public List<Diagnostic> OpenDiagnostics { get; } = new List<Diagnostic>();

        private DurableSimModel(string dir) { _dir = dir; }

        public string Dir => _dir;
        private string SnapshotPath => Path.Combine(_dir, "modelo.json");
        private string WalPath(long gen) => Path.Combine(_dir, "modelo." + gen.ToString(CultureInfo.InvariantCulture) + ".wal");

        public sealed class ModelCorruptException : IOException { public ModelCorruptException(string m) : base(m) { } }
        public sealed class StaleFencingException : InvalidOperationException { public StaleFencingException(string m) : base(m) { } }

        /// <summary>Creates a new model. <paramref name="elements"/>: "Category=N" or "Category=N@Level".</summary>
        public static DurableSimModel Create(string dir, string docKey, string docPrint, IEnumerable<string> elements, Dictionary<string, object> config = null)
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(Path.Combine(dir, "modelo.json"))) throw new IOException("a model already exists in " + dir);
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

        /// <summary>Opens a model from disk (nothing in memory is reused). Throws ModelCorruptException if it is not trustworthy.</summary>
        public static DurableSimModel Open(string dir)
        {
            var m = new DurableSimModel(dir);
            m.Load();
            return m;
        }

        private void Load()
        {
            if (!File.Exists(SnapshotPath)) throw new ModelCorruptException("no modelo.json in " + _dir);
            Dictionary<string, object> s;
            try { s = Json.ParseObject(File.ReadAllText(SnapshotPath)); }
            catch (FormatException ex) { throw new ModelCorruptException("modelo.json unreadable: " + ex.Message); }
            if (Arg.GetLong(s, "esquema") != SchemaVersion) throw new ModelCorruptException("unsupported model schema: " + Arg.GetString(s, "esquema"));
            var seal = Arg.GetString(s, "sello");
            var body = new Dictionary<string, object>(s, StringComparer.Ordinal); body.Remove("sello");
            if (seal != Home.Sha256Hex(OpSemantics.Canonical(body), 32)) throw new ModelCorruptException("modelo.json tampered with (seal)");
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

            // leftovers from earlier generations (compaction interrupted after writing the snapshot): ignored and deleted
            foreach (var w in Directory.GetFiles(_dir, "modelo.*.wal"))
                if (w != WalPath(_gen)) { OpenDiagnostics.Add(Diagnostic.Info("wal_antiguo_descartado", Path.GetFileName(w))); try { File.Delete(w); } catch (IOException) { } }
            foreach (var tmp in Directory.GetFiles(_dir, "*.tmp-*")) { try { File.Delete(tmp); } catch (IOException) { } }

            // alpha3 (P1-C): validate everything before applying anything. 1st pass only validates (bounded memory); 2nd applies in streaming.
            _wal = ChainLog.Open(WalPath(_gen), true, _ => true, OpenDiagnostics, "wal");
            if (Diagnostic.AnyError(OpenDiagnostics)) throw new ModelCorruptException("model WAL not trustworthy: " + string.Join("; ", OpenDiagnostics.Where(d => d.Severity == "error")));
            var second = new List<Diagnostic>();
            var applied = 0L;
            ChainLog.Open(WalPath(_gen), false, r => { if (applied >= _wal.LastSeq) return false; ApplyRecord(r); applied++; return true; }, second, "wal");
            if (Diagnostic.AnyError(second) || applied != _wal.LastSeq) throw new ModelCorruptException("the WAL changed between validation and application");
        }

        private void ApplyRecord(Dictionary<string, object> r)
        {
            if (r.TryGetValue("cambios", out var ch) && ch is IEnumerable<object> list)
                foreach (var x in list.Cast<IList<object>>())
                {
                    var id = Convert.ToInt64(x[0], CultureInfo.InvariantCulture);
                    if (!_cat.ContainsKey(id)) throw new ModelCorruptException("the WAL changes a nonexistent element: " + id);
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

        /// <summary>Folds the WAL into a new snapshot (generation +1). After this, the transaction detail no longer exists.</summary>
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
                WriteSnapshot(0, ChainLog.Genesis);       // compaction commit point
                try { File.Delete(old); } catch (IOException) { }
                _walTx.Clear();
                _wal = ChainLog.Open(WalPath(_gen), true, new List<Dictionary<string, object>>(), new List<Diagnostic>(), "wal");
            }
        }

        // ---------- read ----------

        public List<long> Targets(IReadOnlyDictionary<string, object> scope)
        {
            lock (_lock)
            {
                string cat = null, lvl = null;
                if (scope != null && scope.TryGetValue("categoria", out var c)) cat = Convert.ToString(c, CultureInfo.InvariantCulture);
                if (scope != null && scope.TryGetValue("nivel", out var l)) lvl = Convert.ToString(l, CultureInfo.InvariantCulture);
                HashSet<long> ids = null;   // rc1: alcance.ids in the simulator too
                if (scope != null && scope.TryGetValue("ids", out var iv) && iv is System.Collections.IEnumerable ie && !(iv is string))
                    ids = new HashSet<long>(ie.Cast<object>().Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)));
                return _cat.Where(kv => (ids == null || ids.Contains(kv.Key)) && (cat == null || kv.Value == cat) && (lvl == null || (_params[kv.Key].TryGetValue("nivel", out var v) && Convert.ToString(v, CultureInfo.InvariantCulture) == lvl)))
                           .Select(kv => kv.Key).ToList();
            }
        }

        public int CountCategory(string cat) { lock (_lock) return _cat.Count(kv => kv.Value == cat); }

        public object Get(long id, string param) { lock (_lock) return _params.TryGetValue(id, out var p) && p.TryGetValue(param, out var v) ? v : null; }

        /// <summary>Committed transactions of a plan operation: total count and records with detail (the non-compacted ones).</summary>
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

        /// <summary>Fingerprint of the id set (to know whether the targets changed since a transaction).</summary>
        public static string IdsHash(IEnumerable<long> ids) => Home.Sha256Hex(string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))), 16);

        // ---------- write ----------

        /// <summary>
        /// Commits a transaction: checks the fencing (an owner with fencing lower than the maximum seen for the plan is a zombie and is
        /// rejected), writes ONE WAL line with fsync and only then applies in memory. Returns the transaction id.
        /// </summary>
        public string Commit(string op, string plan, long fencing, int targets, string targetsHash, List<object[]> changes, string origin = "agenthost")
        {
            lock (_lock)
            {
                if (plan != null)
                {
                    var max = _maxFencing.TryGetValue(plan, out var f) ? f : 0;
                    if (fencing < max) throw new StaleFencingException("fencing_obsoleto: " + fencing + " < " + max + " (another process owns the plan)");
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

        /// <summary>Change made by "someone else" in the model (tests): it belongs to no plan.</summary>
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
