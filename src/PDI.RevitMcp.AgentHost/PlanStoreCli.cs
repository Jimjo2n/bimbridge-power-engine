// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Vía manual de reconciliación, para lo que no se puede demostrar leyendo el modelo (evidencia dañada, plan con ids perdidos).
    ///   plan-store estado --doc "clave"
    ///   plan-store resolver --doc "clave" --plan P-x|--archivo P-x.json --autor nombre --motivo "qué revisé" --confirmo-revision-humana
    /// Exige Revit cerrado y conserva la evidencia: archiva o aparta a cuarentena, nunca borra.
    /// </summary>
    public static class PlanStoreCli
    {
        public static int Run(string[] a)
        {
            var doc = Program.Opt(a, "--doc");
            if (string.IsNullOrWhiteSpace(doc)) { Console.Error.WriteLine("falta --doc"); return 2; }
            var store = new PlanStore(PlanStore.DefaultQueueRoot);
            if (a[1] == "estado") { store.LoadPersisted(doc); Console.WriteLine(Json.Write(store.CheckWritable(doc).ToJson())); return 0; }
            if (a[1] != "resolver") { Console.Error.WriteLine("plan-store estado|resolver"); return 2; }

            var author = Program.Opt(a, "--autor");
            var reason = Program.Opt(a, "--motivo");
            if (Array.IndexOf(a, "--confirmo-revision-humana") < 0 || string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(reason))
            {
                Console.Error.WriteLine("resolver exige --autor, --motivo y --confirmo-revision-humana: una persona debe haber revisado el modelo");
                return 2;
            }
            if (Process.GetProcessesByName("Revit").Length > 0) { Console.Error.WriteLine("Revit está abierto: ciérrelo antes de resolver. No se cambió nada."); return 3; }

            var file = Program.Opt(a, "--archivo");
            if (file != null)
            {
                store.QuarantineCorrupt(store.DocDir(doc), file, author, reason);
                Console.WriteLine("Apartado a cuarentena: " + file);
            }
            else
            {
                store.LoadPersisted(doc);
                var plan = store.Get(Program.Opt(a, "--plan"));
                if (plan == null) { Console.Error.WriteLine("no hay ese plan en ese documento"); return 2; }
                var check = new ReconcileCheck { Verdict = ReconcileCheck.ManualHuman };
                check.Reasons.Add(reason);
                store.Reconcile(plan, check, author);
                Console.WriteLine("Registrado como " + check.Verdict + ": " + plan.Id);
            }
            Console.WriteLine(Json.Write(store.CheckWritable(doc).ToJson()));
            return 0;
        }
    }
}
