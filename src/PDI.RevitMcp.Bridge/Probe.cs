// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Bridge
{
    /// <summary>
    /// PDI.RevitMcp.Bridge.exe --probe : prueba de humo sin IA. Abre DOS conexiones a la vez
    /// (como si fueran Claude y Codex), y en paralelo pide contexto, un conteo de luminarias por nivel
    /// y la auditoría de tags. Sólo lectura. Imprime los resultados y los tiempos.
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
            Console.WriteLine("sesion A: " + (a.Hello != null && a.Hello.TryGetValue("sesion", out var s) ? s : "?"));

            var q1 = Run(a, "A", Wire.OpQuery, new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["group_by"] = "lvl" });
            var q2 = Run(b, "B", Wire.OpCheck, new Dictionary<string, object> { ["name"] = "lighting_audit" });
            var q3 = Run(b, "B", Wire.OpQuery, new Dictionary<string, object> { ["cat"] = "LightingFixtures,LightingDevices,ElectricalFixtures", ["group_by"] = "cat", });
            await Task.WhenAll(q1, q2, q3);
            Console.WriteLine("sesion B: " + (b.Hello != null && b.Hello.TryGetValue("sesion", out var s2) ? s2 : "?"));
            Console.WriteLine(ok ? "PROBE OK" : "PROBE CON ERRORES");
            a.Dispose(); b.Dispose();
            return ok ? 0 : 1;
        }

        /// <summary>
        /// PDI.RevitMcp.Bridge.exe --probe-write : prueba de escritura en el modelo de práctica.
        ///  1) En vivo: cambia Comments de UNA luminaria a "PDI MCP prueba hh:mm:ss" (plan → commit) y lo verifica.
        ///  2) Retener: planifica devolver el valor original y hace commit → debe quedar "retenido" sin tocar el modelo.
        ///  3) Código libre: debe rechazarse como inválido.
        /// Deja el plan de restauración en la cola para que el usuario lo aplique con el botón Cola. Nunca guarda ni sincroniza.
        /// </summary>
        public static async Task<int> RunWriteAsync()
        {
            var c = new RevitConnection { ClientName = "probe-escritura" };
            var t = TimeSpan.FromSeconds(120);
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDI.RevitMcp");
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
                Console.WriteLine((cond ? "  OK   " : "  FALLA ") + what);
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
                ["summary"] = "Prueba del MCP: Comments de 1 luminaria",
            };

            try
            {
                var ctx = await Call("0", Wire.OpContext, new Dictionary<string, object>());
                if (ctx.Estado != Status.Ok) return 2;
                var lv = c.Hello != null && c.Hello.TryGetValue("niveles", out var n) ? Convert.ToString(n) : "";
                if (lv.IndexOf('P') < 0 || lv.IndexOf('W') < 0)
                {
                    Console.WriteLine("Niveles '" + lv + "': falta P o W. Instale con -Niveles RPW y reinicie Revit.");
                    return 3;
                }

                var pick = await Call("0", Wire.OpQuery, new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["f"] = "id,Comments", ["page_size"] = 1L });
                var row = ((pick.Datos as Dictionary<string, object>)?["rows"] as List<object>)?[0] as List<object>;
                if (row == null) { Console.WriteLine("No hay luminarias."); return 4; }
                var id = Convert.ToInt64(row[0]);
                var original = Convert.ToString(row[1], System.Globalization.CultureInfo.InvariantCulture) ?? "";
                var test = "BIMBRIDGE prueba " + DateTime.Now.ToString("HH:mm:ss");
                Console.WriteLine("Luminaria " + id + ", Comments original: '" + original + "'");
                Dictionary<string, object> Get() => new Dictionary<string, object> { ["ids"] = id.ToString(), ["f"] = "id,Comments" };

                // 1) En vivo
                if (File.Exists(holdFile)) File.Delete(holdFile);
                await Task.Delay(1200);
                var p1 = await Call("1", Wire.OpPlan, Ops(id, test));
                var p1Id = (p1.Datos as Dictionary<string, object>)?["plan_id"] as string;
                Expect(p1.Estado == Status.Ok && p1Id != null, "plan devuelve diff y plan_id");
                var c1 = await Call("1", Wire.OpCommit, new Dictionary<string, object> { ["plan_id"] = p1Id });
                Expect(c1.Estado == Status.Ok, "commit en vivo aplica");
                Expect(CommentsOf(await Call("1", Wire.OpQuery, Get())) == test, "el modelo tiene el valor nuevo");

                // 2) Retener
                File.WriteAllText(holdFile, DateTime.Now.ToString("o"));
                await Task.Delay(1200);
                var p2 = await Call("2", Wire.OpPlan, Ops(id, original));
                var p2Id = (p2.Datos as Dictionary<string, object>)?["plan_id"] as string;
                var c2 = await Call("2", Wire.OpCommit, new Dictionary<string, object> { ["plan_id"] = p2Id });
                Expect(c2.Estado == Status.Retenido, "commit con Retener queda en la cola");
                Expect(CommentsOf(await Call("2", Wire.OpQuery, Get())) == test, "el modelo NO cambió todavía");
                var j2 = await Call("2", Wire.OpJob, new Dictionary<string, object> { ["plan_id"] = p2Id });
                Expect(j2.Estado == Status.Retenido, "job informa retenido");

                // 3) Código libre: no existe
                var bad = await Call("3", Wire.OpPlan, new Dictionary<string, object>
                {
                    ["ops"] = new List<object> { new Dictionary<string, object> { ["op"] = "run_python", ["ids"] = "1" } },
                });
                Expect(bad.Estado == Status.Invalido, "código libre se rechaza");

                Console.WriteLine();
                Console.WriteLine(fails == 0 ? "PROBE ESCRITURA OK" : "PROBE ESCRITURA CON " + fails + " FALLAS");
                Console.WriteLine("Queda 1 plan en la cola (" + p2Id + ") que devuelve Comments de " + id + " a '" + original + "'.");
                Console.WriteLine("Para terminar la prueba: cinta BIMBRIDGE > Cola > Aplicar.");
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
