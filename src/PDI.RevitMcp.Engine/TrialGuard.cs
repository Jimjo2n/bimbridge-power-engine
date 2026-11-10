// SPDX-License-Identifier: GPL-3.0-only
namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// alpha3 (P0 de Codex, 2026-10-28): revertir un TransactionGroup deja Document.IsModified=false aunque el documento tuviera
    /// cambios sin guardar (observado en NRT-04 run4). Por eso TODA ruta que abre una transacción para revertirla siempre
    /// (commit modo=ensayo, exec ensayo, captura temporal, QC de tarea) se niega o se degrada si el documento está modificado.
    /// La decisión vive aquí (pura, probada sin Revit); el add-in la consulta en el hilo de Revit justo antes de abrir el grupo.
    /// Nunca se manipula IsModified para disimularlo.
    /// </summary>
    public static class TrialGuard
    {
        public const string Code = "documento_con_cambios_sin_guardar";

        /// <summary>null = se puede ensayar; texto = motivo del rechazo previo (nada se ha modificado).</summary>
        public static string Refuse(bool isModified, string route) =>
            isModified
                ? Code + ": " + route + " revertiría un grupo de transacciones y Revit borraría la marca de cambios sin guardar del documento. " +
                  "Guarde o descarte esos cambios a mano y repita, o use un documento sin cambios pendientes. No se modificó nada."
                : null;

        /// <summary>Verificación temporal y rollback síncronos, dentro de la misma invocación nativa.</summary>
        public static T VerifyAndRollback<T>(System.Func<T> verify, System.Action rollback)
        {
            try { return verify(); }
            finally { rollback(); }
        }

        public static string TrialStatus(bool functionalOk, bool restorationOk) =>
            !restorationOk ? Protocol.Status.Desconocido : functionalOk ? PlanStatus.Ensayado : PlanStatus.Revertido;

        public const string CorrectReverted = "ensayo_correcto_revertido";
        public const string FunctionalFailureReverted = "fallo_funcional_revertido";
        public const string RollbackFailed = "rollback_fallido";
        public const string UnknownNeedsReconciliation = "desconocido_requiere_reconciliacion";

        /// <summary>Los cuatro resultados del ensayo. Sólo el primero es éxito, y exige rollback confirmado y relectura limpia.</summary>
        public static string Classify(bool functionalOk, bool rollbackConfirmed, bool restorationOk) =>
            !rollbackConfirmed ? RollbackFailed : !restorationOk ? UnknownNeedsReconciliation : functionalOk ? CorrectReverted : FunctionalFailureReverted;

        public static bool FunctionalSuccess(int verifiedExisting, bool differences, bool skipped,
            int expectedCreations, int verifiedCreations, bool creationMismatch) =>
            !differences && !skipped && !creationMismatch &&
            verifiedCreations >= expectedCreations && verifiedExisting + verifiedCreations > 0;

        /// <summary>Rutas auxiliares (captura, QC de tarea) que pueden funcionar sin grupo temporal: false = no abrir el grupo.</summary>
        public static bool MayOpenTemporaryGroup(bool isModified) => !isModified;
    }
}
