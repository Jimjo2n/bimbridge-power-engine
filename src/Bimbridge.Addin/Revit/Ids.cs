// SPDX-License-Identifier: GPL-3.0-only
using Autodesk.Revit.DB;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// ElementId ↔ long conversion for all versions (feasibility section 5 bis):
    /// 2022–2023 use IntegerValue; 2024 adds Value (Int64); 2026 removes IntegerValue.
    /// In the protocol ids always travel as 64-bit integers.
    /// </summary>
    public static class Ids
    {
        public static long ToLong(ElementId id)
        {
            if (id == null) return -1;
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId ToId(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
    }
}
