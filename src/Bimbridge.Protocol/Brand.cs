// SPDX-License-Identifier: GPL-3.0-only
using System;

namespace Bimbridge.Protocol
{
    /// <summary>
    /// Public product identity. Everything user-visible uses these strings. Technical identifiers (Bimbridge namespaces, add-in GUID,
    /// pipes, %APPDATA%\Bimbridge folders, MCP key "bimbridge") follow the same brand: see docs/0.9.0/ARCHITECTURE_DECISIONS.md (D39).
    /// </summary>
    public static class Brand
    {
        public const string Product = "BIMBRIDGE";
        public const string Short = "BIMBRIDGE";
        public const string Subtitle = "MCP • AgentHost • Revit Automation";
        /// <summary>Transaction prefix in the Revit history (Undo).</summary>
        public const string TransactionPrefix = "BIMBRIDGE";

        public static bool IsOwnTransaction(string name) =>
            name != null && name.StartsWith(TransactionPrefix, StringComparison.Ordinal);

        public static bool IsTemporaryTransaction(string name) =>
            name != null && name.StartsWith(TransactionPrefix + " temporary", StringComparison.Ordinal);
    }
}
