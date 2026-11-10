// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Escenario simulado completo (§9): N operaciones (1 000 por defecto) sobre un modelo de 10 niveles con dependencias en cadena,
    /// fallos deliberados y puntos de interrupción. Escribe en &lt;dir&gt;: plan_entrada.json, modelo/ y escenario.json (qué se espera de
    /// cada operación especial, para que la prueba lo compruebe sin depender de este código).
    /// Estructura: 2 consultas · 10 niveles × K parámetros (cadenas de 5: cada una depende de la anterior; la primera, de la consulta) ·
    /// 10 QC (uno por nivel, dependen del final de 5 cadenas) · 8 escrituras sobre un nivel inexistente (4 con permitir_vacio).
    /// Fallos deliberados: rechazos del backend (bloquean su cadena), transitorios recuperables y no recuperables, resultados esperados
    /// erróneos, un QC con desviaciones y conjuntos vacíos no permitidos.
    /// </summary>
    public static class Scenario
    {
        public const int Levels = 10, PerLevel = 20, Chain = 5;

        public static string Generate(string dir, int n = 1000)
        {
            Directory.CreateDirectory(dir);
            var k = Math.Max(Chain, (n - 2 - Levels - 8) / Levels);
            var plan = new PlanDraft { Objective = "Escenario 1000: parámetros por nivel con cadenas, QC y fallos deliberados", DocKey = "archivo:c:\\escenario.rvt", DocPrint = "huella-escenario-0001", LengthUnit = "mm", Coordinates = "interno_revit" };
            plan.Sources.Add(new SourceDoc { Id = "doc-1", Nombre = "escenario_1000.txt", Sha256 = new string('e', 64) });
            OpDraft Q(string cat) { var o = new OpDraft { Operation = "consultar" }; o.Params["categoria"] = cat; return o; }
            plan.Ops.Add(Q("OST_LightingFixtures"));
            plan.Ops.Add(Q("OST_LightingDevices"));
            var byLevel = new Dictionary<int, List<int>>();
            var chainEnds = new Dictionary<int, List<int>>();
            for (var l = 1; l <= Levels; l++)
            {
                byLevel[l] = new List<int>(); chainEnds[l] = new List<int>();
                for (var p = 0; p < k; p++)
                {
                    var o = new OpDraft { Operation = "fijar_parametro", Source = new SourceRef { Fuente = "doc-1", Pagina = l, Requisito = "REQ-L" + l.ToString("00", CultureInfo.InvariantCulture) + "-" + p.ToString("000", CultureInfo.InvariantCulture) } };
                    o.Scope["categoria"] = "OST_LightingFixtures"; o.Scope["nivel"] = "L" + l.ToString("00", CultureInfo.InvariantCulture);
                    o.Params["parametro"] = "P" + p.ToString("000", CultureInfo.InvariantCulture); o.Params["valor"] = (long)(l * 1000 + p); o.Params["unidades"] = "mm";
                    o.Expected["elementos"] = (long)PerLevel;
                    o.DependsOn.Add(p % Chain == 0 ? "#0" : "#" + (plan.Ops.Count - 1).ToString(CultureInfo.InvariantCulture));
                    byLevel[l].Add(plan.Ops.Count);
                    if (p % Chain == Chain - 1) chainEnds[l].Add(plan.Ops.Count);
                    plan.Ops.Add(o);
                }
            }
            var qc = new List<int>();
            for (var l = 1; l <= Levels; l++)
            {
                var o = new OpDraft { Operation = "verificar_qc" };
                o.Params["receta"] = "qc_manifiesto"; o.Scope["nivel"] = "L" + l.ToString("00", CultureInfo.InvariantCulture);
                o.DependsOn.AddRange(chainEnds[l].Take(5).Select(i => "#" + i.ToString(CultureInfo.InvariantCulture)));
                qc.Add(plan.Ops.Count);
                plan.Ops.Add(o);
            }
            var empty = new List<int>();
            for (var e = 0; e < 8; e++)
            {
                var o = new OpDraft { Operation = "fijar_parametro", AllowEmpty = e < 4 };
                o.Scope["categoria"] = "OST_LightingFixtures"; o.Scope["nivel"] = "L99";
                o.Params["parametro"] = "VACIO" + e; o.Params["valor"] = (long)e; o.Params["unidades"] = "mm";
                empty.Add(plan.Ops.Count);
                plan.Ops.Add(o);
            }
            // fallos deliberados (índices dentro de cadenas concretas)
            var rejected = new[] { byLevel[2][1], byLevel[5][7], byLevel[9][12] };                                // bloquean el resto de su cadena
            var transientOk = new[] { byLevel[1][3], byLevel[3][4], byLevel[4][10], byLevel[6][2], byLevel[8][9] }; // 1 rechazo, reintentos=1 → ok
            var transientKo = new[] { byLevel[7][5], byLevel[10][15] };                                           // 3 rechazos, reintentos=1 → fallida
            var wrongExpect = new[] { byLevel[1][20], byLevel[3][30], byLevel[6][40], byLevel[8][50], byLevel[10][60] };
            foreach (var i in transientOk.Concat(transientKo)) plan.Ops[i].Retries = 1;
            foreach (var i in wrongExpect) plan.Ops[i].Expected["elementos"] = (long)(PerLevel + 1);
            plan.AssignIds();
            string Id(int i) => plan.Ops[i].Id;

            var errs = IrValidator.Validate(plan, out var vp);
            if (errs.Count > 0) throw new InvalidOperationException("escenario inválido: " + string.Join("; ", errs.Take(5)));
            File.WriteAllText(Path.Combine(dir, "plan_entrada.json"), Json.Write(plan.ToJson()));
            var qcBad = qc[3];
            var elements = Enumerable.Range(1, Levels).Select(l => "OST_LightingFixtures=" + PerLevel + "@L" + l.ToString("00", CultureInfo.InvariantCulture))
                                     .Concat(new[] { "OST_LightingDevices=40@L01" }).ToList();
            DurableSimModel.Create(Path.Combine(dir, "modelo"), plan.DocKey, plan.DocPrint, elements, new Dictionary<string, object>
            {
                ["rechazar"] = rejected.Select(i => (object)Id(i)).ToList(),
                ["transitorio"] = transientOk.ToDictionary(i => Id(i), i => (object)1L).Concat(transientKo.ToDictionary(i => Id(i), i => (object)3L)).ToDictionary(kv => kv.Key, kv => kv.Value),
                ["qc_desviaciones"] = new Dictionary<string, object> { [Id(qcBad)] = 2L },
            });
            var spec = new Dictionary<string, object>
            {
                ["plan"] = vp.Hash, ["operaciones"] = (long)plan.Ops.Count,
                ["rechazadas"] = rejected.Select(i => (object)Id(i)).ToList(),
                ["transitorias_ok"] = transientOk.Select(i => (object)Id(i)).ToList(),
                ["transitorias_ko"] = transientKo.Select(i => (object)Id(i)).ToList(),
                ["esperado_erroneo"] = wrongExpect.Select(i => (object)Id(i)).ToList(),
                ["qc_desviado"] = Id(qcBad),
                ["vacio_permitido"] = empty.Take(4).Select(i => (object)Id(i)).ToList(),
                ["vacio_no_permitido"] = empty.Skip(4).Select(i => (object)Id(i)).ToList(),
                ["por_nivel"] = byLevel.ToDictionary(kv => "L" + kv.Key.ToString("00", CultureInfo.InvariantCulture), kv => (object)kv.Value.Select(i => (object)Id(i)).ToList()),
                ["qc"] = qc.Select(i => (object)Id(i)).ToList(),
            };
            File.WriteAllText(Path.Combine(dir, "escenario.json"), Json.Write(spec));
            return "escenario " + vp.Hash + " · " + plan.Ops.Count + " operaciones · " + vp.Ops.Count(o => o.Writes) + " escrituras · " + vp.Ops.Sum(o => o.DependsOn.Count) + " dependencias";
        }
    }
}
