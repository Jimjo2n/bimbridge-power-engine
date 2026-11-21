// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bimbridge.Engine.Workflows
{
    /// <summary>An observed Revit instance: the pair (pid, process start) identifies it even if the system reuses the pid.</summary>
    public sealed class RevitInstance
    {
        public int Pid;
        public DateTime StartedUtc;
        public string Version;
        public string Build;
        public List<RevitDocumentRef> Documents = new List<RevitDocumentRef>();

        public string Identity => Pid.ToString(CultureInfo.InvariantCulture) + "@" + StartedUtc.ToUniversalTime().ToString("yyyyMMddTHHmmss.fffZ", CultureInfo.InvariantCulture);
    }

    /// <summary>Open document: the dN handle is ephemeral (per session); the stable identity comes from the add-in (e.g. path hash + model GUID).</summary>
    public sealed class RevitDocumentRef
    {
        public string Handle;       // dN, valid only in the session that issued it
        public string SessionId;    // session that issued the handle
        public string Identity;     // stable
        public string Title;
    }

    /// <summary>Fixed binding of an operation to an instance and a document (#23 §5, #25).</summary>
    public sealed class TargetBinding
    {
        public string InstanceIdentity;
        public string DocumentIdentity;
        public string Version;
        public string SessionId;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["instance"] = InstanceIdentity, ["document"] = DocumentIdentity, ["revit"] = Version, ["session_id"] = SessionId,
        };
    }

    public static class TargetResolver
    {
        /// <summary>
        /// Resolves the target of an operation. Fails closed: with several instances or documents and no explicit choice, it refuses;
        /// a dN handle from another session is not valid; the target is never inferred from desktop focus.
        /// </summary>
        public static TargetBinding Resolve(IList<RevitInstance> instances, string sessionId, string wantedVersion, string wantedPid, string handle, out string refusal)
        {
            refusal = null;
            var live = (instances ?? new List<RevitInstance>()).ToList();
            if (live.Count == 0) { refusal = "no Revit instance with BIMBRIDGE is connected"; return null; }
            var pool = live;
            if (wantedPid != null) pool = pool.Where(i => i.Pid.ToString(CultureInfo.InvariantCulture) == wantedPid.Trim()).ToList();
            if (wantedVersion != null) pool = pool.Where(i => string.Equals(i.Version, wantedVersion.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (pool.Count == 0) { refusal = "no instance matches (" + string.Join(", ", live.Select(i => "Revit " + i.Version + " pid " + i.Pid)) + ")"; return null; }
            if (pool.Count > 1) { refusal = pool.Count + " Revit instances match; give the version or pid (" + string.Join(", ", pool.Select(i => "Revit " + i.Version + " pid " + i.Pid)) + ")"; return null; }
            var inst = pool[0];
            RevitDocumentRef doc;
            if (handle != null)
            {
                doc = inst.Documents.FirstOrDefault(d => string.Equals(d.Handle, handle.Trim(), StringComparison.OrdinalIgnoreCase) && d.SessionId == sessionId);
                if (doc == null)
                {
                    refusal = inst.Documents.Any(d => string.Equals(d.Handle, handle.Trim(), StringComparison.OrdinalIgnoreCase))
                        ? "handle " + handle + " belongs to another session: dN handles are ephemeral; list the documents again"
                        : "document " + handle + " does not exist in Revit " + inst.Version;
                    return null;
                }
            }
            else
            {
                if (inst.Documents.Count != 1) { refusal = inst.Documents.Count == 0 ? "Revit " + inst.Version + " has no open documents" : inst.Documents.Count + " documents are open; give doc=dN"; return null; }
                doc = inst.Documents[0];
            }
            return new TargetBinding { InstanceIdentity = inst.Identity, DocumentIdentity = doc.Identity, Version = inst.Version, SessionId = sessionId };
        }

        /// <summary>Before each step: the binding still points to the same thing (same instance, not a reused pid, and same document).</summary>
        public static bool StillValid(TargetBinding b, IList<RevitInstance> instances, out string refusal)
        {
            refusal = null;
            if (b == null) { refusal = "operation without a bound target"; return false; }
            var inst = (instances ?? new List<RevitInstance>()).FirstOrDefault(i => i.Identity == b.InstanceIdentity);
            if (inst == null)
            {
                var pid = b.InstanceIdentity.Split('@')[0];
                refusal = (instances ?? new List<RevitInstance>()).Any(i => i.Pid.ToString(CultureInfo.InvariantCulture) == pid)
                    ? "pid " + pid + " is now used by another Revit process (restart): target changed, operation refused"
                    : "the bound Revit instance is gone (closed or restarted)";
                return false;
            }
            if (!inst.Documents.Any(d => d.Identity == b.DocumentIdentity)) { refusal = "the bound document is no longer open in that instance"; return false; }
            return true;
        }
    }

    /// <summary>Observations for the connection diagnosis (#25). Each field is measured by whoever can measure it; null = not measured.</summary>
    public sealed class ConnectionObservations
    {
        public bool? BridgeInstalled;
        public string BridgeBuild;
        public Dictionary<string, bool> ClientRegistrations = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public bool? HelloOk;
        public int? ToolsListed;
        /// <summary>BIMBRIDGE tools the agent actually sees in its own catalog (may differ from tools/list).</summary>
        public int? ToolsVisibleToAgent;
        public int? ToolsPermitted;
        public List<RevitInstance> Instances;
        public bool? UiExecutorAvailable;
    }

    public static class ConnectionDiagnostics
    {
        /// <summary>Classifies the state with a precise message. Distinguishes client registration, Bridge, Revit and target (not everything is "Revit unavailable").</summary>
        public static Dictionary<string, object> Report(ConnectionObservations o, string productBuild)
        {
            var findings = new List<object>();
            string verdict;
            void F(string code, string text) => findings.Add(new Dictionary<string, object> { ["code"] = code, ["detail"] = text });

            if (o.BridgeInstalled == false) { verdict = "bridge_not_installed"; F(verdict, "the Bridge executable is not installed"); }
            else if (o.ClientRegistrations.Count > 0 && o.ClientRegistrations.Values.All(v => !v)) { verdict = "client_unregistered"; F(verdict, "the Bridge is installed but no MCP client has it registered: " + string.Join(", ", o.ClientRegistrations.Keys)); }
            else if (o.HelloOk == false) { verdict = "bridge_no_response"; F(verdict, "the Bridge did not complete the greeting (hello)"); }
            else if (o.ToolsListed.HasValue && o.ToolsListed.Value == 0) { verdict = "bridge_no_tools"; F(verdict, "tools/list returned no tools"); }
            else if (o.ToolsListed > 0 && o.ToolsVisibleToAgent.HasValue && o.ToolsVisibleToAgent.Value == 0)
            { verdict = "tools_not_exposed_to_agent"; F(verdict, "the Bridge responds and lists " + o.ToolsListed + " tools, but the client does not expose them to the agent: this is a client registration/permissions problem, not a Revit one"); }
            else if (o.ToolsPermitted.HasValue && o.ToolsPermitted.Value == 0) { verdict = "tools_not_allowed"; F(verdict, "the client exposes the tools but the policy allows none"); }
            else if (o.Instances != null && o.Instances.Count == 0) { verdict = "revit_not_connected"; F(verdict, "the Bridge works but no Revit instance with the add-in is connected"); }
            else if (o.Instances != null && o.Instances.Count > 1) { verdict = "multiple_instances"; F(verdict, "there are " + o.Instances.Count + " Revit instances: each operation must give the version or pid"); }
            else verdict = o.HelloOk == true ? "ok" : "unmeasured";

            if (o.UiExecutorAvailable != true) F("no_ui_executor", "there is no authorized UI executor: interactive native workflows end in a manual handoff");
            if (!string.IsNullOrEmpty(o.BridgeBuild) && !string.IsNullOrEmpty(productBuild) && o.BridgeBuild != productBuild)
                F("different_builds", "the Bridge is " + o.BridgeBuild + " and the engine is " + productBuild);

            return new Dictionary<string, object>
            {
                ["verdict"] = verdict,
                ["build"] = productBuild ?? "",
                ["findings"] = findings,
                ["instances"] = (o.Instances ?? new List<RevitInstance>()).Select(i => (object)new Dictionary<string, object>
                {
                    ["pid"] = (long)i.Pid, ["revit"] = i.Version, ["build"] = i.Build ?? "", ["documents"] = (long)i.Documents.Count,
                }).ToList(),
                ["ui_executor"] = o.UiExecutorAvailable == true,
            };
        }
    }
}
