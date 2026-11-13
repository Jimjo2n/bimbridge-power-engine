// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BimBridge.PowerEngine.Workflows;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// AUD-19: idempotencia efectiva de PDF→Revit. El lote lleva su origen en las operaciones; el motor deriva clave y huella y las
    /// usa en el diario de operaciones (la autoridad de siempre) aunque el agente no pase clave=. Cada caso reproduce lo que hace
    /// WithJournal con un commit: JournalIdentity → OperationJournal.Begin → (aplicar) → Complete.
    /// </summary>
    public class SourceBindingTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb_src_" + Guid.NewGuid().ToString("N"));
        private string JournalPath => Path.Combine(_dir, "operaciones.jsonl");
        public SourceBindingTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private static PlacementPlan Plan(string sha = "sha-pdf-1", string revision = "B", double shiftX = 0, double tx = 100)
        {
            var p = new PlacementPlan { Registration = new RegistrationResult { M = new[] { 1.0, 0, 0, tx, 0, 1, 0, 50, 0, 0, 1, 0 } }, Fingerprint = "huella-plan-1" };
            p.Source["id"] = "E-201"; p.Source["sha256"] = sha; p.Source["revision"] = revision;
            p.Batches.Add(new Dictionary<string, object>
            {
                ["operacion"] = new Dictionary<string, object>
                {
                    ["op"] = "place", ["type"] = "F: A", ["level"] = "LEVEL 02", ["elev"] = 9.25,
                    ["puntos"] = new List<object> { new List<object> { 1.0 + shiftX, 2.0 }, new List<object> { 3.0, 4.0 } },
                },
                ["items"] = new List<object>(),
            });
            p.Place.Add(new PlacementItem()); p.Place.Add(new PlacementItem());
            return p;
        }

        /// <summary>Lo que el agente recibe y reenvía a plan: las ops del primer plan ejecutable.</summary>
        private static List<object> OpsOf(PlacementPlan p) => (List<object>)((Dictionary<string, object>)((List<object>)PdfToPlan.Execution(p)["planes"])[0])["ops"];

        /// <summary>Un commit como lo hace el motor. <paramref name="apply"/> = estado final (null = se interrumpe sin final).</summary>
        private OperationJournal.BeginKind Commit(OperationJournal j, List<object> ops, string doc, string givenKey, string apply, ref int applied,
            OperationJournal.ReconcileMode mode = OperationJournal.ReconcileMode.None, string docPrint = "print-1")
        {
            var bound = SourceBinding.Resolve(ops);
            Assert.True(SourceBinding.JournalIdentity(bound, givenKey, out var key, out var hash, out var err), err);
            var b = j.Begin(key, doc, "commit", hash, null, "S1", mode, docPrint, "g1");
            if (b.Kind != OperationJournal.BeginKind.New) return b.Kind;
            applied++;                                   // aquí Revit aplicaría el lote
            if (apply != null) j.Complete(key, doc, apply, null);
            return b.Kind;
        }

        [Fact]
        public void EveryOpCarriesItsOrigin_AndTheEngineDerivesTheSameKeyTheWorkflowAnnounced()
        {
            var e = PdfToPlan.Execution(Plan());
            var one = (Dictionary<string, object>)((List<object>)e["planes"])[0];
            var ops = (List<object>)one["ops"];
            Assert.All(ops.Cast<Dictionary<string, object>>(), o => Assert.StartsWith(SourceBinding.TokenPrefix, (string)o[SourceBinding.OpField]));
            var bound = SourceBinding.Resolve(ops);
            Assert.Equal(one[Wire.KeyArg], bound.Key);
            var origin = (Dictionary<string, object>)one["origen"];
            Assert.Equal("sha-pdf-1", origin["sha256"]); Assert.Equal("B", origin["revision"]); Assert.Equal(bound.Hash, origin["huella_operaciones"]);
            // Las operaciones con origen pasan la validación tipada (place admite el campo).
            OpSpec.ParseAll(new Dictionary<string, object> { ["ops"] = ops });
            // Operaciones sin origen: plan normal, sin vínculo.
            Assert.Null(SourceBinding.Resolve(new List<object> { new Dictionary<string, object> { ["op"] = "place" } }));
        }

        [Fact]
        public void IdenticalAttempt_WithoutPassingAnyKey_IsNotAppliedTwice_EvenWithANewPlanIdOrAfterReconnecting()
        {
            var applied = 0;
            var j = new OperationJournal(JournalPath);
            Assert.Equal(OperationJournal.BeginKind.New, Commit(j, OpsOf(Plan()), "doc-A", null, Status.Ok, ref applied));
            // Segundo intento idéntico: el agente NO pasa clave y además ha vuelto a planificar (plan_id nuevo: no interviene en la huella).
            Assert.Equal(OperationJournal.BeginKind.Completed, Commit(j, OpsOf(Plan()), "doc-A", null, Status.Ok, ref applied));
            // Reconexión / reinicio de Revit: otro diario sobre el mismo archivo.
            Assert.Equal(OperationJournal.BeginKind.Completed, Commit(new OperationJournal(JournalPath), OpsOf(Plan()), "doc-A", null, Status.Ok, ref applied));
            // Pasar la clave vinculada explícitamente es lo mismo.
            var k = SourceBinding.Resolve(OpsOf(Plan())).Key;
            Assert.Equal(OperationJournal.BeginKind.Completed, Commit(j, OpsOf(Plan()), "doc-A", k, Status.Ok, ref applied));
            Assert.Equal(1, applied);
        }

        [Fact]
        public void AnotherKeyCannotBeUsedToRepeatTheBatch()
        {
            var bound = SourceBinding.Resolve(OpsOf(Plan()));
            Assert.False(SourceBinding.JournalIdentity(bound, "otra-clave", out _, out _, out var err));
            Assert.Contains(bound.Key, err);
            // Un plan sin vínculo conserva el comportamiento de siempre (clave del agente o ninguna).
            Assert.True(SourceBinding.JournalIdentity(null, "mi-clave", out var key, out var hash, out _));
            Assert.Equal("mi-clave", key); Assert.Null(hash);
        }

        [Fact]
        public void SamePdf_DifferentDestination_IsASeparateExecution_ModifiedPdfOrRegistration_IsADifferentBatch()
        {
            var applied = 0;
            var j = new OperationJournal(JournalPath);
            Commit(j, OpsOf(Plan()), "doc-A", null, Status.Ok, ref applied);
            // Mismo PDF, otro modelo de destino (otra huella de documento): se aplica allí una vez…
            Assert.Equal(OperationJournal.BeginKind.New, Commit(j, OpsOf(Plan()), "doc-B", null, Status.Ok, ref applied, docPrint: "print-2"));
            Assert.Equal(OperationJournal.BeginKind.Completed, Commit(j, OpsOf(Plan()), "doc-B", null, Status.Ok, ref applied, docPrint: "print-2"));
            // …y si «otro destino» es en realidad una COPIA del mismo modelo, el diario pide reconciliar en vez de repetir a ciegas.
            Assert.Equal(OperationJournal.BeginKind.PossibleCopy, Commit(j, OpsOf(Plan()), "doc-A-copia", null, Status.Ok, ref applied, docPrint: "print-1"));
            Assert.Equal(2, applied);
            // PDF modificado, otra revisión, otro registro u otro punto: otro testigo u otra huella → otra clave.
            var baseKey = SourceBinding.Resolve(OpsOf(Plan())).Key;
            Assert.NotEqual(baseKey, SourceBinding.Resolve(OpsOf(Plan(sha: "sha-pdf-2"))).Key);
            Assert.NotEqual(baseKey, SourceBinding.Resolve(OpsOf(Plan(revision: "C"))).Key);
            Assert.NotEqual(baseKey, SourceBinding.Resolve(OpsOf(Plan(tx: 100.5))).Key);
            Assert.NotEqual(baseKey, SourceBinding.Resolve(OpsOf(Plan(shiftX: 0.25))).Key);
        }

        [Fact]
        public void InterruptedOrUncertainCommit_RequiresReconciliation_NeverABlindRepeat()
        {
            var applied = 0;
            var j = new OperationJournal(JournalPath);
            // Commit interrumpido: empezó y Revit se cerró (sin final). Tras reiniciar no se repite: se pide reconciliar.
            Commit(j, OpsOf(Plan()), "doc-A", null, null, ref applied);
            var afterCrash = new OperationJournal(JournalPath);
            Assert.Equal(OperationJournal.BeginKind.Interrupted, Commit(afterCrash, OpsOf(Plan()), "doc-A", null, Status.Ok, ref applied));
            Assert.Equal(1, applied);
            // Reconciliación «ya quedó aplicado»: se marca hecho sin repetir.
            Assert.Equal(OperationJournal.BeginKind.Completed, Commit(afterCrash, OpsOf(Plan()), "doc-A", null, Status.Ok, ref applied, OperationJournal.ReconcileMode.Applied));
            Assert.Equal(1, applied);
            Assert.Equal(OperationJournal.BeginKind.Completed, Commit(afterCrash, OpsOf(Plan()), "doc-A", null, Status.Ok, ref applied));

            // Resultado incierto en otro lote: desconocido → reconciliar; «no quedó aplicado» → se aplica de nuevo una sola vez.
            var other = OpsOf(Plan(sha: "sha-pdf-9"));
            Commit(j, other, "doc-A", null, Status.Desconocido, ref applied);
            Assert.Equal(2, applied);
            Assert.Equal(OperationJournal.BeginKind.Interrupted, Commit(j, other, "doc-A", null, Status.Ok, ref applied));
            Assert.Equal(OperationJournal.BeginKind.New, Commit(j, other, "doc-A", null, Status.Ok, ref applied, OperationJournal.ReconcileMode.NotApplied));
            Assert.Equal(3, applied);
            Assert.Equal(OperationJournal.BeginKind.Completed, Commit(j, other, "doc-A", null, Status.Ok, ref applied));
            Assert.Equal(3, applied);
        }

        [Fact]
        public void MixedOrForgedOrigins_AreRejectedAtPlanTime()
        {
            var ops = OpsOf(Plan());
            var plain = new Dictionary<string, object> { ["op"] = "place", ["type"] = "F: A", ["level"] = "L", ["puntos"] = new List<object> { new List<object> { 0.0, 0.0 } } };
            Assert.Throws<QueryException>(() => SourceBinding.Resolve(new List<object> { ops[0], plain }));
            var otherSource = OpsOf(Plan(sha: "sha-pdf-2"));
            Assert.Throws<QueryException>(() => SourceBinding.Resolve(new List<object> { ops[0], otherSource[0] }));
            var forged = new Dictionary<string, object>((Dictionary<string, object>)ops[0]) { [SourceBinding.OpField] = "lo-que-sea" };
            Assert.Throws<QueryException>(() => SourceBinding.Resolve(new List<object> { forged }));
            Assert.Throws<ArgumentException>(() => SourceBinding.Token("pdf", "x", null, "B", "r", "h", 1));
            // Sin SHA-256 de la fuente el flujo no es ejecutable.
            var noSha = Plan(); noSha.Source.Remove("sha256");
            Assert.Equal(false, PdfToPlan.Execution(noSha)["ejecutable"]);
        }
    }
}
