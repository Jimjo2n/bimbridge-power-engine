// SPDX-License-Identifier: GPL-3.0-only
namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// 0.8.0: tokens ESTIMADOS a partir de caracteres. El tokenizador real sólo lo conoce el cliente (Claude/Codex); toda cifra que salga
    /// de aquí se etiqueta como estimación, nunca como medida.
    /// </summary>
    public static class TokenEstimate
    {
        public const string Method = "ESTIMADO: caracteres/4; el tokenizador real sólo lo conoce el cliente";
        public static long FromChars(long chars) => chars <= 0 ? 0 : (chars + 3) / 4;
    }
}
