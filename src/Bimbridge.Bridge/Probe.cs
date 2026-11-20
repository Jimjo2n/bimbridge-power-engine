// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Protocol;

namespace Bimbridge.Bridge
{
    /// <summary>
    /// Bimbridge.Bridge.exe --probe : smoke test without AI. Opens TWO connections at once
    /// (as if they were Claude and Codex), and in parallel requests context, a lighting fixture count per level
    /// and the tag audit. Read-only. Prints the results and timings.
    /// </summary>
    public static class Probe
    {
        public static async Task<int> RunAsync()
        {
            var a = new RevitConnection { ClientName = "probe-a" };
            var b = new RevitConnection { ClientName = "probe-b" };
            var t = TimeSpan.FromSeconds(120);
            var ok = true;

            async Task<Response> Run(RevitConnection c, string label, string op, Dictionary<string, object> args)
            {
                var sw = Stopwatch.StartNew();
                var r = await c.CallAsync(op, args, t, CancellationToken.None);
                Console.WriteLine("[" + label + "] " + op + " " + sw.ElapsedMilliseconds + " ms -> " + RevitTools.Render(r));
                if (r.Estado != Status.Ok) ok = false;
                return r;
            }

            var ctx = await Run(a, "A", Wire.OpContext, new Dictionary<string, object>());
            if (ctx.Estado != Status.Ok) return 2;
            Console.WriteLine("session A: " + (a.Hello != null && a.Hello.TryGetValue("sesion", out var s) ? s : "?"));

            var q1 = Run(a, "A", Wire.OpQuery, new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["group_by"] = "lvl" });
            var q2 = Run(b, "B", Wire.OpCheck, new Dictionary<string, object> { ["name"] = "lighting_audit" });
            var q3 = Run(b, "B", Wire.OpQuery, new Dictionary<string, object> { ["cat"] = "LightingFixtures,LightingDevices,ElectricalFixtures", ["group_by"] = "cat", });
            await Task.WhenAll(q1, q2, q3);
            Console.WriteLine("session B: " + (b.Hello != null && b.Hello.TryGetValue("sesion", out var s2) ? s2 : "?"));
            Console.WriteLine(ok ? "PROBE OK" : "PROBE WITH ERRORS");
            a.Dispose(); b.Dispose();
            return ok ? 0 : 1;
        }

