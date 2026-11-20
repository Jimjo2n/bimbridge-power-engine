// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// rc1: native test gate. No command that connects to Revit (revit …, ejecutar --revit-pid) runs unless the OPERATOR
    /// declares in the same invocation that Revit is available: variable BIMBRIDGE_REVIT_NATIVE=AUTORIZADO and flag --revit-disponible.
    /// Without both, the command opens no pipes and reads no descriptors: it reports the blocked state and exits with code 3.
    /// </summary>
    public static class NativeGate
    {
        public const string Blocked = "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION";
        public const string EnvVar = "BIMBRIDGE_REVIT_NATIVE", EnvValue = "AUTORIZADO", Flag = "--revit-disponible";

        public static bool Open(string[] args, Func<string, string> env = null)
        {
            env = env ?? Environment.GetEnvironmentVariable;
            return string.Equals(env(EnvVar), EnvValue, StringComparison.Ordinal) && args.Contains(Flag);
        }

        /// <summary>Returns null if it may continue; otherwise writes the blocked state and returns the exit code (3).</summary>
        public static int? Check(string[] args, Func<string, string> env = null)
        {
            if (Open(args, env)) return null;
            Console.WriteLine(Blocked);
            Console.WriteLine("(for the authorized campaign: " + EnvVar + "=" + EnvValue + " and " + Flag + ")");
            return 3;
        }
    }
}
