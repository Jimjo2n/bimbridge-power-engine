// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Engine.Workflows
{
    /// <summary>Execution modes of a native capability (#23 §1).</summary>
    public enum ExecMode { read, preflight, plan, direct_api, interactive_invocation, ui_assisted, manual_handoff }

    /// <summary>Support level of a mode: proven (native campaign), experimental (implemented without native proof) or unavailable.</summary>
    public enum SupportLevel { proven, experimental, unavailable }

    /// <summary>What the client can actually do, measured at run time (not assumed from the tool catalog).</summary>
    public sealed class ClientEnvironment
    {
        /// <summary>An authorized UI executor that can answer Revit dialogs is present (none by default).</summary>
        public bool UiExecutorAvailable;
        public string UiExecutorName;
        /// <summary>The user authorized saving the target document in this session.</summary>
        public bool SaveAuthorized;
    }

    public sealed class ModeSupport
    {
        public ExecMode Mode;
        public SupportLevel Level;
        public string Why;
    }

    /// <summary>Versioned, discoverable descriptor of a native operation per Revit version and client environment (#23 §1).</summary>
    public sealed class NativeCapability
    {
        public const string Schema = "bimbridge.native_capability/1";
        public string Operation;
        public int RevitVersion;
        public List<ModeSupport> Modes = new List<ModeSupport>();
        public bool RequiresUiExecutor;
        public List<string> Validations = new List<string>();
        public List<string> Limitations = new List<string>();

        public SupportLevel LevelOf(ExecMode m) => Modes.FirstOrDefault(x => x.Mode == m)?.Level ?? SupportLevel.unavailable;

        /// <summary>The best mode to reach the end: direct API, else assisted UI, else manual handoff.</summary>
        public ExecMode Recommended =>
            LevelOf(ExecMode.direct_api) != SupportLevel.unavailable ? ExecMode.direct_api
            : LevelOf(ExecMode.ui_assisted) != SupportLevel.unavailable ? ExecMode.ui_assisted
            : ExecMode.manual_handoff;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["schema"] = Schema, ["operation"] = Operation, ["revit"] = (long)RevitVersion, ["recommended"] = Recommended.ToString(),
            ["requires_ui_executor"] = RequiresUiExecutor,
            ["modes"] = Modes.Select(m => (object)new Dictionary<string, object> { ["mode"] = m.Mode.ToString(), ["support"] = m.Level.ToString(), ["reason"] = m.Why }).ToList(),
            ["validations"] = Validations.Cast<object>().ToList(), ["limitations"] = Limitations.Cast<object>().ToList(),
        };
    }

    /// <summary>
    /// Native capability matrix. It relies on the offline API inventory (0.9.1 Block B): Copy/Monitor has NO public batch-copy
    /// API in 2022–2026 (there is no CopyMonitorOptions or equivalent); there is one to read links and inspect monitoring
    /// relationships (Element.IsMonitoringLinkElement / GetMonitoredLinkElementIds) and to invoke the command (PostCommand).
    /// Nothing here is "proven" until the single native campaign.
    /// </summary>
    public static class NativeCapabilities
    {
        public const string CopyMonitorFixtures = "copy_monitor_fixtures";
        public static readonly string[] Operations = { CopyMonitorFixtures };

        public static NativeCapability Describe(string operation, int revitVersion, ClientEnvironment env)
        {
            if (revitVersion < 2022 || revitVersion > 2027) throw new ArgumentOutOfRangeException(nameof(revitVersion), "Revit 2022–2027");
            env = env ?? new ClientEnvironment();
            switch (operation)
            {
                case CopyMonitorFixtures:
                {
                    var c = new NativeCapability { Operation = operation, RevitVersion = revitVersion, RequiresUiExecutor = true };
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.read, Level = SupportLevel.experimental, Why = "inventory of links, source and host fixtures with the public API" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.preflight, Level = SupportLevel.experimental, Why = "loaded link, transform, levels, categories and counts per type" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.plan, Level = SupportLevel.experimental, Why = "proposed type mapping and expected exceptions, without touching the model" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.direct_api, Level = SupportLevel.unavailable, Why = "BIMBRIDGE uses the interactive handoff to configure and run the batch copy" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.interactive_invocation, Level = SupportLevel.experimental, Why = "PostCommand(CopyMonitorSelectLink) only opens the native mode: accepting the command is not copying" });
                    c.Modes.Add(env.UiExecutorAvailable
                        ? new ModeSupport { Mode = ExecMode.ui_assisted, Level = SupportLevel.experimental, Why = "declared UI executor: " + (env.UiExecutorName ?? "unnamed") + "; each step is proven with observed evidence" }
                        : new ModeSupport { Mode = ExecMode.ui_assisted, Level = SupportLevel.unavailable, Why = "there is no authorized UI executor in this client" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.manual_handoff, Level = SupportLevel.experimental, Why = "exact steps for the user + later verification with the API" });
                    c.Validations.AddRange(new[]
                    {
                        "count and type of source fixtures per family/type", "link transform (identity or the declared one)",
                        "monitoring relationship of each copy (IsMonitoringLinkElement)", "expected family/type after the mapping", "position and orientation after the transform",
                        "duplicates, missing, wrong type and unmonitored", "save only with explicit authorization and a later reread",
                    });
                    c.Limitations.AddRange(new[]
                    {
                        "GetMonitoredLinkElementIds returns ids of link INSTANCES, not of the source fixture: the source→copy correspondence is inferred by type and position",
                        "ElementTransformUtils.CopyElements does not create monitoring relationships: it never replaces Copy/Monitor",
                        "no native campaign: no mode is 'proven'",
                    });
                    return c;
                }
                default:
                    throw new ArgumentException("unknown native operation: " + operation + ". Known: " + string.Join(", ", Operations));
            }
        }

        /// <summary>Exact steps of the native workflow for the user when there is no UI executor (truthful manual handoff).</summary>
        public static List<string> CopyMonitorManualSteps(string linkName, IEnumerable<string> typeMappings, bool saveAuthorized)
        {
            var steps = new List<string>
            {
                "Collaborate tab > Copy/Monitor > Select Link, and pick the link '" + linkName + "'.",
                "In the Copy/Monitor bar, open Coordination Settings.",
                "Plumbing Fixtures category: check 'Allow batch copy'.",
            };
            var maps = (typeMappings ?? Enumerable.Empty<string>()).ToList();
            steps.Add(maps.Count == 0
                ? "Type mapping: leave 'Copy original type' for all types."
                : "Type mapping: " + string.Join("; ", maps) + ".");
            steps.Add("Click OK and then Batch Copy; in 'Fixtures Found' choose to copy the fixtures.");
            steps.Add("Click Finish in the Copy/Monitor bar.");
            steps.Add(saveAuthorized ? "Save the target document." : "Do not save yet: the result is verified first.");
            steps.Add("Tell the assistant to read the model and verify counts, types, position and monitoring.");
            return steps;
        }
    }
}
