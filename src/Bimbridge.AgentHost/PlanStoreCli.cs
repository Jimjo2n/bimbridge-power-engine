// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Manual reconciliation path, for what cannot be proven by reading the model (damaged evidence, plan with lost ids).
    ///   plan-store estado --doc "key"
    ///   plan-store resolver --doc "key" --plan P-x|--archivo P-x.json --autor name --motivo "what I reviewed" --confirmo-revision-humana
    /// Requires Revit to be closed and keeps the evidence: archives or sets it aside in quarantine, never deletes.
    /// </summary>
    public static class PlanStoreCli
    {
        public static int Run(string[] a)
        {
            var doc = Program.Opt(a, "--doc");
            if (string.IsNullOrWhiteSpace(doc)) { Console.Error.WriteLine("missing --doc"); return 2; }
            var store = new PlanStore(PlanStore.DefaultQueueRoot);
            if (a[1] == "status") { store.LoadPersisted(doc); Console.WriteLine(Json.Write(store.CheckWritable(doc).ToJson())); return 0; }
            if (a[1] != "resolve") { Console.Error.WriteLine("plan-store status|resolve"); return 2; }

            var author = Program.Opt(a, "--autor");
            var reason = Program.Opt(a, "--motivo");
            if (Array.IndexOf(a, "--confirmo-revision-humana") < 0 || string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(reason))
            {
                Console.Error.WriteLine("resolve requires --autor, --motivo and --confirmo-revision-humana: a person must have reviewed the model");
                return 2;
            }
            if (Process.GetProcessesByName("Revit").Length > 0) { Console.Error.WriteLine("Revit is open: close it before resolving. Nothing was changed."); return 3; }

            var file = Program.Opt(a, "--archivo");
            if (file != null)
            {
                store.QuarantineCorrupt(store.DocDir(doc), file, author, reason);
                Console.WriteLine("Set aside in quarantine: " + file);
            }
            else
            {
                store.LoadPersisted(doc);
                var plan = store.Get(Program.Opt(a, "--plan"));
                if (plan == null) { Console.Error.WriteLine("that plan does not exist in that document"); return 2; }
                var check = new ReconcileCheck { Verdict = ReconcileCheck.ManualHuman };
                check.Reasons.Add(reason);
                store.Reconcile(plan, check, author);
                Console.WriteLine("Recorded as " + check.Verdict + ": " + plan.Id);
            }
            Console.WriteLine(Json.Write(store.CheckWritable(doc).ToJson()));
            return 0;
        }
    }
}
