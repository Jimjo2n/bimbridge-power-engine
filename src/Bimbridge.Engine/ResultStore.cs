// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Full results stored on disk with a short handle (R055, F14). Long output is not pasted into the conversation:
    /// a summary + "res rXXXX" is returned, and any client (including a new one after compacting its context) pages it with
    /// "res rXXXX desde=… n=…". Files live 7 days in %LOCALAPPDATA%\Bimbridge\resultados.
    /// The bridge and the add-in share the folder (same user and PC).
    /// </summary>
    public static class ResultStore
    {
        private static int _seq;
        public const int KeepDays = 7;
        public const string JsonFragmentFormat = "json_fragmentos: concatenate the lines with no separator";

        public static string Folder => Home.Sub("resultados");

        /// <summary>Saves the text and returns its handle (r + short date + sequence; distinct prefix per process).</summary>
        public static string Save(string text, string origin = "a")
        {
            Directory.CreateDirectory(Folder);
            Prune();
            var n = Interlocked.Increment(ref _seq);
            var stamp = DateTime.UtcNow.ToString("MMddHHmmss", CultureInfo.InvariantCulture);
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id % 1000;
            var id = "r" + origin + stamp + pid.ToString("000", CultureInfo.InvariantCulture) + n.ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(Folder, id + ".txt"), text ?? "", new UTF8Encoding(false));
            return id;
        }

        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 40 || id[0] != 'r') return false;
            foreach (var c in id) if (!char.IsLetterOrDigit(c)) return false;
            return true;
        }

        public static string FragmentJson(string json)
        {
            var chunks = new StringBuilder(json.Length);
            for (var start = 0; start < json.Length;)
            {
                var length = Math.Min(4000, json.Length - start);
                if (start + length < json.Length && char.IsHighSurrogate(json[start + length - 1])) length--;
                chunks.Append(json, start, length).Append('\n');
                start += length;
            }
            return chunks.ToString();
        }

        public static string RenderPowerResult(Dictionary<string, object> result, int maxChars)
        {
            var json = Json.Write(result);
            if (json.Length <= maxChars) return json;
            var id = Save(FragmentJson(json), "b");
            var summary = new Dictionary<string, object>
            {
                ["res"] = id, ["formato_res"] = JsonFragmentFormat, ["lectura"] = "res " + id + " desde=0 n=2",
            };
            foreach (var key in new[] { "codigo", "estado" })
                if (result.TryGetValue(key, out var value)) summary[key] = value;
            return Json.Write(summary);
        }

        /// <summary>Reads lines [from, from+count). total = total lines; null if it does not exist.</summary>
        public static string Read(string id, int from, int count, out int total)
        {
            total = 0;
            if (!IsValidId(id)) return null;
            var path = Path.Combine(Folder, id + ".txt");
            if (!File.Exists(path)) return null;
            if (from < 0) from = 0;
            if (count <= 0) count = 200;
            var sb = new StringBuilder();
            var end = (long)from + count;
            foreach (var line in File.ReadLines(path))
            {
                if (total >= from && total < end) sb.Append(line).Append('\n');
                total++;
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static DateTime _lastPrune = DateTime.MinValue;

        private static void Prune()
        {
            if ((DateTime.UtcNow - _lastPrune).TotalHours < 6) return;
            _lastPrune = DateTime.UtcNow;
            try
            {
                foreach (var f in Directory.GetFiles(Folder, "r*.txt"))
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalDays > KeepDays) File.Delete(f);
            }
            catch (Exception) { }
        }

        /// <summary>If the text exceeds <paramref name="maxChars"/>, saves it and returns the beginning + the handle to read the rest.</summary>
        public static string Shorten(string text, int maxChars, string origin, out string id)
        {
            id = null;
            if (text == null || text.Length <= maxChars) return text;
            id = Save(text, origin);
            var lines = text.Split('\n');
            var sb = new StringBuilder();
            var shown = 0;
            foreach (var l in lines)
            {
                if (sb.Length + l.Length + 1 > maxChars) break;
                sb.Append(l).Append('\n');
                shown++;
            }
            sb.Append("… (" + shown + " of " + lines.Length + " lines; rest with: res " + id + " desde=" + shown + ")");
            return sb.ToString();
        }
    }
}
