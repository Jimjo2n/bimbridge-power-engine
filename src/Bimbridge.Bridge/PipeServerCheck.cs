// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Bimbridge.Bridge
{
    /// <summary>Checks that the pipe server process is the expected Revit (announced PID and Revit executable). null = correct.</summary>
    public static class PipeServerCheck
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverPid);

        public static string Verify(NamedPipeClientStream pipe, int expectedPid)
        {
            if (!OperatingSystem.IsWindows() || expectedPid <= 0) return null; // does not apply in tests without Windows
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
                return "could not identify the pipe server (error " + Marshal.GetLastWin32Error() + ")";
            return Judge((int)pid, expectedPid, ProcessName((int)pid));
        }

        /// <summary>Pure rule (tested without Windows).</summary>
        public static string Judge(int actualPid, int expectedPid, string processName)
        {
            if (actualPid != expectedPid) return "the pipe is served by process " + actualPid + ", not the announced Revit (" + expectedPid + "): connection rejected";
            if (!string.Equals(processName, "Revit", StringComparison.OrdinalIgnoreCase)) return "the pipe server is not Revit (" + processName + "): connection rejected";
            return null;
        }

        private static string ProcessName(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch (Exception) { return "?"; }
        }
    }
}
