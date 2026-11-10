// SPDX-License-Identifier: GPL-3.0-only
using System;

namespace PDI.RevitMcp.Protocol
{
    /// <summary>
    /// Identidad pública del producto. Lo visible usa estos textos. Los identificadores técnicos (namespaces PDI.RevitMcp, GUID del add-in,
    /// tuberías, carpetas %APPDATA%\PDI.RevitMcp, clave MCP "pdi-revit") se conservan por compatibilidad: ver docs/0.9.0/REBRANDING.md.
    /// </summary>
    public static class Brand
    {
        public const string Product = "BIMBRIDGE - Power Engine";
        public const string Short = "BIMBRIDGE";
        public const string Subtitle = "MCP • AgentHost • Revit Automation";
        /// <summary>Prefijo de transacciones en el historial de Revit (Deshacer).</summary>
        public const string TransactionPrefix = "BIMBRIDGE";
        /// <summary>Prefijo heredado hasta 0.9.0+b944e6e: se sigue reconociendo como propio al atribuir cambios.</summary>
        public const string LegacyTransactionPrefix = "PDI MCP";

        public static bool IsOwnTransaction(string name) =>
            name != null && (name.StartsWith(TransactionPrefix, StringComparison.Ordinal) || name.StartsWith(LegacyTransactionPrefix, StringComparison.Ordinal));

        public static bool IsTemporaryTransaction(string name) =>
            name != null && (name.StartsWith(TransactionPrefix + " temporal", StringComparison.Ordinal) || name.StartsWith(LegacyTransactionPrefix + " temporal", StringComparison.Ordinal));
    }
}
