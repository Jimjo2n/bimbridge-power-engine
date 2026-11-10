// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Protocol
{
    /// <summary>
    /// 0.8.0: permisos separados por clase de operación. Cada petición pertenece a UNA clase; si config.json trae "permisos", sólo
    /// pasan las clases listadas (además de los niveles R/P/W/D de siempre, que siguen aplicando). Publicar no tiene ninguna operación:
    /// se deniega siempre. Las operaciones de interfaz (show/cap) son su propia clase porque cambian lo que ve el usuario.
    /// </summary>
    public static class Permissions
    {
        public const string Read = "leer", ModifyProject = "modificar_proyecto", ModifyFamily = "modificar_familia", LoadFile = "cargar_archivo",
            Save = "guardar", Sync = "sincronizar", Export = "exportar", Publish = "publicar", DynamicCode = "codigo_dinamico", Ui = "interfaz";

        public static readonly string[] All = { Read, ModifyProject, ModifyFamily, LoadFile, Save, Sync, Export, Publish, DynamicCode, Ui };

        /// <summary>Clase de la petición. <paramref name="familyTarget"/>: el documento destino es una familia (RFA).</summary>
        public static string Of(string op, IDictionary<string, object> args, bool familyTarget)
        {
            var kind = OpSemantics.Classify(op, args, out _);
            switch (op)
            {
                case Wire.OpExec:
                    return kind == OpSemantics.Kind.Read ? Read : DynamicCode;   // consulta revertida = lectura; ensayo/ejecutar = código dinámico
                case Wire.OpFam:
                    var f = OpSemantics.FamVerb(args);
                    if (f == "cargar_archivo") return LoadFile;
                    if (f == "cargar") return ModifyProject;                    // carga en el proyecto un RFA ya abierto
                    return kind == OpSemantics.Kind.Read ? Read : ModifyFamily;
                case Wire.OpDoc:
                    switch ((Arg.GetString(args, "accion") ?? "").Trim().ToLowerInvariant())
                    {
                        case "sync": case "sincronizar": return Sync;
                        case "pdf": return Export;
                        default: return Save;                                    // guardar, guardar_como, cerrar (cerrar puede guardar)
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
        /// Permisos efectivos: los explícitos de config (si hay) o los derivados de los niveles. Publicar nunca se concede: no existe
        /// ninguna operación de publicación en BIMBRIDGE y un permiso sin operación no debe parecer disponible.
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
                // P sin W: los commits quedan retenidos para que el usuario los aplique (cola); la clase debe pasar para llegar a esa cola.
                if (levels.Contains("P")) { set.Add(ModifyProject); set.Add(ModifyFamily); }
                if (levels.Contains("W")) { set.Add(ModifyProject); set.Add(ModifyFamily); set.Add(LoadFile); set.Add(DynamicCode); set.Add(Save); set.Add(Sync); set.Add(Export); }
                if (levels.Contains("D")) { set.Add(Save); set.Add(Sync); set.Add(Export); }
            }
            set.Remove(Publish);
            return set;
        }

        /// <summary>null si se permite; si no, el motivo para la IA.</summary>
        public static string Deny(string permission, HashSet<string> effective) =>
            effective.Contains(permission) ? null
            : permission == Publish ? "publicar no está soportado en BIMBRIDGE"
            : "permiso '" + permission + "' no concedido en config.json (permisos: " + string.Join(", ", effective.OrderBy(x => x, StringComparer.Ordinal)) + "). Pídaselo al usuario; no lo intente por otra vía.";
    }
}
