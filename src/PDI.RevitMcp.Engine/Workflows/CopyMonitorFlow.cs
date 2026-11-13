// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PDI.RevitMcp.Engine.Workflows
{
    /// <summary>
    /// AUD-20: congelación y recuperación de las expectativas de un intento de Copy/Monitor. Sin Revit: el add-in aporta lo leído.
    ///  - <see cref="Freeze"/> (preflight): guarda una sola vez inventario del anfitrión + expectativas. Si el intento ya existe,
    ///    devuelve lo congelado y las diferencias con lo que se ve ahora; nunca lo sustituye.
    ///  - <see cref="Load"/> (verificar): devuelve lo congelado y las diferencias con el estado actual. Si alguna altera el objetivo
    ///    (<see cref="Result.Conflict"/>), no se debe verificar contra expectativas nuevas: se informa el conflicto.
    /// </summary>
    public static class CopyMonitorFlow
    {
        public sealed class Result
        {
            public ReceiptStore.Baseline Baseline;
            public CopyMonitorExpectations Frozen;
            public bool Created;
            public List<string> Differences = new List<string>();
            public bool Conflict => Differences.Any(CopyMonitorExpectations.Blocking);
        }

        public static Result Freeze(ReceiptStore store, string attemptKey, CopyMonitorExpectations now, IEnumerable<long> hostIds)
        {
            var b = store.SaveBaselineOnce(attemptKey, now.HostIdentity, now.LinkIdentity, hostIds, out var created, now.ToText());
            var r = new Result { Baseline = b, Created = created };
            r.Frozen = Parse(b, attemptKey);
            if (!created) r.Differences = r.Frozen.Diff(now);
            return r;
        }

        /// <param name="mappingGiven">false si quien verifica no indicó mapeo ni exclusiones: se usan los congelados (no es un cambio).</param>
        public static Result Load(ReceiptStore store, string attemptKey, CopyMonitorExpectations now, bool mappingGiven)
        {
            var b = store.LoadBaseline(attemptKey);
            if (b == null) return null;
            var r = new Result { Baseline = b, Frozen = Parse(b, attemptKey) };
            if (!mappingGiven) now.Mappings = r.Frozen.Mappings;
            r.Differences = r.Frozen.Diff(now);
            return r;
        }

        private static CopyMonitorExpectations Parse(ReceiptStore.Baseline b, string key)
        {
            if (string.IsNullOrEmpty(b.Expectations))
                throw new InvalidDataException("el intento " + key + " se inició sin expectativas congeladas (versión anterior): inicie un intento nuevo con otra clave=");
            return CopyMonitorExpectations.FromText(b.Expectations);
        }
    }
}
