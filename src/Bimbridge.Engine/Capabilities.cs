// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Capabilities declared per operation, document type and version (R002, R059, R060, R070). ctx publishes them for the target
    /// document and the CAPABILITY_MATRIX.md matrix is generated from here (a test compares them), so the docs never drift.
    /// "evidence" states what was actually checked: nothing that only compiles is declared tested in Revit.
    /// </summary>
    public static class Capabilities
    {
        /// <summary>Evidence levels, from lowest to highest.</summary>
        public const string Compiles = "compiles 2022–2027";
        public const string UnitTested = "compiles + tested without Revit";
        public const string Fake = "compiles + tested with simulated Revit (engine/transport)";
        public const string RevitRead2024 = "tested in Revit 2024 (read, 0.2.x)";
        public const string RevitRead2026 = "tested in Revit 2026 (read, 0.5.0, one sample)";

        public sealed class Row
        {
            public string Command;
            public string What;
            public string Project;
            public string Family;
            public string Level;
            public int Since = 2022;
            public string Evidence;
            public string Notes;
        }

        public const string Yes = "yes";
        public const string No = "no";

        public static readonly List<Row> All = new List<Row>
        {
            R("ctx / docs", "context, open documents with handle dN, capabilities", Yes, Yes, "R", RevitRead2026, "lists RVT and RFA, type, path, modified, read-only, active"),
            R("cat", "catalog (levels, views, types, families…)", Yes, "partial", "R", RevitRead2024, "in families only types/families/views"),
            R("q / get", "queries by category, level, type, set", Yes, Yes, "R", RevitRead2024, "document pinned across all pages since 0.5.0"),
            R("chk lighting_audit", "QC of fixtures and tags per view", Yes, No, "R", RevitRead2024, "per-task tag profile: tags=one|none|ignore"),
            R("geo", "geometry: mounting, clashes, view ranges, rooms", Yes, No, "R", RevitRead2024, "links whose transform fails are excluded and reported"),
            R("show / cap", "point at and capture views", Yes, Yes, "R", RevitRead2024, "only on the active document (needs the UI)"),
            R("chk model_health", "model health: grouped warnings, duplicates, empty required parameters, free MEP connectors, empty sheets and views not on a sheet", Yes, No, "R", Compiles, "read-only, paged; does not reproduce Revit's structural checks"),
            R("chk history", "history of long operations with filters, progress of running ones and retry advice", Yes, Yes, "R", Compiles, "persisted in %LOCALAPPDATA%; never advises repeating anything uncertain"),
            R("chk coverage", "on-demand discovery of the F01–F37 catalog: compact paged rows and full contract by id", Yes, Yes, "R", Compiles, "immutable in-memory index; does not enter the Revit thread or read assemblies"),
            R("chk copy_monitor", "Copy/Monitor: link preflight and inventory, truthful manual handoff and later verification (read-only)", Yes, No, "R", Compiles, "there is no public batch Copy API: never declares 'copied'; source→copy correspondence inferred by type and position"),
            R("plan / commit", "46 typed operations with diff and a single Undo (0.8.0: 2D drawing, dimensions, detail components, sheet set; 0.9.1: levels, grids, walls, floors, ceilings, rooms, ducts, pipes, circuits and schedules; also straight conduit and runs with elbows)", Yes, "partial", "P/W", Compiles, "in families: set, values, move, rotate, mirror, copy, delete; the 0.9.1 modeling operations only in projects"),
            R("exec query", "general C# over the API; everything is rolled back at the end", Yes, Yes, "R", UnitTested, "Roslyn 4.8 compiler; compilation tested against each year's API without Revit"),
            R("exec run", "general C# with automatic transaction, group or no transaction", Yes, Yes, "W", UnitTested, "tx=auto|group|none; warnings classified by FailureDefinitionId"),
            R("fam info / faces", "types, parameters, formulas, GUID, connectors, planar faces", Yes, Yes, "R", RevitRead2026, "in a project: per loaded family (connectors of one instance)"),
            R("fam search / load_file", "0.8.0: RFA index only in family_folders; load an indexed, unchanged RFA (sha-256)", Yes, No, "R / W", UnitTested, "never scans disks; load_file permission"),
            R("fam open", "EditFamily from the project → RFA handle (reused only if this project opened it for this family)", Yes, No, "W", Compiles, "0.6.0: provenance by project + family UniqueId"),
            R("fam connector", "create or reconcile a connector by semantic key and verify", No, Yes, "W", Compiles, "pipe, duct, electrical, conduit, cable tray; explicit radius or diameter"),
            R("fam param", "value, formula, create and associate family parameters", No, Yes, "W", Compiles, ""),
            R("fam load", "load the RFA into the project with IFamilyLoadOptions and revalidate", No, Yes, "W", Compiles, "overwrite=values|no"),
            R("doc save / save_as", "Save / SaveAs with file and hash verification", Yes, Yes, "D or W", Compiles, ""),
            R("doc sync", "synchronize with central and verify", Yes, No, "D or W", Compiles, "only workshared models"),
            R("doc pdf", "export sheets or a set to PDF and verify the files", Yes, No, "D or W", Compiles, "Document.Export(PDFExportOptions), available since 2022"),
            R("doc ifc", "export IFC (2x3, 4 RV, 4 DTV; model or view) and verify the file", Yes, No, "D or W", Compiles, "the transaction Revit requires is always rolled back: the model does not change"),
            R("doc schedule_csv", "export a schedule to CSV and compare rows", Yes, No, "D or W", Compiles, "ViewSchedule.Export with comma and double quotes"),
            R("doc close", "close a document that is not the active one", Yes, Yes, "D or W", Compiles, "Revit does not allow closing the active one through the API"),
            R("res", "read long results stored by handle", Yes, Yes, "R", UnitTested, "7 days on disk"),
            R("key= / task=", "idempotency and durable operation journal", Yes, Yes, "—", UnitTested, "commit, exec run, fam and doc"),
            R("task start / resume", "long tasks split into units, resumable, with save/sync/pdf barriers (0.6.0)", Yes, Yes, "W", Fake, "engine tested with injected faults; Revit backend only compiled"),
            R("task status / summary / wait", "task memory across clients: compact summary, units, decisions", Yes, Yes, "R", Fake, "both profiles (compact and full)"),
            R("task reconcile / cancel", "explicit decision on unverifiable units; cancellation between units", Yes, Yes, "W", Fake, ""),
            R("built-in recipes", "tag_lights, batch_parameter, qc_manifest, mount_supports, export_sheets", Yes, No, "R/W", Compiles, "compiled against each year's API by a test"),
            R("Bim.* (scripts)", "groups, mounting with real support (rays, links), view range, why it is not visible, manifest", Yes, "partial", "R/W", Compiles, "the QC logic (manifest, transforms, visibility) does have tests without Revit"),
            R("metrics", "characters per call and client to measure consumption (not tokens)", Yes, Yes, "—", UnitTested, "local to the bridge"),
            R("permissions", "0.8.0: 10 classes (read, modify project/family, load file, save, sync, export, publish, dynamic code, UI)", Yes, Yes, "—", UnitTested, "config permissions; empty = derived from the levels; publish always denied"),
            R("Update online (GitHub)", "0.9.1: latest public Release of the repository, no account; verifies published SHA-256, size and version before installing", Yes, Yes, "—", UnitTested, "only from the ribbon and only on click; needs a published Release; online_update=no disables it"),
            R("Update from ZIP", "0.8.0: manifest, RSA signature, hashes, compatibility; applied when Revit closes, with rollback", Yes, Yes, "—", UnitTested, "only from the ribbon; never closes Revit"),
            R("Setup /diagnostics", "installed version, loaded in each Revit, bridge, hashes and pending restart", Yes, Yes, "—", UnitTested, "the installer verifies manifest.json before and after copying"),
        };

        private static Row R(string command, string what, string project, string family, string level, string evidence, string notes) =>
            new Row { Command = command, What = what, Project = project, Family = family, Level = level, Evidence = evidence, Notes = notes };

        /// <summary>Rows for ctx: only what applies to that document type ("project" or "family").</summary>
        public static List<object> ForDoc(string kind, int year)
        {
            var fam = string.Equals(kind, "family", StringComparison.OrdinalIgnoreCase);
            var rows = new List<object>();
            foreach (var r in All)
            {
                if (year > 0 && year < r.Since) continue;
                var v = fam ? r.Family : r.Project;
                if (v == No) continue;
                rows.Add(new object[] { r.Command, v == Yes ? "" : v, r.Level });
            }
            return rows;
        }

        /// <summary>What does NOT apply to that document type, with the alternative.</summary>
        public static List<string> NotFor(string kind)
        {
            var fam = string.Equals(kind, "family", StringComparison.OrdinalIgnoreCase);
            return All.Where(r => (fam ? r.Family : r.Project) == No).Select(r => r.Command).ToList();
        }

        public static string Markdown(string version)
        {
            var sb = new StringBuilder();
            sb.Append("# Capability matrix — BIMBRIDGE ").Append(version).Append("\n\n");
            sb.Append("Generated from `src/Bimbridge.Engine/Capabilities.cs` (a test checks that it matches). ");
            sb.Append("*Evidence* column: what was actually checked. What is new in 0.6.0 has not yet run inside Revit: ");
            sb.Append("it is compiled for 2022–2027 and tested without Revit; the native test is `tools/acceptance/PROCEDURE_0.6.0.md`.\n\n");
            sb.Append("| Command | What it does | Project | Family | Level | Since | Evidence | Notes |\n|---|---|---|---|---|---|---|---|\n");
            foreach (var r in All)
                sb.Append("| ").Append(r.Command).Append(" | ").Append(r.What).Append(" | ").Append(r.Project).Append(" | ").Append(r.Family)
                  .Append(" | ").Append(r.Level).Append(" | ").Append(r.Since.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(r.Evidence)
                  .Append(" | ").Append(r.Notes).Append(" |\n");
            sb.Append("\n## By version\n\n| Revit | Add-in runtime | Runner compiler | Reference API used for compiling |\n|---|---|---|---|\n");
            sb.Append("| 2022 | .NET Framework 4.8 | Roslyn 4.8 (in roslyn folder) | Nice3point 2022.1.80 |\n");
            sb.Append("| 2023 | .NET Framework 4.8 | Roslyn 4.8 (in roslyn folder) | Nice3point 2023.1.90 |\n");
            sb.Append("| 2024 | .NET Framework 4.8 | Roslyn 4.8 (in roslyn folder) | Nice3point 2024.3.60 |\n");
            sb.Append("| 2025 | .NET 8 | Roslyn 4.8 (own load context) | Nice3point 2025.4.60 |\n");
            sb.Append("| 2026 | .NET 8 | Roslyn 4.8 (own load context) | Nice3point 2026.4.10 |\n");
            sb.Append("| 2027 | .NET 10 | Roslyn 4.8 (own load context) | Nice3point 2027.0.0 |\n");
            sb.Append("\nThe real minimum build for each year is not verified: compiling against the reference API does not prove that a method exists in an earlier build. ");
            sb.Append("ctx reports the exact build and the runner reports the concrete error (MissingMethodException) if a method does not exist.\n");
            return sb.ToString();
        }
    }
}
