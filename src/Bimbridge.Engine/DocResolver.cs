// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Resolves what the AI requested with doc= (handle dN, path, durable key or title) among the open documents, without the Revit API
    /// (tested without Revit). Order: handle → exact path → normalized path → key → title. Several title matches = ambiguous:
    /// the candidates are listed and NONE is chosen (§3.1).
    /// </summary>
    public static class DocResolver
    {
        public sealed class Candidate
        {
            public string Id;
            public string Key;
            public string Title;
            public string Path;
            public bool Active;
        }

        /// <summary>Index of the chosen document, or -1 with the reason in <paramref name="error"/>.</summary>
        public static int Find(IReadOnlyList<Candidate> snap, string requested, out string error, out bool ambiguous)
        {
            error = null;
            ambiguous = false;
            int One(Func<Candidate, bool> pred)
            {
                var hits = Enumerable.Range(0, snap.Count).Where(i => pred(snap[i])).ToList();
                return hits.Count == 1 ? hits[0] : hits.Count == 0 ? -1 : -2;
            }
            if (string.IsNullOrWhiteSpace(requested))
            {
                var a = One(c => c.Active);
                if (a < 0) error = "there is no active document in Revit";
                return a < 0 ? -1 : a;
            }
            var r = requested.Trim();
            var k = One(c => string.Equals(c.Id, r, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) return k;
            k = One(c => !string.IsNullOrEmpty(c.Path) && string.Equals(c.Path, r, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) return k;
            var norm = DocIdentity.NormalizePath(r);
            k = One(c => !string.IsNullOrEmpty(c.Path) && DocIdentity.NormalizePath(c.Path) == norm);
            if (k >= 0) return k;
            k = One(c => string.Equals(c.Key, r, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) return k;
            var byTitle = snap.Where(c => TitleMatches(c.Title, r)).ToList();
            if (byTitle.Count == 1) return snap.ToList().IndexOf(byTitle[0]);
            if (byTitle.Count > 1)
            {
                ambiguous = true;
                error = "there are " + byTitle.Count + " documents with the title '" + r + "': " +
                        string.Join("; ", byTitle.Select(c => c.Id + " " + (string.IsNullOrEmpty(c.Path) ? "(unsaved)" : c.Path))) + ". Use the handle dN; none is chosen";
                return -1;
            }
            error = "there is no open document '" + r + "'. Open: " +
                    (snap.Count == 0 ? "none" : string.Join(", ", snap.Select(c => c.Id + " " + c.Title + (c.Active ? " (active)" : ""))));
            return -1;
        }

        public static bool TitleMatches(string title, string wanted)
        {
            if (title == null || wanted == null) return false;
            string Strip(string s) { s = s.Trim(); foreach (var ext in new[] { ".rvt", ".rfa", ".rte", ".rft" }) if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return s.Substring(0, s.Length - ext.Length); return s; }
            return string.Equals(Strip(title), Strip(wanted), StringComparison.OrdinalIgnoreCase);
        }
    }
}
