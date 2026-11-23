// SPDX-License-Identifier: GPL-3.0-only
namespace Bimbridge.Engine
{
    /// <summary>
    /// 0.8.0: tokens ESTIMATED from characters. Only the client (Claude/Codex) knows the real tokenizer; every figure coming
    /// from here is labeled as an estimate, never as a measurement.
    /// </summary>
    public static class TokenEstimate
    {
        public const string Method = "ESTIMATED: characters/4; only the client knows the real tokenizer";
        public static long FromChars(long chars) => chars <= 0 ? 0 : (chars + 3) / 4;
    }
}
