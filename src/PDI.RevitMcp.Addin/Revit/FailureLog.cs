// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Fallos de Revit durante una transacción (R058, F18): cada aviso se clasifica por FailureDefinitionId y se asocia a sus elementos,
    /// en vez de borrarlo sin dejar rastro. Los avisos se quitan del diálogo (no bloquean un trabajo desatendido) pero vuelven en la respuesta.
    /// Errores: la transacción se revierte entera y se informan.
    /// </summary>
    public sealed class FailureLog : IFailuresPreprocessor
    {
        public sealed class Item
        {
            public string Severity;
            public string Guid;
            public string Kind;
            public string Text;
            public List<long> Ids = new List<long>();

            public string Line() => Kind + ": " + Text + (Ids.Count > 0 ? " [ids " + string.Join(",", Ids.Take(8)) + (Ids.Count > 8 ? "…" : "") + "]" : "");

            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["tipo"] = Kind, ["gravedad"] = Severity, ["texto"] = Text, ["definicion"] = Guid,
                ["ids"] = Ids.Take(50).Select(i => (object)i).ToList(),
            };
        }

        public readonly List<Item> Items = new List<Item>();
        /// <summary>false = dejar los avisos en Revit (el usuario los verá en el diálogo de avisos).</summary>
        public bool DeleteWarnings = true;

        public List<string> Errors => Items.Where(i => i.Severity != "aviso").Select(i => i.Text).ToList();
        public List<string> Warnings => Items.Where(i => i.Severity == "aviso").Select(i => i.Line()).ToList();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            var hasError = false;
            foreach (var m in a.GetFailureMessages())
            {
                var sev = m.GetSeverity();
                var item = new Item
                {
                    Severity = sev == FailureSeverity.Warning ? "aviso" : sev == FailureSeverity.Error ? "error" : sev.ToString().ToLowerInvariant(),
                    Guid = m.GetFailureDefinitionId()?.Guid.ToString(),
                    Text = m.GetDescriptionText(),
                };
                item.Kind = Classify(m.GetFailureDefinitionId(), item.Text);
                try { foreach (var id in m.GetFailingElementIds()) item.Ids.Add(Ids.ToLong(id)); } catch (Exception) { }
                try { foreach (var id in m.GetAdditionalElementIds()) if (!item.Ids.Contains(Ids.ToLong(id))) item.Ids.Add(Ids.ToLong(id)); } catch (Exception) { }
                if (Items.Count < 500) Items.Add(item);
                if (sev == FailureSeverity.Warning) { if (DeleteWarnings) a.DeleteWarning(m); }
                else hasError = true;
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }

        /// <summary>Tipo corto y estable para agrupar en el QC.</summary>
        public static string Classify(FailureDefinitionId id, string text)
        {
            try
            {
                if (id != null && id.Guid == BuiltInFailures.OverlapFailures.DuplicateInstances.Guid) return "duplicados";
            }
            catch (Exception) { }
            var t = (text ?? "").ToLowerInvariant();
            if (t.Contains("identical instances") || t.Contains("instancias idénticas")) return "duplicados";
            if (t.Contains("host") || t.Contains("anfitri")) return "anfitrion";
            if (t.Contains("overlap") || t.Contains("superpuest")) return "superposicion";
            if (t.Contains("constraint") || t.Contains("restricci")) return "restriccion";
            if (t.Contains("group") || t.Contains("grupo")) return "grupo";
            if (t.Contains("connector") || t.Contains("conector")) return "conector";
            return "otro";
        }

        public static FailureHandlingOptions Attach(Transaction t, FailureLog log)
        {
            var opts = t.GetFailureHandlingOptions();
            opts.SetFailuresPreprocessor(log);
            opts.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(opts);
            return opts;
        }
    }
}
