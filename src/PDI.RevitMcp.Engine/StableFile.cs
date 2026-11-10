// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Security.Cryptography;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// rc1: copia + SHA-256 consistentes de un archivo que otro proceso puede tener abierto (Revit con el .rvt activo).
    /// Lee compartiendo lectura/escritura y comprueba que el archivo NO cambió durante la lectura (longitud y fecha antes/después,
    /// bytes leídos = longitud) y que la copia en disco tiene el mismo hash. Cualquier diferencia lanza IOException: nunca un hash "parcial".
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
            if (!before.Exists) throw new FileNotFoundException("no existe", source);
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
                throw new IOException("el archivo cambió durante la lectura (longitud " + len0 + "→" + after.Length + ", leídos " + read + ", fecha " +
                                      t0.ToString("o") + "→" + after.LastWriteTimeUtc.ToString("o") + "): no se certifica");
            var copySha = HashFile(destination);
            if (!string.Equals(copySha, sha, StringComparison.Ordinal)) throw new IOException("la copia temporal no coincide con lo leído (" + copySha + " ≠ " + sha + ")");
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
