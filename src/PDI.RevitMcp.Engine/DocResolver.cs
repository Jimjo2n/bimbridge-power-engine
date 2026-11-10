// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Resuelve lo que pidió la IA con doc= (handle dN, ruta, clave duradera o título) entre los documentos abiertos, sin la API de Revit
    /// (se prueba sin Revit). Orden: handle → ruta exacta → ruta normalizada → clave → título. Varias coincidencias por título = ambiguo:
    /// se listan los candidatos y NO se elige ninguno (§3.1).
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

        /// <summary>Índice del documento elegido, o -1 con el motivo en <paramref name="error"/>.</summary>
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
                if (a < 0) error = "no hay ningún documento activo en Revit";
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
                error = "hay " + byTitle.Count + " documentos con el título '" + r + "': " +
                        string.Join("; ", byTitle.Select(c => c.Id + " " + (string.IsNullOrEmpty(c.Path) ? "(sin guardar)" : c.Path))) + ". Use el handle dN; no se elige ninguno";
                return -1;
            }
            error = "no hay un documento abierto '" + r + "'. Abiertos: " +
                    (snap.Count == 0 ? "ninguno" : string.Join(", ", snap.Select(c => c.Id + " " + c.Title + (c.Active ? " (activo)" : ""))));
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
