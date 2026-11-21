// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Durable execution state of a plan (AH-03).
    /// AUTHORITY: <c>diario.jsonl</c> (ChainLog). Every state change of an operation is a "transicion" line written and flushed to
    /// disk BEFORE acting on it; the state of any operation is the result of replaying the journal.
    /// DERIVED: <c>estado.json</c> (checkpoint) — compact copy so another process (the one that lost the lease, the service, an agent)
    /// can inspect progress without replaying the journal. It carries the seq and chain hash it corresponds to, and a seal. On recovery it is
    /// checked against the journal: if it is older, it is ignored (warning); if it is altered or unreadable, it is discarded (warning) and rebuilt
    /// from the journal; if it says something the journal does not (seq ahead, a different history, other states at the same seq) recovery is
    /// REJECTED: a checkpoint never certifies completed operations on its own.
    /// </summary>
    public sealed class ExecJournal
    {
        public const int SchemaVersion = 1;
        public static readonly string[] PlanActions = { "pausar", "reanudar", "cancelar" };

        private readonly string _dir;
        private readonly ValidatedPlan _plan;
        private ChainLog _log;
        private readonly Faults _faults;
        public readonly Dictionary<string, OpRecord> Ops = new Dictionary<string, OpRecord>(StringComparer.Ordinal);
        public string PlanState { get; private set; } = PlanRunState.Active;
        public long Fencing { get; private set; }
        public long Seq => _log.LastSeq;
        /// <summary>Changes in order (seq, op, new state, error) for incremental queries.</summary>
        public readonly List<(long Seq, string Op, string To, string Error)> Changes = new List<(long, string, string, string)>();
        public int CheckpointEvery = 64;
        private int _sinceCheckpoint;

        public string JournalPath => Path.Combine(_dir, "diario.jsonl");
        public string CheckpointPath => Path.Combine(_dir, "estado.json");

        private ExecJournal(string dir, ValidatedPlan plan, Faults faults)
        {
            _dir = dir; _plan = plan; _faults = faults ?? new Faults();
            foreach (var op in plan.Ops) Ops[op.Id] = new OpRecord();
        }

        /// <summary>
        /// Rebuilds the state from disk. <paramref name="writer"/>: this process holds the lease (it may trim a torn final line
        /// and append). Returns null if recovery is not safe; the reasons go in <paramref name="diags"/>.
        /// </summary>
        public static ExecJournal Recover(string dir, ValidatedPlan plan, bool writer, Faults faults, List<Diagnostic> diags)
        {
            var j = new ExecJournal(dir, plan, faults);
            foreach (var tmp in Directory.Exists(dir) ? Directory.GetFiles(dir, "estado.json.tmp-*") : new string[0])
            {
                diags.Add(Diagnostic.Warn("checkpoint_a_medias", Path.GetFileName(tmp) + ": interrupted write, discarded"));
                if (writer) try { File.Delete(tmp); } catch (IOException) { }
            }
            var cp = ReadCheckpoint(j.CheckpointPath, plan, diags);
            var journalExists = File.Exists(j.JournalPath);
            var cpSeq = cp == null ? -1 : (Arg.GetLong(cp, "seq") ?? -1);
            Dictionary<string, OpRecord> atCp = null;
            string hashAtCp = null, planStateAtCp = null;
            long count = 0;
            // alpha3 (P1-C): replayed by streaming; on any error null is returned and the partial state is discarded.
            j._log = ChainLog.Open(j.JournalPath, writer, r =>
            {
                count++;
                if (!j.Replay(r, diags)) return false;
                var s = Arg.GetLong(r, "seq") ?? 0;
                if (s == cpSeq) { atCp = j.Ops.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal); hashAtCp = Arg.GetString(r, "h"); planStateAtCp = j.PlanState; }
                return true;
            }, diags, "diario");
            if (Diagnostic.AnyError(diags)) return null;
            if (count == 0)
            {
                if (cp != null) { diags.Add(Diagnostic.Error("diario_ausente", "there is a checkpoint (seq " + Arg.GetLong(cp, "seq") + ") but the journal " + (journalExists ? "is empty" : "does not exist") + "; nothing is certified from the checkpoint")); return null; }
                if (writer) j._log.Append(new Dictionary<string, object> { ["ev"] = "apertura", ["plan"] = plan.Hash, ["esquema"] = (long)SchemaVersion, ["t"] = Home.Now() });
                return j;
            }
            if (cp != null)
            {
                if (cpSeq > j.Seq) { diags.Add(Diagnostic.Error("checkpoint_adelantado", "the checkpoint says seq " + cpSeq + " and the journal ends at " + j.Seq + "; journal records are missing")); return null; }
                if (hashAtCp != Arg.GetString(cp, "h")) { diags.Add(Diagnostic.Error("checkpoint_otra_historia", "the chain hash at seq " + cpSeq + " does not match the journal")); return null; }
                if (atCp == null) { diags.Add(Diagnostic.Error("checkpoint_invalido", "seq " + cpSeq + " does not exist in the journal")); return null; }
                var cpOps = cp.TryGetValue("ops", out var co) ? co as Dictionary<string, object> : null;
                var mine = OpSemantics.Canonical(atCp.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToJson()));
                if (cpOps == null || OpSemantics.Canonical(cpOps) != mine || Arg.GetString(cp, "plan_estado") != planStateAtCp)
                { diags.Add(Diagnostic.Error("checkpoint_contradice_diario", "at seq " + cpSeq + " the checkpoint and the journal give different states")); return null; }
                if (cpSeq < j.Seq) diags.Add(Diagnostic.Info("checkpoint_atrasado", "checkpoint at seq " + cpSeq + ", journal at " + j.Seq + "; using the journal"));
            }
            return j;
        }

        private static Dictionary<string, object> ReadCheckpoint(string path, ValidatedPlan plan, List<Diagnostic> diags)
        {
            if (!File.Exists(path)) return null;
            Dictionary<string, object> cp;
            try { cp = Json.ParseObject(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is FormatException || ex is IOException) { diags.Add(Diagnostic.Warn("checkpoint_ilegible", ex.Message + "; rebuilding from the journal")); return null; }
            var seal = Arg.GetString(cp, "sello");
            var body = new Dictionary<string, object>(cp, StringComparer.Ordinal); body.Remove("sello");
            if (seal == null || seal != Home.Sha256Hex(OpSemantics.Canonical(body), 32)) { diags.Add(Diagnostic.Warn("checkpoint_alterado", "invalid seal; discarded and rebuilt from the journal")); return null; }
            if (Arg.GetLong(cp, "esquema") != SchemaVersion) { diags.Add(Diagnostic.Warn("checkpoint_esquema", "schema " + Arg.GetString(cp, "esquema") + " not supported; rebuilding from the journal")); return null; }
            if (Arg.GetString(cp, "plan") != plan.Hash) { diags.Add(Diagnostic.Error("checkpoint_de_otro_plan", Arg.GetString(cp, "plan"))); return cp; }
            return cp;
        }

        /// <summary>Applies a journal line, validating it. false = not recoverable (diagnostic added).</summary>
        private bool Replay(Dictionary<string, object> r, List<Diagnostic> diags)
        {
            var seq = Arg.GetLong(r, "seq") ?? 0;
            var ev = Arg.GetString(r, "ev");
            string where = "seq " + seq;
            if (seq == 1)
            {
                if (ev != "apertura") { diags.Add(Diagnostic.Error("diario_sin_apertura", where)); return false; }
                if (Arg.GetString(r, "plan") != _plan.Hash) { diags.Add(Diagnostic.Error("diario_de_otro_plan", "plan " + Arg.GetString(r, "plan") + ", expected " + _plan.Hash)); return false; }
                if (Arg.GetLong(r, "esquema") != SchemaVersion) { diags.Add(Diagnostic.Error("diario_esquema_no_soportado", Arg.GetString(r, "esquema"))); return false; }
                return true;
            }
            var f = Arg.GetLong(r, "f") ?? 0;
            switch (ev)
            {
                case "dueno":
                    if (f < Fencing) { diags.Add(Diagnostic.Error("fencing_regresivo", where + ": owner with fencing " + f + " < " + Fencing)); return false; }
                    Fencing = f;
                    return true;
                case "transicion":
                {
                    if (f != Fencing) { diags.Add(Diagnostic.Error("escritor_obsoleto", where + ": written with fencing " + f + " when the current owner had " + Fencing)); return false; }
                    var op = Arg.GetString(r, "op");
                    var from = Arg.GetString(r, "de");
                    var a = Arg.GetString(r, "a");
                    if (op == null || !Ops.TryGetValue(op, out var rec)) { diags.Add(Diagnostic.Error("operacion_desconocida", where + ": " + op)); return false; }
                    if (!OpState.IsValid(from) || !OpState.IsValid(a)) { diags.Add(Diagnostic.Error("estado_desconocido", where + ": " + from + " → " + a)); return false; }
                    if (rec.State != from) { diags.Add(Diagnostic.Error("transicion_inconsistente", where + ": " + op + " was in " + rec.State + ", the line says " + from)); return false; }
                    if (!OpState.IsLegal(from, a)) { diags.Add(Diagnostic.Error("transicion_ilegal", where + ": " + op + " " + from + " → " + a)); return false; }
                    ApplyTransition(rec, r, seq);
                    Changes.Add((seq, op, a, rec.Error));
                    return true;
                }
                case "plan":
                {
                    if (f != Fencing) { diags.Add(Diagnostic.Error("escritor_obsoleto", where)); return false; }
                    var acc = Arg.GetString(r, "accion");
                    if (Array.IndexOf(PlanActions, acc) < 0) { diags.Add(Diagnostic.Error("accion_desconocida", where + ": " + acc)); return false; }
                    PlanState = acc == "pausar" ? PlanRunState.Paused : acc == "cancelar" ? PlanRunState.Cancelled : PlanRunState.Active;
                    return true;
                }
                case "nota":
                    return true;
                default:
                    diags.Add(Diagnostic.Error("evento_desconocido", where + ": " + ev));
                    return false;
            }
        }

        private static void ApplyTransition(OpRecord rec, Dictionary<string, object> r, long seq)
        {
            rec.State = Arg.GetString(r, "a");
            rec.LastSeq = seq;
            var att = Arg.GetLong(r, "intento"); if (att != null) rec.Attempt = (int)att.Value;
            if (r.TryGetValue("res", out var res) && res is Dictionary<string, object> rd) rec.Result = rd;
            if (r.TryGetValue("evid", out var ev) && ev is Dictionary<string, object> ed) rec.Evidence = ed;
            rec.Error = Arg.GetString(r, "err");
            rec.Note = Arg.GetString(r, "nota");
        }

        // ---------- writing (owner only) ----------

        public void RecordOwner(long fencing, string owner)
        {
            if (fencing < Fencing) throw new InvalidOperationException("regressive fencing");
            _log.Append(new Dictionary<string, object> { ["ev"] = "dueno", ["f"] = fencing, ["dueno"] = owner, ["t"] = Home.Now() });
            Fencing = fencing;
        }

        /// <summary>Changes the state of an operation: validates the transition, writes it to the journal (fsync) and only then applies it.</summary>
        public void Transition(string op, string to, int? attempt = null, Dictionary<string, object> result = null, Dictionary<string, object> evidence = null, string error = null, string note = null)
        {
            var rec = Ops[op];
            if (!OpState.IsLegal(rec.State, to)) throw new InvalidOperationException("transicion_ilegal: " + op + " " + rec.State + " → " + to);
            var line = new Dictionary<string, object> { ["ev"] = "transicion", ["f"] = Fencing, ["op"] = op, ["de"] = rec.State, ["a"] = to, ["t"] = Home.Now() };
            if (attempt != null) line["intento"] = (long)attempt.Value;
            if (result != null) line["res"] = result;
            if (evidence != null) line["evid"] = evidence;
            if (error != null) line["err"] = error;
            if (note != null) line["nota"] = note;
            var written = _log.Append(line);
            ApplyTransition(rec, written, _log.LastSeq);
            Changes.Add((_log.LastSeq, op, to, rec.Error));
            if (++_sinceCheckpoint >= CheckpointEvery) Checkpoint();
        }

        public void PlanAction(string action)
        {
            if (Array.IndexOf(PlanActions, action) < 0) throw new ArgumentException(action);
            _log.Append(new Dictionary<string, object> { ["ev"] = "plan", ["f"] = Fencing, ["accion"] = action, ["t"] = Home.Now() });
            PlanState = action == "pausar" ? PlanRunState.Paused : action == "cancelar" ? PlanRunState.Cancelled : PlanRunState.Active;
        }

        public void Note(string op, string text, Dictionary<string, object> data = null)
        {
            var line = new Dictionary<string, object> { ["ev"] = "nota", ["f"] = Fencing, ["op"] = op, ["texto"] = text, ["t"] = Home.Now() };
            if (data != null) line["datos"] = data;
            _log.Append(line);
        }

        /// <summary>Writes the derived checkpoint (atomic). Crash point "runner.en_checkpoint": leaves a half-written temp file.</summary>
        public void Checkpoint()
        {
            _sinceCheckpoint = 0;
            var body = new Dictionary<string, object>
            {
                ["esquema"] = (long)SchemaVersion, ["plan"] = _plan.Hash, ["seq"] = _log.LastSeq, ["h"] = _log.LastHash, ["plan_estado"] = PlanState,
                ["t"] = Home.Now(), ["ops"] = Ops.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToJson(), StringComparer.Ordinal),
            };
            body["sello"] = Home.Sha256Hex(OpSemantics.Canonical(body), 32);
            var text = Json.Write(body);
            if (_faults.Armed("runner.en_checkpoint"))
            {
                var tmp = CheckpointPath + ".tmp-caida";
                var bytes = Encoding.UTF8.GetBytes(text);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) { fs.Write(bytes, 0, bytes.Length / 2); fs.Flush(true); }
                _faults.Hit("runner.en_checkpoint");
            }
            Home.WriteAtomic(CheckpointPath, text);
        }

        /// <summary>Reads the checkpoint without a lease and without replaying the journal (inspection by another process). null if missing or unreliable.</summary>
        public static Dictionary<string, object> PeekCheckpoint(string dir, string planHash)
        {
            var diags = new List<Diagnostic>();
            var path = Path.Combine(dir, "estado.json");
            if (!File.Exists(path)) return null;
            try
            {
                var cp = Json.ParseObject(File.ReadAllText(path));
                var seal = Arg.GetString(cp, "sello");
                var body = new Dictionary<string, object>(cp, StringComparer.Ordinal); body.Remove("sello");
                if (seal != Home.Sha256Hex(OpSemantics.Canonical(body), 32) || Arg.GetString(cp, "plan") != planHash) return null;
                return cp;
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException) { return null; }
        }
    }
}