        /// <summary>
        /// Bimbridge.Bridge.exe --probe-write : write test on the practice model.
        ///  1) Live: changes Comments of ONE lighting fixture to "BIMBRIDGE test hh:mm:ss" (plan → commit) and verifies it.
        ///  2) Hold: plans restoring the original value and commits → it must end up "retenido" without touching the model.
        ///  3) Free-form code: must be rejected as invalid.
        /// Leaves the restore plan in the queue for the user to apply with the Queue button. Never saves or syncs.
        /// </summary>
        public static async Task<int> RunWriteAsync()
        {
            var c = new RevitConnection { ClientName = "probe-write" };
            var t = TimeSpan.FromSeconds(120);
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimbridge");
            var holdFile = Path.Combine(home, "retener.on");
            var holdBefore = File.Exists(holdFile);
            var fails = 0;

            async Task<Response> Call(string step, string op, Dictionary<string, object> args)
            {
                var sw = Stopwatch.StartNew();
                var r = await c.CallAsync(op, args, t, CancellationToken.None);
                Console.WriteLine("[" + step + "] " + op + " " + sw.ElapsedMilliseconds + " ms -> " + RevitTools.Render(r));
                return r;
            }
            void Expect(bool cond, string what)
            {
                Console.WriteLine((cond ? "  OK   " : "  FAIL ") + what);
                if (!cond) fails++;
            }
            string CommentsOf(Response r)
            {
                var d = r.Datos as Dictionary<string, object>;
                var row = (d?["rows"] as List<object>)?[0] as List<object>;
                return row == null ? null : Convert.ToString(row[1], System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }
            Dictionary<string, object> Ops(long id, string value) => new Dictionary<string, object>
            {
                ["ops"] = new List<object> { new Dictionary<string, object> { ["op"] = "set_param", ["ids"] = new List<object> { id }, ["name"] = "Comments", ["value"] = value } },
                ["summary"] = "MCP test: Comments of 1 lighting fixture",
            };

            try
            {
                var ctx = await Call("0", Wire.OpContext, new Dictionary<string, object>());
                if (ctx.Estado != Status.Ok) return 2;
                var lv = c.Hello != null && c.Hello.TryGetValue("niveles", out var n) ? Convert.ToString(n) : "";
                if (lv.IndexOf('P') < 0 || lv.IndexOf('W') < 0)
                {
                    Console.WriteLine("Levels '" + lv + "': P or W missing. Install with -Niveles RPW and restart Revit.");
                    return 3;
                }

                var pick = await Call("0", Wire.OpQuery, new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["f"] = "id,Comments", ["page_size"] = 1L });
                var row = ((pick.Datos as Dictionary<string, object>)?["rows"] as List<object>)?[0] as List<object>;
                if (row == null) { Console.WriteLine("No lighting fixtures."); return 4; }
                var id = Convert.ToInt64(row[0]);
                var original = Convert.ToString(row[1], System.Globalization.CultureInfo.InvariantCulture) ?? "";
                var test = "BIMBRIDGE test " + DateTime.Now.ToString("HH:mm:ss");
                Console.WriteLine("Lighting fixture " + id + ", original Comments: '" + original + "'");
                Dictionary<string, object> Get() => new Dictionary<string, object> { ["ids"] = id.ToString(), ["f"] = "id,Comments" };

                // 1) Live
                if (File.Exists(holdFile)) File.Delete(holdFile);
                await Task.Delay(1200);
                var p1 = await Call("1", Wire.OpPlan, Ops(id, test));
                var p1Id = (p1.Datos as Dictionary<string, object>)?["plan_id"] as string;
                Expect(p1.Estado == Status.Ok && p1Id != null, "plan returns diff and plan_id");
                var c1 = await Call("1", Wire.OpCommit, new Dictionary<string, object> { ["plan_id"] = p1Id });
                Expect(c1.Estado == Status.Ok, "live commit applies");
                Expect(CommentsOf(await Call("1", Wire.OpQuery, Get())) == test, "the model has the new value");

                // 2) Hold
                File.WriteAllText(holdFile, DateTime.Now.ToString("o"));
                await Task.Delay(1200);
                var p2 = await Call("2", Wire.OpPlan, Ops(id, original));
                var p2Id = (p2.Datos as Dictionary<string, object>)?["plan_id"] as string;
                var c2 = await Call("2", Wire.OpCommit, new Dictionary<string, object> { ["plan_id"] = p2Id });
                Expect(c2.Estado == Status.Retenido, "commit with Hold stays in the queue");
                Expect(CommentsOf(await Call("2", Wire.OpQuery, Get())) == test, "the model has NOT changed yet");
                var j2 = await Call("2", Wire.OpJob, new Dictionary<string, object> { ["plan_id"] = p2Id });
                Expect(j2.Estado == Status.Retenido, "job reports retenido");

                // 3) Free-form code: does not exist
                var bad = await Call("3", Wire.OpPlan, new Dictionary<string, object>
                {
                    ["ops"] = new List<object> { new Dictionary<string, object> { ["op"] = "run_python", ["ids"] = "1" } },
                });
                Expect(bad.Estado == Status.Invalido, "free-form code is rejected");

                Console.WriteLine();
                Console.WriteLine(fails == 0 ? "WRITE PROBE OK" : "WRITE PROBE WITH " + fails + " FAILURES");
                Console.WriteLine("1 plan is left in the queue (" + p2Id + ") that restores Comments of " + id + " to '" + original + "'.");
                Console.WriteLine("To finish the test: BIMBRIDGE ribbon > Queue > Apply.");
                return fails == 0 ? 0 : 1;
            }
            finally
            {
                try { if (!holdBefore && File.Exists(holdFile)) File.Delete(holdFile); } catch (Exception) { }
                c.Dispose();
            }
        }
    }
}
