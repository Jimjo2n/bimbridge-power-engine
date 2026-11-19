// SPDX-License-Identifier: GPL-3.0-only
using System;

namespace Bimbridge.Protocol
{
    /// <summary>
    /// Identidad pública del producto. Lo visible usa estos textos. Los identificadores técnicos (namespaces Bimbridge, GUID del add-in,
    /// tuberías, carpetas %APPDATA%\Bimbridge, clave MCP "bimbridge") siguen la misma marca: ver docs/0.9.0/ARCHITECTURE_DECISIONS.md (D39).
    /// </summary>
    public static class Brand
    {
        public const string Product = "BIMBRIDGE - Power Engine";
        public const string Short = "BIMBRIDGE";
        public const string Subtitle = "MCP • AgentHost • Revit Automation";
        /// <summary>Prefijo de transacciones en el historial de Revit (Deshacer).</summary>
        public const string TransactionPrefix = "BIMBRIDGE";

        public static bool IsOwnTransaction(string name) =>
            name != null && name.StartsWith(TransactionPrefix, StringComparison.Ordinal);

        public static bool IsTemporaryTransaction(string name) =>
            name != null && name.StartsWith(TransactionPrefix + " temporal", StringComparison.Ordinal);
    }
}
