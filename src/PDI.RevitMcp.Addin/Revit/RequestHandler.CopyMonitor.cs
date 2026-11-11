// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Workflows;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// 0.9.1 (Bloque F, #23/#24): check name=copy_monitor, de SOLO LECTURA.
    ///  - fase=preflight (por defecto): vínculo, transformación, aparatos de origen por tipo, aparatos ya presentes en el anfitrión
    ///    (se guardan como conjunto $cm_antes para la verificación), la capacidad nativa real de este cliente y los pasos del traspaso
    ///    manual. No hay API pública de Copia por lotes: nunca se declara "copiado" desde aquí.
    ///  - fase=verificar: relee el anfitrión y compara con el origen (tipo esperado tras el mapeo, posición, orientación, supervisión,
    ///    duplicados). Una copia sin relación de supervisión falla.
    /// </summary>
    public sealed partial class RequestHandler
    {
        private async Task<Response> CopyMonitorCheck(Session session, Request request, CancellationToken ct)
        {
            var link = Arg.GetString(request.Args, "vinculo");
            if (string.IsNullOrWhiteSpace(link)) return Response.Fail(request.Id, Status.Invalido, "copy_monitor: falta vinculo (nombre o id de la instancia del vínculo)");
            var phase = (Arg.GetString(request.Args, "fase") ?? "preflight").Trim().ToLowerInvariant();
            if (phase != "preflight" && phase != "verificar") return Response.Fail(request.Id, Status.Invalido, "copy_monitor fase: preflight o verificar");
            var maps = new List<TypeMapping>();
            if (request.Args.TryGetValue("mapeo", out var m) && m != null)
            {
                if (!(m is Dictionary<string, object> md)) return Response.Fail(request.Id, Status.Invalido, "copy_monitor mapeo: {\"Familia: Tipo origen\": \"Familia: Tipo destino\"}");
                maps.AddRange(md.Select(kv => new TypeMapping { Source = kv.Key, Target = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) }));
            }
            maps.AddRange(Arg.GetStringList(request.Args, "excluir").Select(x => new TypeMapping { Source = x.Trim(), Excluded = true }));

            var probe = new CopyMonitorProbe(link, Arg.GetString(request.Args, "categoria"));
            var sw = Stopwatch.StartNew();
            var result = await RunMain((app, watch, budget) => probe.Step(app, watch, budget, _tracker),
                highPriority: true, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!result.Ok) return FromWork(request.Id, result);
            if (probe.Error != null) return Response.Fail(request.Id, Status.Invalido, probe.Error);

            var setName = "cm_antes_" + probe.LinkInstanceId.ToString(CultureInfo.InvariantCulture);
            var datos = new Dictionary<string, object>
            {
                ["vinculo"] = probe.LinkName, ["vinculo_id"] = probe.LinkInstanceId,
                ["transformacion"] = new Dictionary<string, object>
                {
                    ["dx"] = probe.Transform.Dx, ["dy"] = probe.Transform.Dy, ["dz"] = probe.Transform.Dz, ["rot_deg"] = probe.Transform.RotationDeg,
                    ["identidad"] = Math.Abs(probe.Transform.Dx) + Math.Abs(probe.Transform.Dy) + Math.Abs(probe.Transform.Dz) < 1e-9 && Math.Abs(probe.Transform.RotationDeg) < 1e-9,
                },
                ["origen"] = (long)probe.Source.Count,
                ["origen_por_tipo"] = probe.Source.GroupBy(s => CopyMonitorVerifier.Key(s.Family, s.Type)).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["anfitrion"] = (long)probe.Host.Count,
                ["anfitrion_supervisados"] = (long)probe.Host.Count(h => h.Monitoring),
            };

            if (phase == "preflight")
            {
                session.Sets.Save(setName, probe.Host.Select(h => h.Id), TargetId, TargetTitle);
                datos["conjunto_antes"] = "$" + setName;
                var year = int.TryParse(probe.RevitVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : 0;
                if (year >= 2022 && year <= 2026)
                {
                    // Sin ejecutor de UI autorizado (este add-in no lo tiene): el camino veraz es el traspaso manual.
                    var cap = NativeCapabilities.Describe(NativeCapabilities.CopyMonitorFixtures, year, new ClientEnvironment());
                    datos["capacidad"] = cap.ToJson();
                }
                datos["pasos_manuales"] = NativeCapabilities.CopyMonitorManualSteps(probe.LinkName,
                    maps.Where(x => !x.Excluded).Select(x => x.Source + " → " + x.Target).Concat(maps.Where(x => x.Excluded).Select(x => x.Source + " → no copiar")), false).Cast<object>().ToList();
                datos["siguiente"] = "tras Finalizar en Revit: check name=copy_monitor vinculo=" + probe.LinkInstanceId + " fase=verificar (mismo mapeo/excluir)";
            }
            else
            {
                if (!session.Sets.TryGet(setName, TargetId, out var before, out var setError))
                    return Response.Fail(request.Id, Status.Invalido, "copy_monitor verificar: no hay inventario previo $" + setName + " (" + (setError ?? "ejecute fase=preflight antes de copiar") + ")");
                var beforeSet = new HashSet<long>(before);
                var hostBefore = probe.Host.Where(h => beforeSet.Contains(h.Id)).ToList();
                var report = CopyMonitorVerifier.Verify(probe.Source, hostBefore, probe.Host, probe.Transform, maps, probe.LinkInstanceId);
                foreach (var kv in report) datos[kv.Key] = kv.Value;
                datos["nota"] = "relación origen→copia inferida por tipo y posición: GetMonitoredLinkElementIds devuelve instancias de vínculo, no aparatos de origen";
            }
            datos["ms"] = sw.ElapsedMilliseconds;
            return new Response { Id = request.Id, Rev = probe.Rev, Datos = datos };
        }
    }
}
