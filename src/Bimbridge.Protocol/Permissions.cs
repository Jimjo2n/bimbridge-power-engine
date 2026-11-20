// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Protocol
{
    /// <summary>
    /// 0.8.0: permissions split by operation class. Each request belongs to ONE class; if config.json has "permisos", only
    /// the listed classes pass (on top of the usual R/P/W/D levels, which still apply). Publish has no operation:
    /// it is always denied. UI operations (show/cap) are their own class because they change what the user sees.
    /// </summary>
    public static class Permissions
    {
        public const string Read = "leer", ModifyProject = "modificar_proyecto", ModifyFamily = "modificar_familia", LoadFile = "cargar_archivo",
            Save = "guardar", Sync = "sincronizar", Export = "exportar", Publish = "publicar", DynamicCode = "codigo_dinamico", Ui = "interfaz";

        public static readonly string[] All = { Read, ModifyProject, ModifyFamily, LoadFile, Save, Sync, Export, Publish, DynamicCode, Ui };

        /// <summary>Request class. <paramref name="familyTarget"/>: the target document is a family (RFA).</summary>
        public static string Of(string op, IDictionary<string, object> args, bool familyTarget)
        {
            var kind = OpSemantics.Classify(op, args, out _);
            switch (op)
            {
                case Wire.OpExec:
                    return kind == OpSemantics.Kind.Read ? Read : DynamicCode;   // rolled-back query = read; trial/run = dynamic code
                case Wire.OpFam:
                    var f = OpSemantics.FamVerb(args);
                    if (f == "cargar_archivo") return LoadFile;
                    if (f == "cargar") return ModifyProject;                    // loads an already open RFA into the project
                    return kind == OpSemantics.Kind.Read ? Read : ModifyFamily;
                case Wire.OpDoc:
                    switch ((Arg.GetString(args, "accion") ?? "").Trim().ToLowerInvariant())
                    {
                        case "sync": case "sincronizar": return Sync;
                        case "pdf": return Export;
                        default: return Save;                                    // guardar, guardar_como, cerrar (cerrar may save)
                    }
                case Wire.OpCommit:
                    return familyTarget ? ModifyFamily : ModifyProject;
                case Wire.OpTask:
                    return kind == OpSemantics.Kind.Read ? Read : familyTarget ? ModifyFamily : ModifyProject;
                case Wire.OpShow:
                case Wire.OpCapture:
                    return Ui;
                default:
                    return Read;
            }
        }

        /// <summary>
        /// Effective permissions: the explicit ones from config (if any) or those derived from the levels. Publish is never granted: there is
        /// no publish operation in BIMBRIDGE and a permission with no operation must not look available.
        /// </summary>
        public static HashSet<string> Effective(IEnumerable<string> configured, string levels)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var list = configured?.Select(x => (x ?? "").Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList();
            if (list != null && list.Count > 0) { foreach (var p in list) if (All.Contains(p)) set.Add(p); }
            else
            {
                levels = (levels ?? "").ToUpperInvariant();
                if (levels.Contains("R")) { set.Add(Read); set.Add(Ui); }
                // P without W: commits are held for the user to apply (queue); the class must pass to reach that queue.
                if (levels.Contains("P")) { set.Add(ModifyProject); set.Add(ModifyFamily); }
                if (levels.Contains("W")) { set.Add(ModifyProject); set.Add(ModifyFamily); set.Add(LoadFile); set.Add(DynamicCode); set.Add(Save); set.Add(Sync); set.Add(Export); }
                if (levels.Contains("D")) { set.Add(Save); set.Add(Sync); set.Add(Export); }
            }
            set.Remove(Publish);
            return set;
        }

        /// <summary>null if allowed; otherwise the reason for the AI.</summary>
        public static string Deny(string permission, HashSet<string> effective) =>
            effective.Contains(permission) ? null
            : permission == Publish ? "publicar is not supported in BIMBRIDGE"
            : "permission '" + permission + "' not granted in config.json (permisos: " + string.Join(", ", effective.OrderBy(x => x, StringComparer.Ordinal)) + "). Ask the user for it; do not try another way.";
    }
}
