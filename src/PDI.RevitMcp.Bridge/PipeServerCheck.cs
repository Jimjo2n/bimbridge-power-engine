// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace PDI.RevitMcp.Bridge
{
    /// <summary>Comprueba que el proceso servidor del pipe es el Revit esperado (PID anunciado y ejecutable Revit). null = correcto.</summary>
    public static class PipeServerCheck
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverPid);

        public static string Verify(NamedPipeClientStream pipe, int expectedPid)
        {
            if (!OperatingSystem.IsWindows() || expectedPid <= 0) return null; // en pruebas sin Windows no aplica
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
                return "no se pudo identificar el servidor del pipe (error " + Marshal.GetLastWin32Error() + ")";
            return Judge((int)pid, expectedPid, ProcessName((int)pid));
        }

        /// <summary>Regla pura (probada sin Windows).</summary>
        public static string Judge(int actualPid, int expectedPid, string processName)
        {
            if (actualPid != expectedPid) return "el pipe lo sirve el proceso " + actualPid + ", no el Revit anunciado (" + expectedPid + "): conexión rechazada";
            if (!string.Equals(processName, "Revit", StringComparison.OrdinalIgnoreCase)) return "el servidor del pipe no es Revit (" + processName + "): conexión rechazada";
            return null;
        }

        private static string ProcessName(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch (Exception) { return "?"; }
        }
    }
}
