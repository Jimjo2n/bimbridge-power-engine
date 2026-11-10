// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Identidad duradera de un documento (0.6.0, §3.1). Separa cuatro cosas que antes se mezclaban:
    /// - <b>instancia</b>: el proceso de Revit (pid + hora de arranque);
    /// - <b>handle dN</b>: abreviatura de sesión, sólo vale mientras el documento sigue abierto en esa instancia;
    /// - <b>clave</b> (<see cref="Key"/>): el modelo persistente. Cloud: GUID de proyecto/modelo. Archivo: ruta normalizada.
    ///   Sin guardar: "nuevo:" + instancia + secuencia, así que nunca reclama operaciones de otra instancia ni de otro "Proyecto1";
    /// - <b>huella</b> (<see cref="Print"/>): identidad interna del modelo (ProjectInformation.UniqueId, GUID central, GUID cloud…). Si en la
    ///   misma ruta aparece un modelo con otra huella, el archivo fue sustituido; si la misma huella aparece en otra ruta, es una copia;
    /// - <b>generación</b>: cada apertura (instancia + secuencia). Una reapertura no hereda lo que sólo estaba en memoria.
    /// El título nunca se usa como identidad.
    /// </summary>
    public sealed class DocIdentity
    {
        public string Key;
        public string Print;
        public string Generation;
        public string Kind;

        public static DocIdentity Cloud(Guid project, Guid model, string generation) => new DocIdentity
        {
            Kind = "cloud",
            Key = "cloud:" + project.ToString("D") + "/" + model.ToString("D"),
            Print = "cloud:" + model.ToString("D"),
            Generation = generation,
        };

        public static DocIdentity File(string path, string print, string generation) => new DocIdentity
        {
            Kind = "archivo",
            Key = "archivo:" + NormalizePath(path),
            Print = string.IsNullOrEmpty(print) ? null : print,
            Generation = generation,
        };

        public static DocIdentity Unsaved(string instanceId, int sequence, string generation) => new DocIdentity
        {
            Kind = "nuevo",
            Key = "nuevo:" + instanceId + "#" + sequence.ToString(CultureInfo.InvariantCulture),
            Print = null,
            Generation = generation,
        };

        public static string GenerationOf(string instanceId, int sequence) => instanceId + "#" + sequence.ToString(CultureInfo.InvariantCulture);

        /// <summary>Ruta comparable: separadores uniformes, sin "." ni "..", sin barra final, sin distinguir mayúsculas (Windows).</summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var p = path.Trim().Replace('/', '\\');
            var unc = p.StartsWith("\\\\", StringComparison.Ordinal);
            var rooted = !unc && p.StartsWith("\\", StringComparison.Ordinal);
            var parts = p.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var stack = new System.Collections.Generic.List<string>();
            foreach (var part in parts)
            {
                if (part == ".") continue;
                if (part == "..") { if (stack.Count > 1) stack.RemoveAt(stack.Count - 1); continue; }
                stack.Add(part);
            }
            return ((unc ? "\\\\" : rooted ? "\\" : "") + string.Join("\\", stack)).ToLowerInvariant();
        }

        public override string ToString() => Key;
    }
}
