// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bimbridge.Protocol
{
    /// <summary>
    /// Pipe frames: 4-byte length (little-endian) + UTF-8 JSON.
    /// A message is never cut in half or mixed with the next one.
    /// </summary>
    public static class Frame
    {
        /// <summary>Maximum frame size (16 MB). Guards against corrupt data.</summary>
        public const int MaxBytes = 16 * 1024 * 1024;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static async Task WriteAsync(Stream stream, string json, CancellationToken ct)
        {
            var body = Utf8.GetBytes(json);
            if (body.Length > MaxBytes) throw new InvalidOperationException("Frame too large: " + body.Length + " bytes");
            var buffer = new byte[4 + body.Length];
            buffer[0] = (byte)body.Length;
            buffer[1] = (byte)(body.Length >> 8);
            buffer[2] = (byte)(body.Length >> 16);
            buffer[3] = (byte)(body.Length >> 24);
            Buffer.BlockCopy(body, 0, buffer, 4, body.Length);
            await stream.WriteAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Returns null if the other side closed the connection cleanly.</summary>
        public static async Task<string> ReadAsync(Stream stream, CancellationToken ct)
        {
            var header = new byte[4];
            var got = await ReadExactAsync(stream, header, 4, ct).ConfigureAwait(false);
            if (got == 0) return null;
            if (got < 4) throw new EndOfStreamException("Incomplete frame header");
            var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            if (length < 0 || length > MaxBytes) throw new InvalidDataException("Invalid frame length: " + length);
            var body = new byte[length];
            if (await ReadExactAsync(stream, body, length, ct).ConfigureAwait(false) < length)
                throw new EndOfStreamException("Incomplete frame");
            return Utf8.GetString(body);
        }

        private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            var total = 0;
            while (total < count)
            {
                var n = await stream.ReadAsync(buffer, total, count - total, ct).ConfigureAwait(false);
                if (n == 0) return total;
                total += n;
            }
            return total;
        }
    }
}
