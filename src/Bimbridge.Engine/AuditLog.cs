// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Durable audit (0.6.1), separate from telemetry (<see cref="Journal"/>). Shared by several Revit processes on the same PC.
    ///
    /// Guarantees:
    /// - Synchronous write with WriteThrough + Flush(true): when Append returns, the line is on disk.
    /// - Cross-process serialization: each Append takes <c>auditoria.lock</c> (FileShare.None) and re-reads what other processes appended
    ///   since last time, so seq and prev are never computed over a stale state.
    /// - Chain: each line carries prev = sha256 of the previous one. <see cref="Verify"/> detects deletion, editing, reordering and a cut tail.
    /// - Fail safe: an invalid file (on open or on re-read) is moved to quarantine and a new chain is started whose first record
    ///   cites the set-aside file and its sha256. Writing never continues on top of a broken chain.
    /// - Never silently dropped: a write failure throws <see cref="JournalUnavailableException"/> and adds to <see cref="Failures"/>.
    ///
    /// - Head (rc3): <c>auditoria.jsonl.cabeza</c> stores (seq, hash and length of the last record) after each write. Verify
    ///   compares it with the tail: it detects editing or deletion of the LAST record, which the chain alone cannot detect.
    /// - Cheap re-read (rc3): on each Append it checks (a) that the last known record is still identical byte for byte and (b) that the
    ///   file did not change its timestamp without changing its size; if not, full verification (and quarantine if needed).
    ///
    /// Threat model (local, no secret): detects accidental corruption, interrupted writes and hand edits of the file.
    /// It does NOT withstand a process of the same user that consistently rewrites both the whole chain AND the head: that requires
    /// an anchor outside that user's reach (server, machine DPAPI or a signature with a protected key), planned for 0.7.0.
    /// Two phases (<see cref="Intent"/> / <see cref="Outcome"/>): the intent is persisted BEFORE touching the model; if it cannot be, nothing is touched.
    /// </summary>
    public sealed class AuditLog
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private string _prev = "";
        private long _seq;
        private long _knownLength;
        private long _lastOffset = -1;      // start of the last known record
        private DateTime _knownWrite;        // modification time after our last read/write
        private long _failures;

        public long Failures => Interlocked.Read(ref _failures);
        public string Path_ => _path;
        /// <summary>Files set aside by this instance (full path).</summary>
        public readonly List<string> Quarantined = new List<string>();
        public static TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
        /// <summary>Lock wait limit for THIS instance (defaults to <see cref="LockTimeout"/>, 10 s). Tests shorten it without touching the global value.</summary>
        public TimeSpan LockWait = LockTimeout;
        /// <summary>This instance's margin to receive the confirmation of an entry already claimed by another leader.</summary>
        public TimeSpan ClaimWait = ClaimGrace;
        /// <summary>Tests only: called right before writing; may throw IOException to simulate a full disk.</summary>
        public Action<string> FaultBeforeWrite;
        /// <summary>
        /// Tests only (AUD-01): returns true at the point where a process crash must be simulated. Points: antes_bloqueo, inicio_lider, tras_reclamo,
        /// antes_escribir, durante_escritura, tras_escribir_antes_fsync, tras_fsync_antes_cabeza, tras_cabeza, antes_confirmar,
        /// tras_confirmar_parcial. The crash is an <see cref="AuditCrashException"/>: nothing is confirmed or cleaned up, as if the process died.
        /// </summary>
        public Func<string, bool> CrashPoint;
        private void Crash(string point) { if (CrashPoint != null && CrashPoint(point)) throw new AuditCrashException(point); }

        public AuditLog(string path)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            lock (_lock) WithFileLock(() => { Resync(fullCheck: true); return true; });
        }

        public static string DefaultPath => Path.Combine(Home.Root, "auditoria", "auditoria.jsonl");

        private string LockPath => _path + ".lock";
        public static string HeadPath(string path) => path + ".cabeza";

        /// <summary>Records an authority event. Returns its seq.</summary>
        public long Append(string author, string op, string estado, Dictionary<string, object> extra = null)
        {
            var me = new AuditItem { Id = Guid.NewGuid().ToString("N"), Author = author, Op = op, Estado = estado, Extra = extra, Pid = PidCache, T = Home.Now() };
            try { return AppendBatched(me); }
            catch (JournalUnavailableException) { Interlocked.Increment(ref _failures); throw; }
        }

        // ---------------- cross-process group commit (issue #27) ----------------
        //
        // With the lock free, Append writes directly (fast path, identical format). With the lock busy, the writer drops its entry
        // in <auditoria>.pend\ and waits; whoever holds the lock (the leader) claims ALL pending entries and writes them in a single
        // pass: one append, one fsync and one head update for the whole batch. Each follower only receives its confirmation
        // (<id>.done with its seq) AFTER the leader finished the fsync and the head. A leader error is propagated to each follower
        // (<id>.err): nothing is ever confirmed before being persisted. An entry claimed by a leader that crashed without confirming is NOT rewritten
        // (avoids duplicates): the follower fails in a controlled way and the intent is left to reconcile, like any uncertain write.
        // The global order is the pending file name order (enqueue ticks); seq and prev are always assigned by the leader under the lock.

        private static readonly long PidCache = System.Diagnostics.Process.GetCurrentProcess().Id;
        public const int MaxBatch = 64;
        private DateTime _lastClean = DateTime.MinValue;
        private const long AwaitClaimed = -1;
        /// <summary>Margin for a leader that already claimed my entry to send its confirmation; once past, it fails without rewriting.</summary>
        public static TimeSpan ClaimGrace = TimeSpan.FromSeconds(5);
        private string PendDir => _path + ".pend";

        private sealed class AuditItem
        {
            public string Id, Author, Op, Estado, Cid;
            public object T;
            public long Pid;
            public Dictionary<string, object> Extra;
        }

        private long AppendBatched(AuditItem me)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rnd = new Random();
            string pendName = null;
            while (true)
            {
                if (pendName != null)
                {
                    var done = Collect(me);
                    if (done.HasValue) return done.Value;
                }
                var waited = sw.ElapsedTicks;
                Crash("antes_bloqueo");
                var fs = TryOpenLock();
                if (fs != null)
                {
                    var held = System.Diagnostics.Stopwatch.StartNew();
                    // Replies to followers are sent AFTER releasing the lock: durability is already complete (fsync and head)
                    // and this way the lock does not wait to write one file per follower.
                    var after = new List<Action>();
                    long led = 0;
                    var crashed = false;
                    try
                    {
                        using (fs)
                        {
                            try { lock (_lock) led = Lead(me, pendName, after); }
                            catch (AuditCrashException) { crashed = true; throw; }
                            finally { AuditProfile.Lock(waited, held.ElapsedTicks); }
                        }
                    }
                    finally
                    {
                        // A simulated crash sends nothing: the process "died". The next leader will reconcile.
                        if (!crashed)
                        {
                            Crash("antes_confirmar");
                            for (var k = 0; k < after.Count; k++)
                            {
                                after[k]();
                                if (k == 0 && after.Count > 1) Crash("tras_confirmar_parcial");
                            }
                        }
                    }
                    if (led != AwaitClaimed) return led;
                    var grace = System.Diagnostics.Stopwatch.StartNew();
                    while (grace.Elapsed < ClaimWait) { var d = Collect(me); if (d.HasValue) return d.Value; Thread.Sleep(2); }
                    throw new JournalUnavailableException("audit entry claimed by another writer without confirmation (possible crash); intent pending reconciliation", null);
                }
                if (pendName == null) pendName = Enqueue(me);
                if (sw.Elapsed > LockWait)
                {
                    // Recover our own entry with the SAME claim primitive the leader uses (exclusive open + delete on close):
                    // either I recover it (nobody wrote it) or a leader already has it; never both.
                    if (TryReclaim(Path.Combine(PendDir, pendName + ".json")))
                        throw new JournalUnavailableException("audit lock not obtained within " + LockWait.TotalSeconds + " s (nothing was written)", null);
                    // A leader already claimed it: wait for its confirmation for a bounded margin; if it does not arrive, fail without rewriting.
                    var grace = System.Diagnostics.Stopwatch.StartNew();
                    while (grace.Elapsed < ClaimWait) { var d = Collect(me); if (d.HasValue) return d.Value; Thread.Sleep(5); }
                    throw new JournalUnavailableException("audit entry claimed by another writer without confirmation (possible crash); intent pending reconciliation", null);
                }
                Thread.Sleep(1 + rnd.Next(3));
            }
        }

        /// <summary>
        /// Withdraws one of our own pending entries with the SAME atomic primitive the leader uses to claim it (rename): either the writer
        /// withdraws it (nobody wrote it) or a leader already claimed it; never both.
        /// </summary>
        private static bool TryReclaim(string file)
        {
            try
            {
                var withdrawn = file + ".retirada";
                File.Move(file, withdrawn);
                try { File.Delete(withdrawn); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        /// <summary>Tries to take the lock without waiting. null = busy.</summary>
        private FileStream TryOpenLock()
        {
            try { return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (DirectoryNotFoundException ex) { throw new JournalUnavailableException("audit folder not available: " + ex.Message, ex); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException ex) { throw new JournalUnavailableException("audit lock without permission: " + ex.Message, ex); }
        }

        private string Enqueue(AuditItem me)
        {
            me.Cid = me.Id;
            var name = DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + me.Id;
            var json = Json.Write(new Dictionary<string, object>
            {
                ["id"] = me.Id, ["autor"] = me.Author, ["op"] = me.Op, ["estado"] = me.Estado, ["pid"] = me.Pid, ["t"] = me.T, ["extra"] = me.Extra,
            });
            try
            {
                Directory.CreateDirectory(PendDir);
                var tmp = Path.Combine(PendDir, name + ".t");
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, Path.Combine(PendDir, name + ".json"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new JournalUnavailableException("audit not writable (writer queue): " + ex.Message, ex);
            }
            return name;
        }

        /// <summary>Confirmation or error a leader left for this entry. null = none yet.</summary>
        private long? Collect(AuditItem me)
        {
            var done = Path.Combine(PendDir, me.Id + ".done");
            var err = Path.Combine(PendDir, me.Id + ".err");
            try
            {
                if (File.Exists(done))
                {
                    var seq = long.Parse(File.ReadAllText(done).Trim(), CultureInfo.InvariantCulture);
                    try { File.Delete(done); } catch (Exception) { }
                    return seq;
                }
                if (File.Exists(err))
                {
                    var msg = File.ReadAllText(err);
                    try { File.Delete(err); } catch (Exception) { }
                    throw new JournalUnavailableException(msg, null);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException) { return null; }
            return null;
        }

        /// <summary>With the lock held: claims the pending entries, writes them with a single fsync and confirms to each follower.</summary>
        private long Lead(AuditItem me, string pendName, List<Action> after)
        {
            Crash("inicio_lider");
            if (pendName != null) { var early = Collect(me); if (early.HasValue) return early.Value; }
            var tp = System.Diagnostics.Stopwatch.StartNew();
            Resync(fullCheck: false);
            AuditProfile.Phase(0, tp.ElapsedTicks); tp.Restart();
            // AUD-01B: claims left by a previous leader that released the lock without finishing (it can only be a crash: claiming, persisting and
            // deleting the claim happen with the lock held). Decided with the journal's evidence, not with anyone's memory.
            long? recoveredMine = RecoverAbandonedClaims(me, after);
            if (recoveredMine.HasValue) return recoveredMine.Value;
            var batch = new List<AuditItem>();
            var claimFiles = new List<string>();
            var mineClaimed = false;
            if (Directory.Exists(PendDir))
            {
                string[] files;
                try { files = Directory.GetFiles(PendDir, "*.json"); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { files = new string[0]; }
                Array.Sort(files, StringComparer.Ordinal);
                foreach (var f in files)
                {
                    if (batch.Count >= MaxBatch - (pendName == null ? 1 : 0)) break;   // the batch never exceeds MaxBatch lines
                    Dictionary<string, object> o;
                    // AUD-01B: DURABLE claim. The entry is renamed to ".reclamada" (atomic: only one leader succeeds) and stays on disk until
                    // its line is persisted and sealed by the head. If the leader dies midway, the claim remains as evidence.
                    var claimed = Path.Combine(PendDir, Path.GetFileNameWithoutExtension(f) + ".reclamada");
                    try { File.Move(f, claimed); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }   // its writer withdrew it
                    try { o = Json.ParseObject(File.ReadAllText(claimed)); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
                    {
                        // Unreadable: not written. The claim stays for reconciliation and the writer receives a definitive error.
                        var bad = Path.GetFileNameWithoutExtension(f); var badId = bad.Length > 20 ? bad.Substring(20) : bad; var reason = ex.GetType().Name;
                        after.Add(() => Reply(badId, ".err", "unreadable audit entry in the queue (" + reason + "): not written"));
                        continue;
                    }
                    var it = new AuditItem
                    {
                        Id = Arg.GetString(o, "id"), Author = Arg.GetString(o, "autor"), Op = Arg.GetString(o, "op"), Estado = Arg.GetString(o, "estado"),
                        Pid = Arg.GetLong(o, "pid") ?? 0, T = o.TryGetValue("t", out var tv) ? tv : Home.Now(),
                        Extra = o.TryGetValue("extra", out var ev) ? ev as Dictionary<string, object> : null,
                    };
                    it.Cid = it.Id;
                    if (it.Id == me.Id) { mineClaimed = true; me.Cid = me.Id; }
                    batch.Add(it);
                    claimFiles.Add(claimed);
                }
                if ((DateTime.UtcNow - _lastClean).TotalSeconds > 30) { _lastClean = DateTime.UtcNow; CleanStaleReplies(); }
            }
            Crash("tras_reclamo");
            AuditProfile.Phase(1, tp.ElapsedTicks); tp.Restart();
            // Another leader already claimed my entry: its reply is sent right after releasing the lock, so we wait (bounded) instead of failing.
            // What I claimed from others is always written: a claimed entry is never lost.
            var meInBatch = pendName == null || mineClaimed;
            if (pendName == null) batch.Add(me);
            if (batch.Count == 0) return AwaitClaimed;
            List<long> seqs;
            try { seqs = WriteBatch(batch); }
            catch (JournalUnavailableException ex)
            {
                // UNCERTAIN state (something may have been written): claims are kept so the next leader decides with the journal.
                var msg = ex.Message + " (uncertain state: reconciled with the journal)";
                foreach (var it in batch) if (it.Id != me.Id) { var id = it.Id; after.Add(() => Reply(id, ".err", msg)); }
                if (meInBatch) throw;
                return AwaitClaimed;   // another leader has my own entry and will reply to me; what I claimed already received its error
            }
            // Persisted and sealed by the head: the claims are no longer needed (with the lock still held).
            foreach (var cf in claimFiles) { try { File.Delete(cf); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { } }
            AuditProfile.Phase(2, tp.ElapsedTicks); tp.Restart();
            AuditProfile.Batch(batch.Count);
            long mySeq = 0;
            for (var i = 0; i < batch.Count; i++)
            {
                if (batch[i].Id == me.Id) mySeq = seqs[i];
                else { var id = batch[i].Id; var text = seqs[i].ToString(CultureInfo.InvariantCulture); after.Add(() => Reply(id, ".done", text)); }
            }
            AuditProfile.Phase(3, tp.ElapsedTicks);
            return meInBatch ? mySeq : AwaitClaimed;
        }

        /// <summary>
        /// AUD-01B: resolves the claims of a leader that crashed. For each claimed entry its cid is looked up in the chain (already repaired or
        /// verified by Resync): if present, it is SEALED with a recovery record (append + fsync + head, which also makes durable the line
        /// the crashed leader may have left without fsync) and only then confirmed to its writer; if absent, it is never rewritten (the writer
        /// may already have reported a failure and acted on it) and it receives a DEFINITIVE error: not recorded as written.
        /// Returns the seq of MY entry if it was among the claimed ones and is recorded as written.
        /// </summary>
        private long? RecoverAbandonedClaims(AuditItem me, List<Action> after)
        {
            if (!Directory.Exists(PendDir)) return null;
            string[] claims;
            try { claims = Directory.GetFiles(PendDir, "*.reclamada"); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            if (claims.Length == 0) return null;
            var byId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in claims)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                byId[name.Length > 20 ? name.Substring(20) : name] = f;
            }
            var found = FindCids(_path, byId.Keys);
            var inQuarantine = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in Quarantined) foreach (var kv in FindCids(q, byId.Keys)) inQuarantine.Add(kv.Key);
            // Seal and evidence BEFORE confirming anything.
            WriteLine("sistema", "recuperacion_reclamos", "reclamos_de_lider_caido", new Dictionary<string, object>
            {
                ["confirmados"] = byId.Keys.Where(found.ContainsKey).Cast<object>().ToList(),
                ["no_escritos"] = byId.Keys.Where(k => !found.ContainsKey(k) && !inQuarantine.Contains(k)).Cast<object>().ToList(),
                ["en_cadena_apartada"] = byId.Keys.Where(inQuarantine.Contains).Cast<object>().ToList(),
                ["motivo"] = "a leader released the lock without finishing (crash), leaving claimed entries",
            });
            long? mine = null;
            string mineError = null;
            foreach (var kv in byId)
            {
                var id = kv.Key;
                if (found.TryGetValue(id, out var seq))
                {
                    if (id == me.Id) mine = seq;
                    else { var text = seq.ToString(CultureInfo.InvariantCulture); after.Add(() => Reply(id, ".done", text)); }
                }
                else
                {
                    var msg = inQuarantine.Contains(id)
                        ? "audit entry written to a chain that was later set aside to quarantine: uncertain state, reconcile"
                        : "audit entry not written: the leader that claimed it crashed before persisting it (nothing was written)";
                    if (id == me.Id) mineError = msg; else after.Add(() => Reply(id, ".err", msg));
                }
                try { File.Delete(kv.Value); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            if (mineError != null) throw new JournalUnavailableException(mineError, null);
            return mine;
        }

        /// <summary>cid → seq of the lines of <paramref name="path"/> whose cid is in <paramref name="ids"/> (recovery path, infrequent).</summary>
        private static Dictionary<string, long> FindCids(string path, IEnumerable<string> ids)
        {
            var want = new HashSet<string>(ids, StringComparer.Ordinal);
            var res = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!File.Exists(path) || want.Count == 0) return res;
            string text;
            try { text = ReadFrom(path, 0); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return res; }
            foreach (var raw in text.Split('\n'))
            {
                if (raw.IndexOf("\"cid\":", StringComparison.Ordinal) < 0) continue;
                Dictionary<string, object> o;
                try { o = Json.ParseObject(raw); } catch (FormatException) { continue; }
                var cid = Arg.GetString(o, "cid");
                if (cid != null && want.Contains(cid)) res[cid] = Arg.GetLong(o, "seq") ?? 0;
            }
            return res;
        }

        private void Reply(string id, string ext, string content)
        {
            try { File.WriteAllText(Path.Combine(PendDir, id + ext), content, new UTF8Encoding(false)); AuditProfile.ReplySent(id + ext); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                /* the follower runs out its wait and fails without rewriting; the error is counted for diagnostics */
                AuditProfile.ReplyError(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void CleanStaleReplies()
        {
            try
            {
                foreach (var pattern in new[] { "*.done", "*.err", "*.t" })
                    foreach (var f in Directory.GetFiles(PendDir, pattern))
                        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > 120) File.Delete(f);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Phase 1: durable intent before touching the model. If it throws, the caller must NOT modify anything.</summary>
        public string Intent(string author, string op, string target, Dictionary<string, object> extra = null)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 16);
            var e = extra != null ? new Dictionary<string, object>(extra) : new Dictionary<string, object>();
            e["intencion"] = id;
            e["objetivo"] = target;
            Append(author, op, "intencion", e);
            return id;
        }

        /// <summary>Phase 2: result. If it fails, the intent is left without a result and <see cref="PendingIntents"/> returns it for reconciliation.</summary>
        public void Outcome(string author, string op, string intentId, string estado, Dictionary<string, object> extra = null)
        {
            var e = extra != null ? new Dictionary<string, object>(extra) : new Dictionary<string, object>();
            e["intencion"] = intentId;
            e["fase"] = "resultado";
            Append(author, op, estado, e);
        }

        public static Dictionary<string, object> FindCommitReceipt(string path, string attempt, string session, string agent, string doc, string docPrint)
        {
            if (string.IsNullOrEmpty(attempt) || string.IsNullOrEmpty(doc) || string.IsNullOrEmpty(docPrint) || !File.Exists(path))
                return null;
            Dictionary<string, object> result = null;
            string intent = null;
            foreach (var raw in ReadLinesShared(path))
            {
                if (raw.IndexOf("\"intento_id\"", StringComparison.Ordinal) < 0 && (intent == null || raw.IndexOf(intent, StringComparison.Ordinal) < 0))
                    continue;
                Dictionary<string, object> entry;
                try { entry = Json.ParseObject(raw); }
                catch (FormatException) { continue; }
                var op = Arg.GetString(entry, "op");
                if (op != "commit" && op != "aplicar_cola") continue;
                var candidate = Arg.GetString(entry, "intencion");
                if (candidate == null) continue;
                var state = Arg.GetString(entry, "estado");
                if (state == "intencion" && Arg.GetString(entry, "intento_id") == attempt &&
                    Arg.GetString(entry, "objetivo") == doc && Arg.GetString(entry, "huella_doc") == docPrint &&
                    ((Arg.GetString(entry, "sesion_intento") ?? Arg.GetString(entry, "autor")) == session ||
                        (!string.IsNullOrEmpty(agent) && Arg.GetString(entry, "agente") == agent)))
                {
                    var planId = Arg.GetString(entry, "plan_id");
                    if (result != null && Arg.GetString(result, "plan_id") != planId) return null;
                    intent = candidate;
                    result = new Dictionary<string, object>
                    {
                        ["intento_id"] = attempt, ["estado"] = Status.Desconocido, ["doc"] = doc, ["huella_doc"] = docPrint,
                        ["plan_id"] = planId, ["inicio"] = Arg.GetString(entry, "t"),
                    };
                }
                else if (candidate == intent && Arg.GetString(entry, "fase") == "resultado")
                {
                    result["estado"] = Arg.GetString(entry, "estado_recibo") ?? state ?? Status.Desconocido;
                    result["fin"] = Arg.GetString(entry, "t");
                    result["rev"] = Arg.GetLong(entry, "rev");
                }
            }
            return result;
        }

        /// <summary>Intents without a result (the write may or may not have happened): input to reconciliation.</summary>
        public static List<Dictionary<string, object>> PendingIntents(string path)
        {
            var open = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            if (!File.Exists(path)) return new List<Dictionary<string, object>>();
            foreach (var raw in ReadLinesShared(path))
            {
                Dictionary<string, object> o;
                try { o = Json.ParseObject(raw); } catch (FormatException) { continue; }
                if (Arg.GetString(o, "op") == "cuarentena" && o.TryGetValue("intenciones_pendientes", out var ip) && ip is List<object> carried)
                {
                    // rc3: what was pending in a set-aside chain stays pending (it is not hidden by quarantining the chain).
                    foreach (var c in carried.OfType<Dictionary<string, object>>())
                    {
                        var cid = Arg.GetString(c, "intencion");
                        if (cid != null) { c["desde_cuarentena"] = Arg.GetString(o, "apartado"); open[cid] = c; }
                    }
                    continue;
                }
                var id = Arg.GetString(o, "intencion");
                if (id == null) continue;
                if (Arg.GetString(o, "estado") == "intencion") open[id] = o;
                else open.Remove(id);
            }
            return open.Values.ToList();
        }

        // ---------------- internal ----------------

        private long WriteLine(string author, string op, string estado, Dictionary<string, object> extra) =>
            WriteBatch(new List<AuditItem> { new AuditItem { Author = author, Op = op, Estado = estado, Extra = extra, Pid = PidCache, T = Home.Now() } })[0];

        /// <summary>
        /// Writes N lines with ONE append, ONE fsync and ONE head update. seq and prev are assigned in order under the lock.
        /// Only once the fsync and the head are done is the in-memory state updated and the caller allowed to confirm to anyone.
        /// </summary>
        private List<long> WriteBatch(List<AuditItem> items)
        {
            var prev = _prev; var seq = _seq;
            var seqs = new List<long>(items.Count);
            var buf = new MemoryStream();
            long lastLineStart = 0;
            var index = 0;
            foreach (var it in items)
            {
                seq++;
                index++;
                var e = new Dictionary<string, object>
                {
                    ["seq"] = seq,
                    ["t"] = it.T ?? Home.Now(),
                    ["pid"] = it.Pid,
                    ["autor"] = it.Author,
                    ["op"] = it.Op,
                    ["estado"] = it.Estado,
                    ["prev"] = prev,
                };
                if (it.Extra != null) foreach (var kv in it.Extra) if (!e.ContainsKey(kv.Key)) e[kv.Key] = kv.Value;
                if (it.Cid != null) e["cid"] = it.Cid;
                // AUD-01: batch marker so verification can tell a complete unconfirmed batch from a truncated tail.
                if (items.Count > 1) e["lote"] = index.ToString(CultureInfo.InvariantCulture) + "/" + items.Count.ToString(CultureInfo.InvariantCulture);
                var line = Json.Write(e);
                lastLineStart = buf.Length;
                var b = Encoding.UTF8.GetBytes(line + "\n");
                buf.Write(b, 0, b.Length);
                prev = Home.Sha256Hex(line);
                seqs.Add(seq);
            }
            try
            {
                foreach (var it in items) FaultBeforeWrite?.Invoke(it.Op);
                Crash("antes_escribir");
                var bytes = buf.ToArray();
                long offset, length;
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
                {
                    offset = fs.Position;
                    var tw = System.Diagnostics.Stopwatch.StartNew();
                    if (CrashPoint != null && CrashPoint("durante_escritura"))
                    {
                        // Tests only: write cut in half (leaves complete lines and a fragment, like a real crash).
                        fs.Write(bytes, 0, bytes.Length / 2);
                        fs.Flush(true);
                        throw new AuditCrashException("durante_escritura");
                    }
                    fs.Write(bytes, 0, bytes.Length);
                    Crash("tras_escribir_antes_fsync");
                    var tf = System.Diagnostics.Stopwatch.StartNew();
                    fs.Flush(true);
                    AuditProfile.Write(tw.ElapsedTicks - tf.ElapsedTicks, tf.ElapsedTicks);
                    length = fs.Length;
                }
                Crash("tras_fsync_antes_cabeza");
                var th = System.Diagnostics.Stopwatch.StartNew();
                Home.WriteAtomic(HeadPath(_path), Json.Write(new Dictionary<string, object> { ["seq"] = seq, ["ultimo"] = prev, ["longitud"] = length }));
                AuditProfile.Head(th.ElapsedTicks);
                Crash("tras_cabeza");
                _knownLength = length;
                _lastOffset = offset + lastLineStart;
                _knownWrite = File.GetLastWriteTimeUtc(_path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Anything left half-written is detected by the next Resync (tail without a newline) and goes to quarantine.
                _knownLength = -1;
                throw new JournalUnavailableException("audit not writable: " + ex.Message, ex);
            }
            _prev = prev;
            _seq = seq;
            return seqs;
        }

        /// <summary>Brings seq/prev up to date with what other processes wrote. With fullCheck it verifies the whole chain.
        /// If something does not add up (broken chain, shortened file, unterminated last line) → quarantine + new chain.</summary>
        private void Resync(bool fullCheck)
        {
            long len;
            try { len = File.Exists(_path) ? new FileInfo(_path).Length : 0; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { throw new JournalUnavailableException("audit unreadable: " + ex.Message, ex); }
            if (!fullCheck && len == _knownLength && SameLastRecord() && File.GetLastWriteTimeUtc(_path) == _knownWrite) return;
            if (!fullCheck && _knownLength >= 0 && len > _knownLength && SameLastRecord())
            {
                // Only the new part: cheap even if the file is large.
                string tail;
                try { tail = ReadFrom(_path, _knownLength); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { throw new JournalUnavailableException("audit unreadable: " + ex.Message, ex); }
                var errors = new List<string>();
                var prev = _prev; var seq = _seq;
                if (!tail.EndsWith("\n", StringComparison.Ordinal)) errors.Add("unterminated last line");
                var off = _knownLength; var lastOff = _lastOffset;
                foreach (var raw in tail.Split('\n'))
                {
                    if (raw.Length == 0) continue;
                    lastOff = off;
                    off += Encoding.UTF8.GetByteCount(raw) + 1;
                    CheckLine(raw, ref prev, ref seq, errors);
                }
                if (errors.Count == 0) { _prev = prev; _seq = seq; _knownLength = len; _lastOffset = lastOff; _knownWrite = File.GetLastWriteTimeUtc(_path); return; }
                // AUD-01: a tail cut by another writer's crash is set aside (with evidence) if it does not touch confirmed records.
                if (TryRepairTail(out _)) return;
                Quarantine(errors);
                return;
            }
            var v = Verify(_path);
            if (v.Ok)
            {
                _prev = v.LastHash ?? ""; _seq = v.Lines; _knownLength = len; _lastOffset = v.LastOffset;
                _knownWrite = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : default(DateTime);
                // AUD-01: batch persisted but not confirmed (crash between the fsync and the head). It is an intact chain: it is sealed with a record
                // documenting it, instead of declaring it corrupt.
                if (v.HeadLag > 1)
                    WriteLine("sistema", "recuperacion_cabeza", "lote_no_confirmado_sellado", new Dictionary<string, object>
                    {
                        ["registros"] = v.HeadLag, ["desde_seq"] = v.Lines - v.HeadLag + 1, ["hasta_seq"] = v.Lines,
                        ["motivo"] = "crash between the batch fsync and the head update; no writer received confirmation",
                    });
                return;
            }
            if (TryRepairTail(out _)) return;
            Quarantine(v.Errors);
        }

        /// <summary>Is the last record we know still intact in its place? (single-line read).</summary>
        private bool SameLastRecord()
        {
            if (_seq == 0 || _lastOffset < 0) return _seq == 0;
            try
            {
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var n = (int)(_knownLength - _lastOffset);
                    if (n <= 1 || fs.Length < _knownLength) return false;
                    var buf = new byte[n];
                    fs.Seek(_lastOffset, SeekOrigin.Begin);
                    var read = 0;
                    while (read < n) { var k = fs.Read(buf, read, n - read); if (k <= 0) return false; read += k; }
                    if (buf[n - 1] != (byte)'\n') return false;
                    return Home.Sha256Hex(Encoding.UTF8.GetString(buf, 0, n - 1)) == _prev;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        private void Quarantine(List<string> errors)
        {
            List<object> pending;
            try { pending = PendingIntents(_path).Select(d => (object)new Dictionary<string, object> { ["intencion"] = Arg.GetString(d, "intencion"), ["op"] = Arg.GetString(d, "op"), ["objetivo"] = Arg.GetString(d, "objetivo"), ["autor"] = Arg.GetString(d, "autor"), ["t"] = Arg.GetString(d, "t") }).ToList(); }
            catch (Exception) { pending = new List<object>(); }
            var dest = _path + ".cuarentena-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string sha;
            try
            {
                sha = Home.FileSha256(_path);
                File.Move(_path, dest);
                if (File.Exists(HeadPath(_path))) File.Move(HeadPath(_path), dest + ".cabeza");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new JournalUnavailableException("invalid audit (" + string.Join("; ", errors.Take(3)) + ") and it could not be set aside: " + ex.Message, ex);
            }
            Quarantined.Add(dest);
            _prev = ""; _seq = 0; _knownLength = 0; _lastOffset = -1;
            WriteLine("sistema", "cuarentena", "cadena_nueva", new Dictionary<string, object>
            {
                ["apartado"] = Path.GetFileName(dest), ["sha256_apartado"] = sha, ["errores"] = errors.Take(10).Cast<object>().ToList(),
                ["intenciones_pendientes"] = pending,
            });
        }

        private T WithFileLock<T>(Func<T> body)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rnd = new Random();
            while (true)
            {
                FileStream fs;
                try { fs = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (DirectoryNotFoundException ex) { throw new JournalUnavailableException("audit folder not available: " + ex.Message, ex); }
                catch (IOException ex)
                {
                    if (sw.Elapsed > LockTimeout) throw new JournalUnavailableException("audit lock not obtained within " + LockTimeout.TotalSeconds + " s", ex);
                    Thread.Sleep(2 + rnd.Next(10));
                    continue;
                }
                catch (UnauthorizedAccessException ex) { throw new JournalUnavailableException("audit lock without permission: " + ex.Message, ex); }
                var waited = sw.ElapsedTicks;
                var held = System.Diagnostics.Stopwatch.StartNew();
                using (fs)
                {
                    try { return body(); }
                    finally { AuditProfile.Lock(waited, held.ElapsedTicks); }
                }
            }
        }

        private static string ReadFrom(string path, long offset)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                using (var r = new StreamReader(fs, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        private static IEnumerable<string> ReadLinesShared(string path)
        {
            var text = ReadFrom(path, 0);
            return text.Split('\n').Where(l => l.Length > 0);
        }

        private static void CheckLine(string raw, ref string prev, ref long n, List<string> errors) => CheckLine(raw, ref prev, ref n, errors, out _);

        private static void CheckLine(string raw, ref string prev, ref long n, List<string> errors, out string lote)
        {
            n++;
            lote = null;
            Dictionary<string, object> o;
            try { o = Json.ParseObject(raw); }
            catch (FormatException) { errors.Add("line " + n + " unreadable"); prev = Home.Sha256Hex(raw); return; }
            var p = Arg.GetString(o, "prev") ?? "";
            if (p != prev) errors.Add("line " + n + ": broken chain (prev does not match)");
            long seq;
            try { seq = Convert.ToInt64(o.TryGetValue("seq", out var s) ? s : 0L, CultureInfo.InvariantCulture); }
            catch (Exception) { seq = -1; }
            if (seq != n) errors.Add("line " + n + ": seq " + seq + " out of order");
            lote = Arg.GetString(o, "lote");
            prev = Home.Sha256Hex(raw);
        }

        /// <summary>"i/n" → (i, n). (0, 0) if there is no batch marker (individual record).</summary>
        private static void ParseLote(string lote, out int i, out int n)
        {
            i = 0; n = 0;
            if (string.IsNullOrEmpty(lote)) return;
            var k = lote.IndexOf('/');
            if (k <= 0 || !int.TryParse(lote.Substring(0, k), NumberStyles.None, CultureInfo.InvariantCulture, out i) ||
                !int.TryParse(lote.Substring(k + 1), NumberStyles.None, CultureInfo.InvariantCulture, out n)) { i = -1; n = -1; }
        }

        /// <summary>
        /// AUD-01: compares the tail with the head. The head can never be ahead or point to another hash. It may lag behind only in the
        /// state left by a crash between a batch fsync and the head update: either ONE individual record, or ONE
        /// COMPLETE batch (all its lines with the "lote" marker i/n, i = 1..n in order, n = lag). Those lines are persisted but NOT confirmed
        /// (nobody received their seq), and the next write seals them. Returns the tolerated lag (0 if the head is up to date).
        /// </summary>
        private static long CheckHead(string path, List<string> hashes, List<string> lotes, List<string> errors)
        {
            var hp = HeadPath(path);
            if (!File.Exists(hp)) return 0; // chains older than rc3
            Dictionary<string, object> h;
            try { h = Json.ParseObject(File.ReadAllText(hp)); }
            catch (Exception) { errors.Add("head unreadable"); return 0; }
            long seq;
            try { seq = Convert.ToInt64(h.TryGetValue("seq", out var s) ? s : 0L, CultureInfo.InvariantCulture); } catch (Exception) { seq = -1; }
            var last = Arg.GetString(h, "ultimo") ?? "";
            if (seq < 0) { errors.Add("head unreadable"); return 0; }
            if (seq > hashes.Count) { errors.Add("records missing at the end (the head says " + seq + ", there are " + hashes.Count + ")"); return 0; }
            if (seq >= 1 && hashes[(int)seq - 1] != last) { errors.Add("record " + seq + " does not match the head (edited)"); return 0; }
            var lag = hashes.Count - seq;
            if (lag <= 1) return lag;
            if (lag > MaxBatch) { errors.Add("the head is " + lag + " records behind (more than one batch)"); return 0; }
            for (var k = 0; k < lag; k++)
            {
                ParseLote(lotes[(int)seq + k], out var i, out var n);
                if (i != k + 1 || n != lag) { errors.Add("the head is " + lag + " records behind and the tail is not a complete batch"); return 0; }
            }
            return lag;
        }

        public sealed class VerifyResult
        {
            public long Lines;
            public string LastHash;
            public long LastOffset = -1;
            /// <summary>Records persisted beyond the head (unconfirmed batch after a crash). 0 = head up to date.</summary>
            public long HeadLag;
            public List<string> Errors = new List<string>();
            public bool Ok => Errors.Count == 0;
        }

        /// <summary>Checks the whole chain. A cut last line (crash midway) is an error, not hidden: repair is separate.</summary>
        public static VerifyResult Verify(string path)
        {
            var r = new VerifyResult();
            if (!File.Exists(path)) return r;
            var text = ReadFrom(path, 0);
            if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)) r.Errors.Add("unterminated last line");
            string prev = "";
            long n = 0, off = 0;
            var hashes = new List<string>();
            var lotes = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                if (raw.Length == 0) { off += 1; continue; }
                r.LastOffset = off;
                off += Encoding.UTF8.GetByteCount(raw) + 1;
                CheckLine(raw, ref prev, ref n, r.Errors, out var lote);
                hashes.Add(prev);
                lotes.Add(lote);
            }
            r.Lines = n;
            r.LastHash = n == 0 ? null : prev;
            r.HeadLag = CheckHead(path, hashes, lotes, r.Errors);
            return r;
        }

        /// <summary>
        /// AUD-01: conservative repair of a tail left by a crash. It only acts if EVERYTHING before is a valid chain and what is left over
        /// is beyond the head (persisted but never confirmed): a final fragment without a newline and/or the lines of an incomplete
        /// batch. What is set aside is kept intact in "&lt;auditoria&gt;.fragmento-…" (evidence), the file is cut at the last valid
        /// boundary, the head is rewritten and a "recuperacion_cola" record documenting it is appended. It never cuts confirmed records
        /// (≤ head); an edit, a deletion or a broken chain still go to quarantine.
        /// </summary>
        private bool TryRepairTail(out string note)
        {
            note = null;
            byte[] all;
            try { all = File.ReadAllBytes(_path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
            var lastNl = Array.LastIndexOf(all, (byte)'\n');
            var completeLen = lastNl + 1;   // bytes of complete lines
            var text = Encoding.UTF8.GetString(all, 0, completeLen);
            var errors = new List<string>();
            string prev = "";
            long n = 0, off = 0;
            var hashes = new List<string>(); var lotes = new List<string>(); var ends = new List<long>();
            foreach (var raw in text.Split('\n'))
            {
                if (raw.Length == 0) { off += 1; continue; }
                off += Encoding.UTF8.GetByteCount(raw) + 1;
                CheckLine(raw, ref prev, ref n, errors, out var lote);
                hashes.Add(prev); lotes.Add(lote); ends.Add(off);
            }
            if (errors.Count > 0) return false;                        // the complete part is not a valid chain: real corruption
            // Incomplete batch at the end: its lines are removed (it was never confirmed).
            var keep = hashes.Count;
            if (keep > 0)
            {
                ParseLote(lotes[keep - 1], out var li, out var ln);
                if (li > 0 && ln > 0 && li < ln) keep -= li;
            }
            var keepBytes = keep == 0 ? 0 : ends[keep - 1];
            if (keepBytes == all.Length) return false;                 // nothing to set aside: the problem is something else
            // The head must fall within what is kept and match its hash.
            long headSeq = 0;
            var hp = HeadPath(_path);
            if (File.Exists(hp))
            {
                Dictionary<string, object> h;
                try { h = Json.ParseObject(File.ReadAllText(hp)); } catch (Exception) { return false; }
                try { headSeq = Convert.ToInt64(h.TryGetValue("seq", out var s) ? s : 0L, CultureInfo.InvariantCulture); } catch (Exception) { return false; }
                var last = Arg.GetString(h, "ultimo") ?? "";
                if (headSeq < 0 || headSeq > keep) return false;       // confirmed records would be lost: quarantine
                if (headSeq >= 1 && hashes[(int)headSeq - 1] != last) return false;
            }
            var dest = _path + ".fragmento-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var dropped = new byte[all.Length - keepBytes];
            Array.Copy(all, keepBytes, dropped, 0, dropped.Length);
            try
            {
                // AUD-10: the fragment must be durable and readable BEFORE truncating the journal. It is written with WriteThrough + fsync to a
                // temp file, re-read and compared byte for byte, and only then renamed. If anything fails, nothing is truncated.
                var tmp = dest + ".tmp";
                using (var fw = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fw.Write(dropped, 0, dropped.Length);
                    fw.Flush(true);
                }
                Crash("reparacion_tras_fragmento_tmp");
                var back = File.ReadAllBytes(tmp);
                if (back.Length != dropped.Length || !back.SequenceEqual(dropped)) { File.Delete(tmp); return false; }
                File.Move(tmp, dest);
                Crash("reparacion_antes_truncar");
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
                {
                    fs.SetLength(keepBytes);
                    fs.Flush(true);
                }
                Crash("reparacion_tras_truncar");
                var lastHash = keep == 0 ? "" : hashes[keep - 1];
                Home.WriteAtomic(hp, Json.Write(new Dictionary<string, object> { ["seq"] = (long)keep, ["ultimo"] = lastHash, ["longitud"] = keepBytes }));
                _prev = lastHash; _seq = keep; _knownLength = keepBytes;
                _lastOffset = keep == 0 ? -1 : (keep == 1 ? 0 : ends[keep - 2]);
                _knownWrite = File.GetLastWriteTimeUtc(_path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
            Repaired.Add(dest);
            note = "incomplete tail set aside: " + (hashes.Count - keep) + " lines of an unfinished batch and " + (all.Length - completeLen) + " bytes without a newline";
            WriteLine("sistema", "recuperacion_cola", "cola_incompleta_apartada", new Dictionary<string, object>
            {
                ["apartado"] = Path.GetFileName(dest), ["sha256_apartado"] = Home.FileSha256(dest), ["bytes"] = (long)dropped.Length,
                ["lineas_descartadas"] = (long)(hashes.Count - keep), ["motivo"] = "write interrupted beyond the head (never confirmed)",
            });
            return true;
        }

        /// <summary>Incomplete-tail files set aside by this instance (evidence of a repair, not of corruption).</summary>
        public readonly List<string> Repaired = new List<string>();
    }

    /// <summary>Simulated crash at a point of the audit protocol (tests only). No I/O handler catches it.</summary>
    public sealed class AuditCrashException : Exception
    {
        public AuditCrashException(string point) : base("simulated crash at " + point) { Point = point; }
        public string Point { get; }
    }

    /// <summary>
    /// Per-process AuditLog cost counters (issue #27): lock wait, time with the lock held, write, fsync and head.
    /// They only add ticks with Interlocked; they do not change behavior. Read by the test probe and diagnostics.
    /// </summary>
    public static class AuditProfile
    {
        private static long _n, _wait, _held, _write, _fsync, _head, _maxWait, _maxHeld;
        private static readonly object _samplesLock = new object();
        private static readonly List<long> _waitSamples = new List<long>(), _heldSamples = new List<long>();
        internal static void Lock(long waitTicks, long heldTicks)
        {
            Interlocked.Increment(ref _n); Interlocked.Add(ref _wait, waitTicks); Interlocked.Add(ref _held, heldTicks);
            lock (_samplesLock) { if (_waitSamples.Count < 100000) { _waitSamples.Add(waitTicks); _heldSamples.Add(heldTicks); } }
        }
        internal static void Write(long writeTicks, long fsyncTicks) { Interlocked.Add(ref _write, writeTicks); Interlocked.Add(ref _fsync, fsyncTicks); }
        internal static void Head(long ticks) => Interlocked.Add(ref _head, ticks);
        private static long _replyErrors; private static string _lastReplyError;
        private static readonly List<string> _sent = new List<string>();
        internal static void ReplySent(string name) { lock (_sent) if (_sent.Count < 20) _sent.Add(name); }
        public static string RepliesSent { get { lock (_sent) return string.Join(",", _sent); } }
        internal static void ReplyError(string msg) { Interlocked.Increment(ref _replyErrors); _lastReplyError = msg; }
        public static string ReplyErrors => Interlocked.Read(ref _replyErrors) + (_lastReplyError == null ? "" : " last: " + _lastReplyError);
        private static readonly long[] _phase = new long[4];
        internal static void Phase(int i, long ticks) => Interlocked.Add(ref _phase[i], ticks);
        public static string PhaseReport() => string.Format(CultureInfo.InvariantCulture, "resync={0:n1}ms claim={1:n1}ms batch_write={2:n1}ms replies={3:n1}ms (process totals)",
            Ms(Interlocked.Read(ref _phase[0])), Ms(Interlocked.Read(ref _phase[1])), Ms(Interlocked.Read(ref _phase[2])), Ms(Interlocked.Read(ref _phase[3])));
        private static long _batches, _batched, _maxBatch;
        internal static void Batch(int n)
        {
            Interlocked.Increment(ref _batches); Interlocked.Add(ref _batched, n);
            long cur; while (n > (cur = Interlocked.Read(ref _maxBatch)) && Interlocked.CompareExchange(ref _maxBatch, n, cur) != cur) { }
        }
        public static string BatchReport() => string.Format(CultureInfo.InvariantCulture, "batches={0} entries={1} max_batch={2} mean_batch={3:n2}",
            Interlocked.Read(ref _batches), Interlocked.Read(ref _batched), Interlocked.Read(ref _maxBatch), Interlocked.Read(ref _batched) / (double)Math.Max(1, Interlocked.Read(ref _batches)));
        private static double Ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static double Pct(List<long> v, double p) { if (v.Count == 0) return 0; var s = v.OrderBy(x => x).ToList(); return Ms(s[Math.Min(s.Count - 1, (int)Math.Ceiling(p * s.Count) - 1)]); }
        public static string Report()
        {
            lock (_samplesLock)
            {
                var n = Math.Max(1, Interlocked.Read(ref _n));
                return string.Format(CultureInfo.InvariantCulture,
                    "n={0} wait_mean={1:n2}ms wait_p50={2:n2} wait_p95={3:n2} wait_max={4:n2} held_mean={5:n2}ms held_p95={6:n2} write_mean={7:n3}ms fsync_mean={8:n2}ms head_mean={9:n2}ms",
                    n, Ms(_wait) / n, Pct(_waitSamples, 0.5), Pct(_waitSamples, 0.95), _waitSamples.Count == 0 ? 0 : Ms(_waitSamples.Max()),
                    Ms(_held) / n, Pct(_heldSamples, 0.95), Ms(_write) / n, Ms(_fsync) / n, Ms(_head) / n);
            }
        }
    }

    /// <summary>
    /// Common two-step protocol (R01) for any write outside the task engine (queue, exec):
    /// durable intent → modify → result. If the intent cannot be persisted, nothing is modified.
    /// rc3: if the result cannot be persisted, the modification ALREADY happened (no rollback is faked): the BIM result is returned marked
    /// with <paramref name="incomplete"/> (incomplete audit + intent id, which stays in PendingIntents for reconciliation), so
    /// the client cannot read it as fully verified.
    /// </summary>
    public static class AuditGate
    {
        public const string Incompleta = "auditoria_incompleta";

        public static T Apply<T>(AuditLog audit, string author, string op, string target, Dictionary<string, object> extra,
            Func<T> apply, Func<T, string> status, Func<string, T> refused, Func<T, string, T> incomplete)
        {
            string intent;
            try
            {
                if (audit == null) throw new JournalUnavailableException("audit not initialized", null);
                intent = audit.Intent(author, op, target, extra);
            }
            catch (JournalUnavailableException ex) { return refused("auditoria_no_disponible: nothing was modified (" + ex.Message + ")"); }
            var result = apply();
            try { audit.Outcome(author, op, intent, status(result), extra); }
            catch (JournalUnavailableException ex)
            {
                return incomplete(result, Incompleta + ": the operation finished (its result is real and was not rolled back), but the result was not recorded in the audit (" +
                                          ex.Message + "). Intent " + intent + " pending reconciliation.");
            }
            return result;
        }
    }
}
