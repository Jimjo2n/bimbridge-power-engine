// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Capability authority independent of the agent (0.6.1, R03).
    ///
    /// Four distinct sets:
    /// 1. requested: what the agent asks for (MCP argument <c>capacidades=</c>). NEVER authorizes anything on its own.
    /// 2. authorized: grants in <see cref="GrantStore"/>, created only by a person from the Revit UI
    ///    (no MCP operation creates them), signed with HMAC, bound to task + document (+ fingerprint) + capabilities + scope + expiry.
    /// 3. used: what the Roslyn analysis found in the code plus the task's barriers.
    /// 4. rejected: used or required ones that have no current grant.
    ///
    /// Checked at start AND at the point of execution (before each batch, right before each unit and before each barrier),
    /// so a revocation or an expiry stops the task at the next unit.
    ///
    /// Declared limit: the HMAC key lives in the user profile. A process of the same user that reads that key (or
    /// TrustedInteractive code inside Revit) can forge grants. It protects against an agent that only speaks MCP, not against the local user.
    /// </summary>
    public static class Capability
    {
        /// <summary>Modify the model (recipe transactions).</summary>
        public const string Modify = "revit_modify";
        public const string Save = "revit_save";
        public const string Sync = "revit_sync";
        public const string Export = "revit_export";
        public const string FileWrite = "write_file";
        /// <summary>Reconcile a doubtful unit as NOT applied (causes it to run again).</summary>
        public const string ReconcileNotApplied = "reconcile_not_applied";
        /// <summary>Autonomously run a recipe that is NOT one of the installed ones (user file, inline codigo=, saved script).
        /// The grant binds task + document, and the task binds the recipe's exact SHA-256 (content fingerprint): approving means approving THAT code.</summary>
        public const string ExternalRecipe = "external_recipe";
        /// <summary>DLL references outside the trusted list.</summary>
        public const string ExternalReference = "external_reference";

        /// <summary>The only things that run without a grant: model reads and the task's own artifacts.</summary>
        public static readonly string[] LowRisk = { };

        public static readonly string[] Grantable = { Modify, Save, Sync, Export, FileWrite, ReconcileNotApplied, ExternalRecipe };

        public static string ForBarrier(string barrier)
        {
            switch (barrier)
            {
                case "save": return Save;
                case "sync": return Sync;
                case "pdf": return Export;
                default: return Export; // barriers declared by the recipe produce files outside the model
            }
        }
    }

    public sealed class Grant
    {
        public string Id;
        public string Task;
        public string DocKey;
        /// <summary>Model fingerprint (detects a different file at the same path). Empty = any fingerprint for that key.</summary>
        public string DocPrint;
        public List<string> Capabilities = new List<string>();
        /// <summary>Optional element scope (UniqueId or id); empty = the whole task.</summary>
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
                ["id"] = Id, ["task"] = Task, ["doc"] = DocKey, ["doc_fingerprint"] = DocPrint ?? "",
                ["capabilities"] = Capabilities.OrderBy(x => x, StringComparer.Ordinal).Cast<object>().ToList(),
                ["scope"] = Scope.Cast<object>().ToList(),
                ["created"] = Created, ["expires"] = Expires, ["approved_by"] = ApprovedBy, ["revoked"] = Revoked ?? "",
            };
            if (withSignature) d["signature"] = Signature;
            return d;
        }

        public static Grant FromJson(Dictionary<string, object> o) => new Grant
        {
            Id = Arg.GetString(o, "id"), Task = Arg.GetString(o, "task"), DocKey = Arg.GetString(o, "doc"), DocPrint = Arg.GetString(o, "doc_fingerprint"),
            Capabilities = Arg.GetStringList(o, "capabilities").ToList(), Scope = Arg.GetStringList(o, "scope").ToList(),
            Created = Arg.GetString(o, "created"), Expires = Arg.GetString(o, "expires"), ApprovedBy = Arg.GetString(o, "approved_by"),
            Revoked = string.IsNullOrEmpty(Arg.GetString(o, "revoked")) ? null : Arg.GetString(o, "revoked"), Signature = Arg.GetString(o, "signature"),
        };

        public bool ValidAt(DateTime now) =>
            Revoked == null && DateTime.TryParse(Expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var e) && e > now;
    }

    /// <summary>An agent's request awaiting a person.</summary>
    public sealed class GrantRequest
    {
        public string Id, Task, DocKey, DocPrint, DocTitle, Client, Created, Objective;
        public List<string> Capabilities = new List<string>();
    }

    /// <summary>Grant store (%LOCALAPPDATA%\Bimbridge\authorizations). Only <see cref="Approve"/>/<see cref="Revoke"/> write,
    /// which the add-in calls from a Revit dialog answered by a person.</summary>
    public sealed class GrantStore
    {
        private readonly string _dir;
        private readonly byte[] _key;
        private readonly string _requests;
        private readonly object _lock = new object();

        public GrantStore(string dir = null)
        {
            _dir = dir ?? Home.Sub("authorizations");
            Directory.CreateDirectory(_dir);
            _requests = LegacyAliases.ResolveExisting(_dir, Path.Combine(_dir, "requests"));
            Directory.CreateDirectory(_requests);
            _key = LoadOrCreateKey(LegacyAliases.ResolveExisting(_dir, Path.Combine(_dir, "key.bin")));
        }

        public string Dir => _dir;

        /// <summary>Key file header since rc3. The content is the key in Base64: Base64 is encoding, NOT encryption;
        /// the protection is the user profile ACL (DPAPI is left for 0.7.0, see DECISIONES_0.6.1.md).</summary>
        public const string KeyHeader = "BIMBRIDGE-GRANT-KEY v1 base64";
        public const int KeyBytes = 32;

        /// <summary>Grant key error. The add-in treats it as "no authority" (writes are denied), never regenerates.</summary>
        public sealed class GrantKeyException : Exception { public GrantKeyException(string m) : base(m) { } }

        /// <summary>
        /// Policy (rc3):
        /// - exists and is valid (v1 format or rc2's bare Base64) → used;
        /// - exists but invalid/truncated → GrantKeyException (not regenerated: it would invalidate or mask grants);
        /// - does not exist and there are signed grants → GrantKeyException (someone deleted it; a person decides);
        /// - does not exist and there are no grants → created atomically (temp + Move without overwrite): if another process wins the race,
        ///   its key is read. There are never two keys.
        /// </summary>
        public static byte[] LoadOrCreateKey(string path)
        {
            if (File.Exists(path)) return ReadKey(path);
            var dir = Path.GetDirectoryName(path);
            // alpha3 (P1-A): root cause of the rc3 race: on Unix, .NET's File.Move (without overwrite) checks the target and then
            // calls rename(), which DOES overwrite; two creators could both "win" and end up with different keys. Now creation
            // is serialized with an exclusive lock (FileShare.None; the OS releases it if the process dies) and re-read inside the lock.
            var lockPath = path + ".lock";
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                FileStream gate = null;
                try { gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(5); continue; }
                catch (IOException ex) { throw new GrantKeyException("could not obtain the grant key lock: " + ex.Message); }
                using (gate)
                {
                    if (File.Exists(path)) return ReadKey(path);
                    if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "c_*.json").Any())
                        throw new GrantKeyException(path + " is missing but there are grants signed with it: no new key is created. " +
                                                    "Restore the key or delete the grants by hand (a person's decision).");
                    var key = new byte[KeyBytes];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
                    var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                    // A publish failure is a controlled error (no authority), not an exception that brings down the process.
                    try
                    {
                        File.WriteAllText(tmp, KeyHeader + "\n" + Convert.ToBase64String(key) + "\n", new UTF8Encoding(false));
                        File.Move(tmp, path);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new GrantKeyException("could not create the grant key: " + ex.GetType().Name + " (" + (ex.HResult & 0xFFFF) + ")");
                    }
                    finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } }
                    return ReadKey(path);   // returns what ended up on disk, never a different in-memory copy
                }
            }
        }

        /// <summary>Maximum wait on a transient sharing violation when reading the key (issue #21).</summary>
        public static TimeSpan ReadRetryWindow = TimeSpan.FromSeconds(3);

        /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33): another handle has the file open without sharing.</summary>
        private static bool IsSharingViolation(IOException ex)
        {
            var code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33;
        }

        /// <summary>
        /// Issue #21 (root cause): on Windows, a freshly published file may be open for an instant by another process without sharing
        /// read (antivirus or indexer, or a handle from another test process). File.ReadAllText failed with a sharing violation
        /// and that became a GrantKeyException. In the multithreaded test, the exception on a bare Thread aborted the test process.
        /// Now: read with FileShare.ReadWrite|Delete (does not block other readers) and a bounded wait ONLY on a sharing violation.
        /// Any other error, or a violation that persists beyond the window, is still a GrantKeyException (fail-closed).
        /// The key is never regenerated and the message never includes its content.
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
                    throw new GrantKeyException("grant key unreadable: " + ex.GetType().Name + " (" + (ex.HResult & 0xFFFF) + ")");
                }
            }
            var lines = text.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count > 0 && lines[0] == KeyHeader) lines.RemoveAt(0);   // v1; no header = rc2 format (bare Base64)
            if (lines.Count != 1) throw new GrantKeyException("invalid or truncated grant key in " + path + " (not regenerated)");
            byte[] key;
            try { key = Convert.FromBase64String(lines[0]); }
            catch (FormatException) { throw new GrantKeyException("invalid grant key (bad Base64) in " + path + " (not regenerated)"); }
            if (key.Length != KeyBytes) throw new GrantKeyException("grant key of " + key.Length + " bytes in " + path + " (expected " + KeyBytes + "; not regenerated)");
            return key;
        }

        private string Sign(Grant g) => Hmac(g.ToJson(withSignature: false));

        private string Hmac(IDictionary<string, object> unsigned)
        {
            using (var h = new HMACSHA256(_key))
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(OpSemantics.Canonical(unsigned)))).Replace("-", "").ToLowerInvariant();
        }

        private string GrantPath(string id) => Path.Combine(_dir, "c_" + id + ".json");

        /// <summary>Creates a grant. Call ONLY from the Revit UI after a person's answer.</summary>
        public Grant Approve(string task, string docKey, string docPrint, IEnumerable<string> caps, TimeSpan validFor, string approvedBy, DateTime now, IEnumerable<string> scope = null)
        {
            var list = caps.Distinct(StringComparer.Ordinal).ToList();
            var bad = list.Where(c => !Capability.Grantable.Contains(c)).ToList();
            if (bad.Count > 0) throw new ArgumentException("capabilities that cannot be granted: " + string.Join(", ", bad));
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
                var g = Load(GrantPath(grantId), out _);
                if (g == null) return false;
                g.Revoked = now.ToString("o", CultureInfo.InvariantCulture);
                g.Signature = Sign(g);
                Home.WriteAtomic(GrantPath(g.Id), Json.Write(g.ToJson()));
                return true;
            }
        }

        public int RevokeTask(string task, DateTime now) => All().Where(g => g.Task == task && g.Revoked == null).Count(g => Revoke(g.Id, now));

        /// <summary>
        /// Reads a grant. The signature is checked over the record AS STORED (grants signed by 0.9.3 and earlier carry legacy key names and
        /// capability names); only then is it normalized to the current vocabulary.
        /// </summary>
        private Grant Load(string path, out bool validSignature)
        {
            validSignature = false;
            try
            {
                var stored = Json.ParseObject(File.ReadAllText(path));
                var sigKey = stored.Keys.FirstOrDefault(k => LegacyAliases.Token(k) == "signature");
                var sig = sigKey == null ? null : Arg.GetString(stored, sigKey);
                var unsigned = stored.Where(kv => kv.Key != sigKey).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                validSignature = sig != null && FixedTimeEquals(sig, Hmac(unsigned));
                var g = Grant.FromJson(LegacyAliases.NormalizeRecord(stored));
                g.Capabilities = g.Capabilities.Select(LegacyAliases.Token).ToList();
                return g;
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException) { return null; }
        }

        /// <summary>Grants with a valid signature (tampered ones are ignored).</summary>
        public List<Grant> All()
        {
            var res = new List<Grant>();
            foreach (var f in Directory.GetFiles(_dir, "c_*.json"))
            {
                var g = Load(f, out var valid);
                if (g != null && valid) res.Add(g);
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

        /// <summary>null if (task, document, fingerprint, capability, scope) has a current grant; otherwise, the reason.</summary>
        public string Check(string task, string docKey, string docPrint, string capability, DateTime now, IEnumerable<string> scope = null)
        {
            var any = All().Where(g => g.Task == task && g.Capabilities.Contains(capability)).ToList();
            if (any.Count == 0) return "no grant of " + capability + " for task " + task;
            // rc3: strict identity. The grant's fingerprint and the current one must be equal; a missing one on either side NEVER widens the scope.
            var docOk = any.Where(g => g.DocKey == docKey && string.Equals(g.DocPrint ?? "", docPrint ?? "", StringComparison.Ordinal)).ToList();
            if (docOk.Count == 0)
            {
                if (any.Any(g => g.DocKey == docKey && !string.IsNullOrEmpty(g.DocPrint) && string.IsNullOrEmpty(docPrint)))
                    return "the grant of " + capability + " requires the document fingerprint and it is not available now";
                if (any.Any(g => g.DocKey == docKey)) return "the grant of " + capability + " is for another identity of the document (different fingerprint: replaced or recreated)";
                return "the grant of " + capability + " is for another document";
            }
            var live = docOk.Where(g => g.ValidAt(now)).ToList();
            if (live.Count == 0) return docOk.Any(g => g.Revoked != null) ? "the grant of " + capability + " was revoked" : "the grant of " + capability + " expired";
            var sc = (scope ?? Enumerable.Empty<string>()).ToList();
            if (sc.Count > 0 && !live.Any(g => g.Scope.Count == 0 || sc.All(g.Scope.Contains))) return "elements outside the granted scope for " + capability;
            return null;
        }

        // -------- requests (created by the agent; informational only) --------

        public GrantRequest Request(string task, string docKey, string docPrint, string docTitle, IEnumerable<string> caps, string client, string objective, DateTime now)
        {
            var r = new GrantRequest
            {
                Id = Home.Sha256Hex(task + "|" + docKey + "|" + string.Join(",", caps.OrderBy(x => x, StringComparer.Ordinal)), 12),
                Task = task, DocKey = docKey, DocPrint = docPrint, DocTitle = docTitle, Client = client, Objective = objective,
                Created = now.ToString("o", CultureInfo.InvariantCulture), Capabilities = caps.Distinct().ToList(),
            };
            Home.WriteAtomic(Path.Combine(_requests, r.Id + ".json"), Json.Write(new Dictionary<string, object>
            {
                ["id"] = r.Id, ["task"] = task, ["doc"] = docKey, ["doc_fingerprint"] = docPrint ?? "", ["title"] = docTitle, ["client"] = client,
                ["target"] = objective, ["created"] = r.Created, ["capabilities"] = r.Capabilities.Cast<object>().ToList(),
            }));
            return r;
        }

        /// <summary>Number of pending requests without reading them (cheap enough for the ribbon refresh).</summary>
        public int PendingCount()
        {
            try { return Directory.Exists(_requests) ? Directory.GetFiles(_requests, "*.json").Length : 0; }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        public List<GrantRequest> PendingRequests()
        {
            var res = new List<GrantRequest>();
            foreach (var f in Directory.GetFiles(_requests, "*.json"))
            {
                try
                {
                    var o = LegacyAliases.NormalizeRecord(Json.ParseObject(File.ReadAllText(f)));
                    res.Add(new GrantRequest
                    {
                        Id = Arg.GetString(o, "id"), Task = Arg.GetString(o, "task"), DocKey = Arg.GetString(o, "doc"), DocPrint = Arg.GetString(o, "doc_fingerprint"),
                        DocTitle = Arg.GetString(o, "title"), Client = Arg.GetString(o, "client"), Objective = Arg.GetString(o, "target"),
                        Created = Arg.GetString(o, "created"), Capabilities = Arg.GetStringList(o, "capabilities").Select(LegacyAliases.Token).ToList(),
                    });
                }
                catch (Exception ex) when (ex is IOException || ex is FormatException) { }
            }
            return res;
        }

        public void CloseRequest(string id)
        {
            try { File.Delete(Path.Combine(_requests, id + ".json")); } catch (IOException) { }
        }
    }

    /// <summary>What the task runner checks at the point of execution.</summary>
    public interface ITaskAuthority
    {
        /// <summary>null = authorized; otherwise, a structured reason.</summary>
        string Check(Tasks.TaskRecord task, string capability, DateTime now);
    }

    /// <summary>Engine default: denies every capability (fail-safe if nobody configured an authority).</summary>
    public sealed class DenyWritesAuthority : ITaskAuthority
    {
        public static readonly DenyWritesAuthority Instance = new DenyWritesAuthority();
        public string Check(Tasks.TaskRecord task, string capability, DateTime now) => "no grant authority configured";
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
