// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PDI.RevitMcp.Protocol
{
    /// <summary>
    /// 0.9.0: dos perfiles de funcionamiento con políticas distintas, comprobadas por pruebas (no una bandera cosmética).
    ///
    /// PRODUCCION (por defecto): documentos reales. Código dinámico sólo si su SHA-256 está autorizado por el usuario para ese
    /// documento (o para todos) en config.json; operaciones EXPERIMENTAL rechazadas; actualizaciones sólo firmadas por una clave
    /// de confianza.
    ///
    /// LABORATORIO: investigación con recursos sintéticos, desechables o autorizados. Permite código dinámico sin autorización por
    /// hash y operaciones experimentales, pero SÓLO sobre documentos cuya ruta local esté dentro de las carpetas de laboratorio
    /// configuradas (nunca nube, central ni rutas fuera); un paquete de actualización sin firma sólo si el usuario lo habilitó
    /// explícitamente para el laboratorio. El perfil no levanta ninguna otra protección (permisos, niveles, auditoría, diario).
    ///
    /// La ejecución nativa en Revit sigue prohibida por una restricción independiente del perfil (NativeGate).
    /// </summary>
    public sealed class ProfilePolicy
    {
        public const string Production = "produccion", Lab = "laboratorio";

        public string Name = Production;
        /// <summary>Carpetas locales de laboratorio (perfil LAB). Vacío = ningún documento es de laboratorio.</summary>
        public List<string> LabFolders = new List<string>();
        /// <summary>LAB: el usuario aceptó instalar paquetes de actualización sin firma en este equipo de laboratorio.</summary>
        public bool LabUnsignedUpdates;
        /// <summary>PRODUCCION: código dinámico autorizado: "sha256" (cualquier documento) o "sha256@clave_documento".</summary>
        public List<string> AuthorizedCode = new List<string>();

        public bool IsLab => Name == Lab;

        public static ProfilePolicy Parse(string name, IEnumerable<string> labFolders, bool labUnsigned, IEnumerable<string> authorizedCode)
        {
            var n = (name ?? Production).Trim().ToLowerInvariant();
            if (n == "lab" || n == "experimental") n = Lab;
            if (n != Lab) n = Production;   // un valor desconocido NUNCA abre el laboratorio
            return new ProfilePolicy
            {
                Name = n,
                LabFolders = (labFolders ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(NormFolder).ToList(),
                LabUnsignedUpdates = n == Lab && labUnsigned,
                AuthorizedCode = (authorizedCode ?? Enumerable.Empty<string>()).Select(x => (x ?? "").Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList(),
            };
        }

        private static string NormFolder(string f)
        {
            var p = f.Trim().Replace('/', '\\').TrimEnd('\\');
            return p.ToLowerInvariant() + "\\";
        }

        /// <summary>
        /// Ruta local de una clave de documento ("archivo:c:\x\y.rvt"); null si es de nube, central o desconocida.
        /// Las claves que no son de archivo local nunca cuentan como laboratorio.
        /// </summary>
        public static string LocalPathOf(string docKey)
        {
            if (string.IsNullOrEmpty(docKey)) return null;
            const string pfx = "archivo:";
            if (!docKey.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) return null;
            var p = docKey.Substring(pfx.Length).Trim().Replace('/', '\\');
            if (p.StartsWith("\\\\", StringComparison.Ordinal)) return null;   // UNC: carpeta de red, no laboratorio local
            if (p.Contains("..")) return null;                                   // sin atajos de ruta
            return p.ToLowerInvariant();
        }

        public bool IsLabDocument(string docKey)
        {
            var p = LocalPathOf(docKey);
            return p != null && LabFolders.Any(f => p.StartsWith(f, StringComparison.Ordinal));
        }

        /// <summary>
        /// Política del perfil para una petición ya clasificada. null = permitida por el perfil (los permisos y niveles se comprueban
        /// aparte). <paramref name="capabilityState"/>: estado del registro de capacidades para la operación (EXPERIMENTAL, AVAILABLE…).
        /// </summary>
        public string DenyRequest(string op, string permission, string docKey, string capabilityState)
        {
            var experimental = string.Equals(capabilityState, CapabilityStates.Experimental, StringComparison.Ordinal);
            if (!IsLab)
            {
                if (experimental) return "perfil_produccion: la operación " + op + " es EXPERIMENTAL y sólo existe en el perfil de laboratorio";
                return null;
            }
            // LAB: nada fuera del laboratorio, ni siquiera lectura de documentos (las listas de contexto no tocan ningún modelo).
            if (op == Wire.OpContext || op == Wire.OpDocs || op == Wire.OpCatalog) return null;
            if (permission == Permissions.Ui) return null;
            if (string.IsNullOrEmpty(docKey)) return null;   // sin documento (catálogo, ayuda): nada que proteger
            if (!IsLabDocument(docKey))
                return "perfil_laboratorio: el documento no está en una carpeta de laboratorio (" + (LabFolders.Count == 0 ? "no hay ninguna configurada" : string.Join("; ", LabFolders)) +
                       "). El laboratorio sólo trabaja con copias locales desechables o autorizadas; nunca nube, central ni modelos de trabajo.";
            return null;
        }

        /// <summary>Código dinámico: en producción exige autorización por SHA-256 (y alcance de documento). null = permitido.</summary>
        public string DenyDynamicCode(string codeSha256, string docKey)
        {
            if (IsLab) return IsLabDocument(docKey) || string.IsNullOrEmpty(docKey) ? null : "perfil_laboratorio: código dinámico fuera de una carpeta de laboratorio";
            var sha = (codeSha256 ?? "").ToLowerInvariant();
            if (sha.Length == 0) return "codigo_no_autorizado: falta el SHA-256 del código";
            var key = (docKey ?? "").ToLowerInvariant();
            foreach (var a in AuthorizedCode)
            {
                var at = a.IndexOf('@');
                if (at < 0 ? a == sha : a.Substring(0, at) == sha && a.Substring(at + 1) == key) return null;
            }
            return "codigo_no_autorizado: en el perfil de producción el código dinámico sólo corre si el usuario autorizó su SHA-256 (" + sha.Substring(0, Math.Min(16, sha.Length)) +
                   "…) para este documento en config.json (codigo_autorizado). Use una operación tipada o pídale la autorización; no lo intente por otra vía.";
        }

        /// <summary>Paquetes de actualización sin firma: sólo en laboratorio y sólo si el usuario lo habilitó. null = admitido (con confirmación).</summary>
        public string DenyUnsignedUpdate() =>
            IsLab && LabUnsignedUpdates ? null
            : IsLab ? "perfil_laboratorio: los paquetes sin firma están deshabilitados (laboratorio.actualizacion_sin_firma=false)"
            : "perfil_produccion: sólo se instalan paquetes firmados por una clave de confianza (procedencia criptográfica obligatoria)";

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["perfil"] = Name, ["carpetas_laboratorio"] = LabFolders.Cast<object>().ToList(), ["actualizacion_sin_firma"] = LabUnsignedUpdates,
            ["codigo_autorizado"] = (long)AuthorizedCode.Count,
        };
    }

    /// <summary>0.9.0: estados del registro de capacidades (descubrimiento bajo demanda). No confundir con los de la matriz de pruebas.</summary>
    public static class CapabilityStates
    {
        public const string Available = "AVAILABLE", Experimental = "EXPERIMENTAL", RequiresAuthorization = "REQUIRES_AUTHORIZATION",
            NativeTestPending = "NATIVE_TEST_PENDING", Deferred = "DEFERRED", ApiLimitation = "API_LIMITATION";
        public static readonly string[] All = { Available, Experimental, RequiresAuthorization, NativeTestPending, Deferred, ApiLimitation };
    }
}
