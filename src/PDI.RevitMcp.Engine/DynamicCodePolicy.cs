// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Ejecución dinámica de C# (exec), desde la revisión consolidada: HABILITADA por defecto para los agentes locales que ya pueden usar
    /// BIMBRIDGE (tubería con ACL del usuario de Windows + niveles de config.json). Un único control de usuario la bloquea:
    /// «Bloquear inyección de código C#» = config.json "bloquear_inyeccion_csharp": true. Persiste entre reinicios.
    /// Sustituye al «Código confiable» de 4 horas y a la autorización por SHA-256 (codigo_autorizado).
    /// No es un sandbox: el código corre en el proceso de Revit con los permisos del usuario; sus efectos fuera del documento no se revierten.
    /// </summary>
    public static class DynamicCodePolicy
    {
        public const string ConfigKey = "bloquear_inyeccion_csharp";
        public const string BlockedCode = "inyeccion_csharp_bloqueada";

        /// <summary>null = puede ejecutarse; texto = rechazo (nada se compiló ni se ejecutó).</summary>
        public static string Decide(bool blocked) =>
            blocked
                ? BlockedCode + ": el usuario activó «Bloquear inyección de código C#» en la cinta BIMBRIDGE. No se ejecutó nada. " +
                  "Use operaciones tipadas (plan/commit) o pídale que lo desactive."
                : null;

        /// <summary>Lee el valor persistido. Si el archivo no se puede leer, bloquea (fail-closed).</summary>
        public static bool LoadBlocked(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var o = Json.ParseObject(File.ReadAllText(path));
                return Arg.GetBool(o, ConfigKey) ?? false;
            }
            catch (Exception) { return true; }
        }

        /// <summary>Guarda el valor conservando el resto de config.json. Lanza si no puede: el cambio no quedaría persistido.</summary>
        public static void SaveBlocked(string path, bool blocked)
        {
            var o = File.Exists(path) ? Json.ParseObject(File.ReadAllText(path)) : new Dictionary<string, object>();
            o[ConfigKey] = blocked;
            Home.WriteAtomic(path, Json.Write(o));
        }
    }
}
