// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Resultados completos guardados en disco con un handle corto (R055, F14). Lo largo no se pega en la conversación:
    /// se devuelve un resumen + "res rXXXX", y cualquier cliente (también uno nuevo tras compactar su contexto) lo pagina con
    /// "res rXXXX desde=… n=…". Los archivos viven 7 días en %LOCALAPPDATA%\PDI.RevitMcp\resultados.
    /// El puente y el add-in comparten la carpeta (mismo usuario y PC).
    /// </summary>
    public static class ResultStore
    {
        private static int _seq;
        public const int KeepDays = 7;

        public static string Folder => Home.Sub("resultados");

        /// <summary>Guarda el texto y devuelve su handle (r + fecha corta + secuencia; prefijo distinto por proceso).</summary>
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

        /// <summary>Lee líneas [from, from+count). total = líneas totales; null si no existe.</summary>
        public static string Read(string id, int from, int count, out int total)
        {
            total = 0;
            if (!IsValidId(id)) return null;
            var path = Path.Combine(Folder, id + ".txt");
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            total = lines.Length;
            if (from < 0) from = 0;
            if (count <= 0) count = 200;
            var sb = new StringBuilder();
            for (var i = from; i < Math.Min(lines.Length, from + count); i++) sb.Append(lines[i]).Append('\n');
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

        /// <summary>Si el texto pasa de <paramref name="maxChars"/>, lo guarda y devuelve el inicio + el handle para leer el resto.</summary>
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
            sb.Append("… (" + shown + " de " + lines.Length + " líneas; resto con: res " + id + " desde=" + shown + ")");
            return sb.ToString();
        }
    }
}
