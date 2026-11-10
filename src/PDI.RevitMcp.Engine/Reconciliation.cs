// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>Estado de escritura de un documento. Cualquier duda (evidencia, archivo dañado, error de disco) lo deja bloqueado.</summary>
    public sealed class DocWriteBlock
    {
        public const string Code = "documento_bloqueado_reconciliacion";
        public string DocKey;
        public List<string> Plans = new List<string>();
        public List<string> CorruptFiles = new List<string>();
        public List<string> Errors = new List<string>();
        public bool Blocked => Plans.Count > 0 || CorruptFiles.Count > 0 || Errors.Count > 0;

        public string Message =>
            "No se aplicó nada: el documento tiene operaciones sin reconciliar" +
            (Plans.Count > 0 ? " (" + string.Join(", ", Plans) + ")" : "") +
            (CorruptFiles.Count > 0 ? "; archivos de evidencia dañados: " + string.Join(", ", CorruptFiles) : "") +
            (Errors.Count > 0 ? "; no se pudo leer la evidencia: " + string.Join("; ", Errors) : "") +
            ". Use job op=reconciliar plan_id=… para comprobarlo contra el modelo; no repita la operación.";

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            // El código sólo aparece cuando hay bloqueo: un "documento_bloqueado" con bloqueado=no confunde a quien lo lee.
            ["codigo"] = Blocked ? Code : null,
            ["bloqueado"] = Blocked,
            ["planes"] = Plans.Cast<object>().ToList(),
            ["archivos_danados"] = CorruptFiles.Cast<object>().ToList(),
            ["errores"] = Errors.Cast<object>().ToList(),
        };
    }

    public sealed class DocWriteBlockedException : InvalidOperationException
    {
        public DocWriteBlock Block { get; }
        public DocWriteBlockedException(DocWriteBlock block) : base(block.Message) { Block = block; }
    }

    /// <summary>La evidencia no quedó en disco: quien llama no debe escribir en el modelo.</summary>
    public sealed class PlanStoreException : System.IO.IOException
    {
        public PlanStoreException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>Resultado de comparar la evidencia de un plan con el modelo actual. Nunca adivina: lo que no se puede comprobar no libera.</summary>
    public sealed class ReconcileCheck
    {
        public const string Reverted = "revertido_verificado";
        public const string Applied = "aplicado_verificado";
        public const string NotVerifiable = "no_verificable";
        public const string ManualHuman = "manual_humano";

        public string Verdict = NotVerifiable;
        public int Checked;
        public List<string> Reasons = new List<string>();

        public bool Verified => Verdict == Reverted || Verdict == Applied;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["veredicto"] = Verdict,
            ["comprobados"] = (long)Checked,
            ["motivos"] = Reasons.Take(20).Cast<object>().ToList(),
        };
    }

    public static class ReconcileEvaluator
    {
        /// <param name="current">Valor canónico actual de la entrada, como PlanApplier.Current.</param>
        /// <param name="exists">¿Existe ese id en el documento?</param>
        public static ReconcileCheck Evaluate(Plan plan, Func<PlanEntry, string> current, Func<long, bool> exists)
        {
            var result = new ReconcileCheck();
            var entries = plan.Entries.Where(Comparable).ToList();
            var created = plan.Created.Distinct().ToList();
            var deleted = EvidenceIds(plan, "ids_borrados");
            var plannedCreations = plan.Entries.Count(e => e.IsCreation);
            var templates = plan.Entries.Count(e => e.Ref > 0);

            int asBefore = 0, asAfter = 0;
            try
            {
                foreach (var e in entries)
                {
                    var now = current(e);
                    result.Checked++;
                    if (PlanEntry.Same(now, e.Before)) asBefore++;
                    else if (PlanEntry.Same(now, e.After)) asAfter++;
                    else result.Reasons.Add(e.Id + " " + e.Key + ": no coincide ni con el antes ni con el después");
                }
                var createdPresent = created.Where(exists).ToList();
                var survivors = created.Where(id => !deleted.Contains(id)).ToList();
                var deletedOriginals = deleted.Where(id => !created.Contains(id)).ToList();
                result.Checked += created.Count + deletedOriginals.Count;

                var revertedOk = asBefore == entries.Count && createdPresent.Count == 0 && deletedOriginals.All(exists);
                var appliedOk = asAfter == entries.Count && survivors.All(exists) && deletedOriginals.All(id => !exists(id));

                // Sin ids no se puede demostrar qué pasó con lo creado.
                if (plannedCreations + templates > 0 && created.Count == 0 && plan.AppliedBatches > 0)
                {
                    result.Reasons.Add("el plan crea elementos y la evidencia no conserva sus ids");
                    return result;
                }
                if (revertedOk && (entries.Count > 0 || created.Count > 0 || plan.AppliedBatches == 0))
                {
                    result.Verdict = ReconcileCheck.Reverted;
                    return result;
                }
                // "Aplicado" exige ver todo el plan: creaciones completas y sin plantillas @n, que no tienen un después comparable.
                if (appliedOk && templates == 0 && created.Count >= plannedCreations && (entries.Count > 0 || created.Count > 0))
                {
                    result.Verdict = ReconcileCheck.Applied;
                    return result;
                }
                result.Reasons.Add("estado mixto: " + asBefore + " como antes, " + asAfter + " como después de " + entries.Count +
                                   "; creados presentes " + createdPresent.Count + " de " + created.Count +
                                   (templates > 0 ? "; incluye " + templates + " plantillas @n" : ""));
            }
            catch (Exception ex)
            {
                result.Reasons.Add("no se pudo leer el modelo: " + ex.GetType().Name + ": " + ex.Message);
            }
            return result;
        }

        private static bool Comparable(PlanEntry e) =>
            e.Ref == 0 && e.Id != 0 && e.Key != "nuevo" && !(e.Key != null && e.Key.Contains("@@"));

        private static HashSet<long> EvidenceIds(Plan plan, string key)
        {
            var set = new HashSet<long>();
            if (plan.Reconciliation != null && plan.Reconciliation.TryGetValue(key, out var v) && v is List<object> list)
                foreach (var x in list)
                    set.Add(Convert.ToInt64(x, CultureInfo.InvariantCulture));
            return set;
        }
    }
}
