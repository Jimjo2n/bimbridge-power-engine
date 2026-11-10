// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// rc1: compuerta de pruebas nativas. Ningún comando que conecte con Revit (revit …, ejecutar --revit-pid) corre sin que el OPERADOR
    /// declare en la misma invocación que Revit está disponible: variable PDI_REVIT_NATIVO=AUTORIZADO y bandera --revit-disponible.
    /// Sin ambas, el comando no abre tuberías ni lee descriptores: informa el estado bloqueado y termina con código 3.
    /// </summary>
    public static class NativeGate
    {
        public const string Blocked = "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION";
        public const string EnvVar = "PDI_REVIT_NATIVO", EnvValue = "AUTORIZADO", Flag = "--revit-disponible";

        public static bool Open(string[] args, Func<string, string> env = null)
        {
            env = env ?? Environment.GetEnvironmentVariable;
            return string.Equals(env(EnvVar), EnvValue, StringComparison.Ordinal) && args.Contains(Flag);
        }

        /// <summary>Devuelve null si se puede continuar; si no, escribe el estado bloqueado y devuelve el código de salida (3).</summary>
        public static int? Check(string[] args, Func<string, string> env = null)
        {
            if (Open(args, env)) return null;
            Console.WriteLine(Blocked);
            Console.WriteLine("(para la campaña autorizada: " + EnvVar + "=" + EnvValue + " y " + Flag + ")");
            return 3;
        }
    }
}
