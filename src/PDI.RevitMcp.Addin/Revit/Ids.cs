// SPDX-License-Identifier: GPL-3.0-only
using Autodesk.Revit.DB;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Conversión de ElementId ↔ long para todas las versiones (sección 5 bis de la factibilidad):
    /// 2022–2023 usan IntegerValue; 2024 añade Value (Int64); 2026 elimina IntegerValue.
    /// En el protocolo los ids siempre van como enteros de 64 bits.
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
