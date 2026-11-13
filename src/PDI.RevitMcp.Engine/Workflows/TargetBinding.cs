// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PDI.RevitMcp.Engine.Workflows
{
    /// <summary>Una instancia de Revit observada: el par (pid, inicio del proceso) la identifica aunque el sistema reutilice el pid.</summary>
    public sealed class RevitInstance
    {
        public int Pid;
        public DateTime StartedUtc;
        public string Version;
        public string Build;
        public List<RevitDocumentRef> Documents = new List<RevitDocumentRef>();

        public string Identity => Pid.ToString(CultureInfo.InvariantCulture) + "@" + StartedUtc.ToUniversalTime().ToString("yyyyMMddTHHmmss.fffZ", CultureInfo.InvariantCulture);
    }

    /// <summary>Documento abierto: el handle dN es efímero (de la sesión); la identidad estable la da el add-in (p. ej. hash de ruta + GUID del modelo).</summary>
    public sealed class RevitDocumentRef
    {
        public string Handle;       // dN, válido sólo en la sesión que lo emitió
        public string SessionId;    // sesión que emitió el handle
        public string Identity;     // estable
        public string Title;
    }

    /// <summary>Vinculación fija de una operación a una instancia y un documento (#23 §5, #25).</summary>
    public sealed class TargetBinding
    {
        public string InstanceIdentity;
        public string DocumentIdentity;
        public string Version;
        public string SessionId;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["instancia"] = InstanceIdentity, ["documento"] = DocumentIdentity, ["revit"] = Version, ["session_id"] = SessionId,
        };
    }

    public static class TargetResolver
    {
        /// <summary>
        /// Resuelve el destino de una operación. Falla cerrado: con varias instancias o documentos y sin elección explícita, rechaza;
        /// un handle dN de otra sesión no sirve; nunca se infiere el destino del foco del escritorio.
        /// </summary>
        public static TargetBinding Resolve(IList<RevitInstance> instances, string sessionId, string wantedVersion, string wantedPid, string handle, out string refusal)
        {
            refusal = null;
            var live = (instances ?? new List<RevitInstance>()).ToList();
            if (live.Count == 0) { refusal = "no hay ninguna instancia de Revit con BIMBRIDGE conectada"; return null; }
            var pool = live;
            if (wantedPid != null) pool = pool.Where(i => i.Pid.ToString(CultureInfo.InvariantCulture) == wantedPid.Trim()).ToList();
            if (wantedVersion != null) pool = pool.Where(i => string.Equals(i.Version, wantedVersion.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (pool.Count == 0) { refusal = "ninguna instancia coincide (" + string.Join(", ", live.Select(i => "Revit " + i.Version + " pid " + i.Pid)) + ")"; return null; }
            if (pool.Count > 1) { refusal = "hay " + pool.Count + " instancias de Revit que coinciden; indique versión o pid (" + string.Join(", ", pool.Select(i => "Revit " + i.Version + " pid " + i.Pid)) + ")"; return null; }
            var inst = pool[0];
            RevitDocumentRef doc;
            if (handle != null)
            {
                doc = inst.Documents.FirstOrDefault(d => string.Equals(d.Handle, handle.Trim(), StringComparison.OrdinalIgnoreCase) && d.SessionId == sessionId);
                if (doc == null)
                {
                    refusal = inst.Documents.Any(d => string.Equals(d.Handle, handle.Trim(), StringComparison.OrdinalIgnoreCase))
                        ? "el handle " + handle + " pertenece a otra sesión: los dN son efímeros; vuelva a listar los documentos"
                        : "no existe el documento " + handle + " en Revit " + inst.Version;
                    return null;
                }
            }
            else
            {
                if (inst.Documents.Count != 1) { refusal = inst.Documents.Count == 0 ? "Revit " + inst.Version + " no tiene documentos abiertos" : "hay " + inst.Documents.Count + " documentos abiertos; indique doc=dN"; return null; }
                doc = inst.Documents[0];
            }
            return new TargetBinding { InstanceIdentity = inst.Identity, DocumentIdentity = doc.Identity, Version = inst.Version, SessionId = sessionId };
        }

        /// <summary>Antes de cada paso: la vinculación sigue apuntando a lo mismo (misma instancia —no un pid reutilizado— y mismo documento).</summary>
        public static bool StillValid(TargetBinding b, IList<RevitInstance> instances, out string refusal)
        {
            refusal = null;
            if (b == null) { refusal = "operación sin destino vinculado"; return false; }
            var inst = (instances ?? new List<RevitInstance>()).FirstOrDefault(i => i.Identity == b.InstanceIdentity);
            if (inst == null)
            {
                var pid = b.InstanceIdentity.Split('@')[0];
                refusal = (instances ?? new List<RevitInstance>()).Any(i => i.Pid.ToString(CultureInfo.InvariantCulture) == pid)
                    ? "el pid " + pid + " lo usa ahora otro proceso de Revit (reinicio): destino cambiado, operación rechazada"
                    : "la instancia de Revit vinculada ya no está (cerrada o reiniciada)";
                return false;
            }
            if (!inst.Documents.Any(d => d.Identity == b.DocumentIdentity)) { refusal = "el documento vinculado ya no está abierto en esa instancia"; return false; }
            return true;
        }
    }

    /// <summary>Observaciones para el diagnóstico de conexión (#25). Cada campo lo mide quien puede medirlo; null = no medido.</summary>
    public sealed class ConnectionObservations
    {
        public bool? BridgeInstalled;
        public string BridgeBuild;
        public Dictionary<string, bool> ClientRegistrations = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public bool? HelloOk;
        public int? ToolsListed;
        /// <summary>Herramientas BIMBRIDGE que el agente ve de verdad en su propio catálogo (puede diferir de tools/list).</summary>
        public int? ToolsVisibleToAgent;
        public int? ToolsPermitted;
        public List<RevitInstance> Instances;
        public bool? UiExecutorAvailable;
    }

    public static class ConnectionDiagnostics
    {
        /// <summary>Clasifica el estado con un mensaje preciso. Distingue registro del cliente, Bridge, Revit y destino (no todo es "Revit no disponible").</summary>
        public static Dictionary<string, object> Report(ConnectionObservations o, string productBuild)
        {
            var findings = new List<object>();
            string verdict;
            void F(string code, string text) => findings.Add(new Dictionary<string, object> { ["codigo"] = code, ["detalle"] = text });

            if (o.BridgeInstalled == false) { verdict = "bridge_no_instalado"; F(verdict, "el ejecutable del Bridge no está instalado"); }
            else if (o.ClientRegistrations.Count > 0 && o.ClientRegistrations.Values.All(v => !v)) { verdict = "cliente_sin_registro"; F(verdict, "el Bridge está instalado pero ningún cliente MCP lo tiene registrado: " + string.Join(", ", o.ClientRegistrations.Keys)); }
            else if (o.HelloOk == false) { verdict = "bridge_sin_respuesta"; F(verdict, "el Bridge no completó el saludo (hello)"); }
            else if (o.ToolsListed.HasValue && o.ToolsListed.Value == 0) { verdict = "bridge_sin_herramientas"; F(verdict, "tools/list no devolvió herramientas"); }
            else if (o.ToolsListed > 0 && o.ToolsVisibleToAgent.HasValue && o.ToolsVisibleToAgent.Value == 0)
            { verdict = "herramientas_no_expuestas_al_agente"; F(verdict, "el Bridge responde y lista " + o.ToolsListed + " herramientas, pero el cliente no se las expone al agente: es un problema de registro/permisos del cliente, no de Revit"); }
            else if (o.ToolsPermitted.HasValue && o.ToolsPermitted.Value == 0) { verdict = "herramientas_no_permitidas"; F(verdict, "el cliente expone las herramientas pero la política no permite ninguna"); }
            else if (o.Instances != null && o.Instances.Count == 0) { verdict = "revit_no_conectado"; F(verdict, "el Bridge funciona pero no hay ninguna instancia de Revit con el add-in conectada"); }
            else if (o.Instances != null && o.Instances.Count > 1) { verdict = "varias_instancias"; F(verdict, "hay " + o.Instances.Count + " instancias de Revit: cada operación debe indicar versión o pid"); }
            else verdict = o.HelloOk == true ? "ok" : "sin_medir";

            if (o.UiExecutorAvailable != true) F("sin_ejecutor_ui", "no hay ejecutor de UI autorizado: los flujos nativos interactivos terminan en traspaso manual");
            if (!string.IsNullOrEmpty(o.BridgeBuild) && !string.IsNullOrEmpty(productBuild) && o.BridgeBuild != productBuild)
                F("builds_distintos", "el Bridge es " + o.BridgeBuild + " y el motor " + productBuild);

            return new Dictionary<string, object>
            {
                ["veredicto"] = verdict,
                ["build"] = productBuild ?? "",
                ["hallazgos"] = findings,
                ["instancias"] = (o.Instances ?? new List<RevitInstance>()).Select(i => (object)new Dictionary<string, object>
                {
                    ["pid"] = (long)i.Pid, ["revit"] = i.Version, ["build"] = i.Build ?? "", ["documentos"] = (long)i.Documents.Count,
                }).ToList(),
                ["ejecutor_ui"] = o.UiExecutorAvailable == true,
            };
        }
    }
}
