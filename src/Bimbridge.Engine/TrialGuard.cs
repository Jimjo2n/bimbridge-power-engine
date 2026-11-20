// SPDX-License-Identifier: GPL-3.0-only
namespace Bimbridge.Engine
{
    /// <summary>
    /// alpha3 (Codex P0, 2026-10-28): rolling back a TransactionGroup leaves Document.IsModified=false even if the document had
    /// unsaved changes (observed in NRT-04 run4). That is why EVERY path that opens a transaction only to roll it back
    /// (commit modo=ensayo, exec trial, temporary capture, task QC) refuses or degrades if the document is modified.
    /// The decision lives here (pure, tested without Revit); the add-in queries it on the Revit thread right before opening the group.
    /// IsModified is never manipulated to hide this.
    /// </summary>
    public static class TrialGuard
    {
        public const string Code = "documento_con_cambios_sin_guardar";

        /// <summary>null = a trial is allowed; text = reason for the upfront refusal (nothing has been modified).</summary>
        public static string Refuse(bool isModified, string route) =>
            isModified
                ? Code + ": " + route + " would roll back a transaction group and Revit would clear the document's unsaved-changes flag. " +
                  "Save or discard those changes manually and repeat, or use a document with no pending changes. Nothing was modified."
                : null;

        /// <summary>Synchronous temporary verification and rollback, within the same native invocation.</summary>
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

        /// <summary>The four trial results. Only the first is success, and it requires a confirmed rollback and a clean re-read.</summary>
        public static string Classify(bool functionalOk, bool rollbackConfirmed, bool restorationOk) =>
            !rollbackConfirmed ? RollbackFailed : !restorationOk ? UnknownNeedsReconciliation : functionalOk ? CorrectReverted : FunctionalFailureReverted;

        public static bool FunctionalSuccess(int verifiedExisting, bool differences, bool skipped,
            int expectedCreations, int verifiedCreations, bool creationMismatch) =>
            !differences && !skipped && !creationMismatch &&
            verifiedCreations >= expectedCreations && verifiedExisting + verifiedCreations > 0;

        /// <summary>Auxiliary paths (capture, task QC) that can work without a temporary group: false = do not open the group.</summary>
        public static bool MayOpenTemporaryGroup(bool isModified) => !isModified;
    }
}
