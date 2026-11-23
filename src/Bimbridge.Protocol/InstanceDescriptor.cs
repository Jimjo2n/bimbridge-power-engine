// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Bimbridge.Protocol
{
    /// <summary>
    /// File that each Revit running the add-in leaves in %LOCALAPPDATA%\Bimbridge\instances\&lt;pid&gt;.json
    /// so the bridge can find it. Deleted when Revit closes; if Revit crashes, the bridge cleans it up.
    /// </summary>
    public sealed class InstanceDescriptor : IJsonWritable
    {
        public int Pid;
        public string RevitVersion;
        public string PipeName;
        public int Protocol;
        public string StartedUtc;
        public string AddinVersion;

        public static string Directory
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("BIMBRIDGE_HOME");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimbridge");
                return Path.Combine(root, "instances");
            }
        }

        public object ToJson() => new Dictionary<string, object>
        {
            ["pid"] = Pid,
            ["revit"] = RevitVersion,
            ["pipe"] = PipeName,
            ["protocol"] = Protocol,
            ["started_utc"] = StartedUtc,
            ["addin"] = AddinVersion,
        };

        public static InstanceDescriptor FromJson(string json)
        {
            var o = Json.ParseObject(json);
            return new InstanceDescriptor
            {
                Pid = (int)(Arg.GetLong(o, "pid") ?? 0),
                RevitVersion = Arg.GetString(o, "revit"),
                PipeName = Arg.GetString(o, "pipe"),
                Protocol = (int)(Arg.GetLong(o, "protocol") ?? 0),
                StartedUtc = Arg.GetString(o, "started_utc"),
                AddinVersion = Arg.GetString(o, "addin"),
            };
        }

        public string FilePath => Path.Combine(Directory, Pid + ".json");

        public void Write()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Json.Write(this));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        public void Delete()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Reads all descriptors. Unreadable ones or those of dead processes are deleted (pyRevit issue C7).</summary>
        public static List<InstanceDescriptor> ReadAll(bool deleteDead = true)
        {
            var list = new List<InstanceDescriptor>();
            if (!System.IO.Directory.Exists(Directory)) return list;
            foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
            {
                InstanceDescriptor d = null;
                try { d = FromJson(File.ReadAllText(file)); } catch (Exception) { /* unreadable */ }
                if (d == null || d.Pid <= 0 || !IsAlive(d.Pid))
                {
                    if (deleteDead) { try { File.Delete(file); } catch (Exception) { } }
                    continue;
                }
                list.Add(d);
            }
            list.Sort((a, b) => string.CompareOrdinal(b.StartedUtc ?? "", a.StartedUtc ?? ""));
            return list;
        }

        public static bool IsAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            // Pid reused by a protected process: this user's Revit could be opened, so this one is not it.
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
    }
}
