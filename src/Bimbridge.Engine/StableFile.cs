// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Security.Cryptography;

namespace Bimbridge.Engine
{
    /// <summary>
    /// rc1: consistent copy + SHA-256 of a file another process may have open (Revit with the active .rvt).
    /// Reads sharing read/write and checks that the file did NOT change during the read (length and date before/after,
    /// bytes read = length) and that the copy on disk has the same hash. Any difference throws IOException: never a "partial" hash.
    /// </summary>
    public static class StableFile
    {
        public sealed class Result
        {
            public string Sha256;
            public long Length;
            public DateTime LastWriteUtc;
        }

        public static Result CopyAndHash(string source, string destination, Action<int> afterChunk = null, int chunk = 1 << 20)
        {
            var before = new FileInfo(source);
            if (!before.Exists) throw new FileNotFoundException("does not exist", source);
            long len0 = before.Length; var t0 = before.LastWriteTimeUtc;
            string sha; long read = 0;
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            using (var h = SHA256.Create())
            {
                var buf = new byte[chunk];
                int n, i = 0;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    h.TransformBlock(buf, 0, n, null, 0); dst.Write(buf, 0, n); read += n;
                    afterChunk?.Invoke(i++);
                }
                h.TransformFinalBlock(buf, 0, 0);
                sha = Hex(h.Hash);
            }
            var after = new FileInfo(source);
            after.Refresh();
            if (after.Length != len0 || after.LastWriteTimeUtc != t0 || read != len0)
                throw new IOException("the file changed during the read (length " + len0 + "→" + after.Length + ", read " + read + ", date " +
                                      t0.ToString("o") + "→" + after.LastWriteTimeUtc.ToString("o") + "): not certified");
            var copySha = HashFile(destination);
            if (!string.Equals(copySha, sha, StringComparison.Ordinal)) throw new IOException("the temporary copy does not match what was read (" + copySha + " ≠ " + sha + ")");
            return new Result { Sha256 = sha, Length = len0, LastWriteUtc = t0 };
        }

        public static string HashFile(string path)
        {
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var h = SHA256.Create()) return Hex(h.ComputeHash(s));
        }

        private static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
    }
}
