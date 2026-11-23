// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Durable identity of a document (0.6.0, §3.1). Separates four things that used to be mixed up:
    /// - <b>instance</b>: the Revit process (pid + start time);
    /// - <b>handle dN</b>: session shorthand, only valid while the document stays open in that instance;
    /// - <b>key</b> (<see cref="Key"/>): the persistent model. Cloud: project/model GUID. File: normalized path.
    ///   Unsaved: "nuevo:" + instance + sequence, so it never claims operations of another instance or of another "Project1";
    /// - <b>fingerprint</b> (<see cref="Print"/>): internal identity of the model (ProjectInformation.UniqueId, central GUID, cloud GUID…). If a model
    ///   with another fingerprint appears at the same path, the file was replaced; if the same fingerprint appears at another path, it is a copy;
    /// - <b>generation</b>: each opening (instance + sequence). A reopen does not inherit what was only in memory.
    /// The title is never used as identity.
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
            Kind = "file",
            Key = "archivo:" + NormalizePath(path),
            Print = string.IsNullOrEmpty(print) ? null : print,
            Generation = generation,
        };

        public static DocIdentity Unsaved(string instanceId, int sequence, string generation) => new DocIdentity
        {
            Kind = "new",
            Key = "nuevo:" + instanceId + "#" + sequence.ToString(CultureInfo.InvariantCulture),
            Print = null,
            Generation = generation,
        };

        public static string GenerationOf(string instanceId, int sequence) => instanceId + "#" + sequence.ToString(CultureInfo.InvariantCulture);

        /// <summary>Comparable path: uniform separators, no "." or "..", no trailing slash, case-insensitive (Windows).</summary>
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
