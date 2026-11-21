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
    /// document and the MATRIZ_CAPACIDADES.md matrix is generated from here (a test compares them), so the docs never drift.
    /// "evidence" states what was actually checked: nothing that only compiles is declared tested in Revit.
    /// </summary>
    public static class Capabilities
    {
        /// <summary>Evidence levels, from lowest to highest.</summary>
        public const string Compila = "compiles 2022–2027";
        public const string Unitaria = "compiles + tested without Revit";
        public const string Fake = "compiles + tested with simulated Revit (engine/transport)";
        public const string RevitLectura2024 = "tested in Revit 2024 (read, 0.2.x)";
        public const string RevitLectura2026 = "tested in Revit 2026 (read, 0.5.0, one sample)";

        public sealed class Row
        {
            public string Orden;
            public string Que;
            public string Proyecto;
            public string Familia;
            public string Nivel;
            public int Desde = 2022;
            public string Evidencia;
            public string Notas;
        }

        public const string Si = "yes";
        public const string No = "no";

        public static readonly List<Row> All = new List<Row>
        {
            R("ctx / docs", "context, open documents with handle dN, capabilities", Si, Si, "R", RevitLectura2026, "lists RVT and RFA, type, path, modified, read-only, active"),
            R("cat", "catalog (levels, views, types, families…)", Si, "partial", "R", RevitLectura2024, "in families only types/families/views"),
            R("q / get", "queries by category, level, type, set", Si, Si, "R", RevitLectura2024, "document pinned across all pages since 0.5.0"),
            R("chk lighting_audit", "QC of fixtures and tags per view", Si, No, "R", RevitLectura2024, "per-task tag profile: tags=uno|ninguno|ignorar"),
            R("geo", "geometry: mounting, clashes, view ranges, rooms", Si, No, "R", RevitLectura2024, "links whose transform fails are excluded and reported"),
            R("show / cap", "point at and capture views", Si, Si, "R", RevitLectura2024, "only on the active document (needs the UI)"),
            R("chk model_health", "model health: grouped warnings, duplicates, empty required parameters, free MEP connectors, empty sheets and views not on a sheet", Si, No, "R", Compila, "read-only, paged; does not reproduce Revit's structural checks"),
            R("chk historial", "history of long operations with filters, progress of running ones and retry advice", Si, Si, "R", Compila, "persisted in %LOCALAPPDATA%; never advises repeating anything uncertain"),
            R("chk cobertura", "on-demand discovery of the F01–F37 catalog: compact paged rows and full contract by id", Si, Si, "R", Compila, "immutable in-memory index; does not enter the Revit thread or read assemblies"),
            R("chk copy_monitor", "Copy/Monitor: link preflight and inventory, truthful manual handoff and later verification (read-only)", Si, No, "R", Compila, "there is no public batch Copy API: never declares 'copied'; source→copy correspondence inferred by type and position"),
            R("plan / commit", "46 typed operations with diff and a single Undo (0.8.0: 2D drawing, dimensions, detail components, sheet set; 0.9.1: levels, grids, walls, floors, ceilings, rooms, ducts, pipes, circuits and schedules; also straight conduit and runs with elbows)", Si, "partial", "P/W", Compila, "in families: set, values, move, rotate, mirror, copy, delete; the 0.9.1 modeling operations only in projects"),
            R("exec consulta", "general C# over the API; everything is rolled back at the end", Si, Si, "R", Unitaria, "Roslyn 4.8 compiler; compilation tested against each year's API without Revit"),
            R("exec ejecutar", "general C# with automatic transaction, group or no transaction", Si, Si, "W", Unitaria, "tx=auto|grupo|ninguna; warnings classified by FailureDefinitionId"),
            R("fam info / caras", "types, parameters, formulas, GUID, connectors, planar faces", Si, Si, "R", RevitLectura2026, "in a project: per loaded family (connectors of one instance)"),
            R("fam buscar / cargar_archivo", "0.8.0: RFA index only in carpetas_familias; load an indexed, unchanged RFA (sha-256)", Si, No, "R / W", Unitaria, "never scans disks; cargar_archivo permission"),
            R("fam abrir", "EditFamily from the project → RFA handle (reused only if this project opened it for this family)", Si, No, "W", Compila, "0.6.0: provenance by project + family UniqueId"),
            R("fam conector", "create or reconcile a connector by semantic key and verify", No, Si, "W", Compila, "pipe, duct, electrical, conduit, cable tray; explicit radius or diameter"),
            R("fam param", "value, formula, create and associate family parameters", No, Si, "W", Compila, ""),
            R("fam cargar", "load the RFA into the project with IFamilyLoadOptions and revalidate", No, Si, "W", Compila, "sobrescribir=valores|no"),
            R("doc guardar / guardar_como", "Save / SaveAs with file and hash verification", Si, Si, "D or W", Compila, ""),
            R("doc sync", "synchronize with central and verify", Si, No, "D or W", Compila, "only workshared models"),
            R("doc pdf", "export sheets or a set to PDF and verify the files", Si, No, "D or W", Compila, "Document.Export(PDFExportOptions), available since 2022"),
            R("doc ifc", "export IFC (2x3, 4 RV, 4 DTV; model or view) and verify the file", Si, No, "D or W", Compila, "the transaction Revit requires is always rolled back: the model does not change"),
            R("doc tabla_csv", "export a schedule to CSV and compare rows", Si, No, "D or W", Compila, "ViewSchedule.Export with comma and double quotes"),
            R("doc cerrar", "close a document that is not the active one", Si, Si, "D or W", Compila, "Revit does not allow closing the active one through the API"),
            R("res", "read long results stored by handle", Si, Si, "R", Unitaria, "7 days on disk"),
            R("clave= / tarea=", "idempotency and durable operation journal", Si, Si, "—", Unitaria, "commit, exec ejecutar, fam and doc"),
            R("tarea iniciar / reanudar", "long tasks split into units, resumable, with save/sync/pdf barriers (0.6.0)", Si, Si, "W", Fake, "engine tested with injected faults; Revit backend only compiled"),
            R("tarea estado / resumen / esperar", "task memory across clients: compact summary, units, decisions", Si, Si, "R", Fake, "both profiles (compact and full)"),
            R("tarea reconciliar / cancelar", "explicit decision on unverifiable units; cancellation between units", Si, Si, "W", Fake, ""),
            R("built-in recipes", "etiquetar_luces, parametro_por_lote, qc_manifiesto, montaje_soportes, exportar_laminas", Si, No, "R/W", Compila, "compiled against each year's API by a test"),
            R("Bim.* (scripts)", "groups, mounting with real support (rays, links), view range, why it is not visible, manifest", Si, "partial", "R/W", Compila, "the QC logic (manifest, transforms, visibility) does have tests without Revit"),
            R("metricas", "characters per call and client to measure consumption (not tokens)", Si, Si, "—", Unitaria, "local to the bridge"),
            R("permisos", "0.8.0: 10 classes (read, modify project/family, load file, save, sync, export, publish, dynamic code, UI)", Si, Si, "—", Unitaria, "config permisos; empty = derived from the levels; publish always denied"),
            R("Update online (GitHub)", "0.9.1: latest public Release of the repository, no account; verifies published SHA-256, size and version before installing", Si, Si, "—", Unitaria, "only from the ribbon and only on click; needs a published Release; actualizacion_online=no disables it"),
            R("Update from ZIP", "0.8.0: manifest, RSA signature, hashes, compatibility; applied when Revit closes, with rollback", Si, Si, "—", Unitaria, "only from the ribbon; never closes Revit"),
            R("Setup /diagnostico", "installed version, loaded in each Revit, bridge, hashes and pending restart", Si, Si, "—", Unitaria, "the installer verifies manifest.json before and after copying"),
        };

        private static Row R(string orden, string que, string proyecto, string familia, string nivel, string evidencia, string notas) =>
            new Row { Orden = orden, Que = que, Proyecto = proyecto, Familia = familia, Nivel = nivel, Evidencia = evidencia, Notas = notas };

        /// <summary>Rows for ctx: only what applies to that document type ("proyecto" or "familia").</summary>
        public static List<object> ForDoc(string kind, int year)
        {
            var fam = string.Equals(kind, "familia", StringComparison.OrdinalIgnoreCase);
            var rows = new List<object>();
            foreach (var r in All)
            {
                if (year > 0 && year < r.Desde) continue;
                var v = fam ? r.Familia : r.Proyecto;
                if (v == No) continue;
                rows.Add(new object[] { r.Orden, v == Si ? "" : v, r.Nivel });
            }
            return rows;
        }

        /// <summary>What does NOT apply to that document type, with the alternative.</summary>
        public static List<string> NotFor(string kind)
        {
            var fam = string.Equals(kind, "familia", StringComparison.OrdinalIgnoreCase);
            return All.Where(r => (fam ? r.Familia : r.Proyecto) == No).Select(r => r.Orden).ToList();
        }

        public static string Markdown(string version)
        {
            var sb = new StringBuilder();
            sb.Append("# Capability matrix — BIMBRIDGE - Power Engine ").Append(version).Append("\n\n");
            sb.Append("Generated from `src/Bimbridge.Engine/Capabilities.cs` (a test checks that it matches). ");
            sb.Append("*Evidence* column: what was actually checked. What is new in 0.6.0 has not yet run inside Revit: ");
            sb.Append("it is compiled for 2022–2027 and tested without Revit; the native test is `tools/acceptance/PROCEDURE_0.6.0.md`.\n\n");
            sb.Append("| Command | What it does | Project | Family | Level | Since | Evidence | Notes |\n|---|---|---|---|---|---|---|---|\n");
            foreach (var r in All)
                sb.Append("| ").Append(r.Orden).Append(" | ").Append(r.Que).Append(" | ").Append(r.Proyecto).Append(" | ").Append(r.Familia)
                  .Append(" | ").Append(r.Nivel).Append(" | ").Append(r.Desde.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(r.Evidencia)
                  .Append(" | ").Append(r.Notas).Append(" |\n");
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
