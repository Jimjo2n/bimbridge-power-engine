// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Longitudes escritas como las escribe una persona: 10'-9", 10' 9 1/2", 1/2", 3277 mm, 3.2 m, 12 in, 0.5 ft.
    /// Un número sin unidad se toma en pies. Así la IA no tiene que convertir a mano (fuente de errores en pyRevit:
    /// valores en mm escritos donde Revit esperaba pies).
    /// </summary>
    public static class Lengths
    {
        public const double MmPerFoot = 304.8;

        private static readonly Regex FeetInches = new Regex(
            @"^(?<sign>-)?\s*(?:(?<ft>\d+(?:\.\d+)?)\s*'\s*-?\s*)?(?:(?<in>\d+(?:\.\d+)?)?\s*(?:(?<num>\d+)\s*/\s*(?<den>\d+))?\s*(?:""|in|pulg)?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WithUnit = new Regex(
            @"^(?<v>[-+]?\d+(?:[.,]\d+)?)\s*(?<u>mm|cm|m|ft|pies|in|pulg)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Convierte a pies. Acepta double/long (ya en pies) o texto con unidad.</summary>
        public static bool TryFeet(object value, out double feet, out string error)
        {
            feet = 0; error = null;
            switch (value)
            {
                case double d: feet = d; return Finite(feet, out error);
                case long l: feet = l; return true;
                case int i: feet = i; return true;
                case string s: return TryParse(s, out feet, out error);
                case null: error = "falta el valor"; return false;
                default: error = "no es una longitud: " + value; return false;
            }
        }

        public static bool TryParse(string text, out double feet, out string error)
        {
            feet = 0; error = null;
            var s = (text ?? "").Trim().Replace("″", "\"").Replace("′", "'").Replace("’", "'").Replace("”", "\"");
            if (s.Length == 0) { error = "longitud vacía"; return false; }

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            {
                feet = plain;
                return Finite(feet, out error);
            }

            var m = WithUnit.Match(s);
            if (m.Success)
            {
                var v = double.Parse(m.Groups["v"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                switch (m.Groups["u"].Value.ToLowerInvariant())
                {
                    case "mm": feet = v / MmPerFoot; break;
                    case "cm": feet = v * 10 / MmPerFoot; break;
                    case "m": feet = v * 1000 / MmPerFoot; break;
                    case "in":
                    case "pulg": feet = v / 12.0; break;
                    default: feet = v; break;
                }
                return Finite(feet, out error);
            }

            // Pies y pulgadas: exige al menos ' o " para no confundir "10 9" con otra cosa.
            if (s.IndexOf('\'') < 0 && s.IndexOf('"') < 0)
            {
                error = "longitud '" + text + "' no reconocida: use 10'-9\", 3277 mm, 3.2 m, 12 in o pies decimales";
                return false;
            }
            var f = FeetInches.Match(s);
            if (!f.Success || (f.Groups["ft"].Value.Length == 0 && f.Groups["in"].Value.Length == 0 && f.Groups["num"].Value.Length == 0))
            {
                error = "longitud '" + text + "' no reconocida: use 10'-9\", 10' 9 1/2\", 1/2\" o 3277 mm";
                return false;
            }
            double ft = 0, inches = 0;
            if (f.Groups["ft"].Success && f.Groups["ft"].Value.Length > 0) ft = double.Parse(f.Groups["ft"].Value, CultureInfo.InvariantCulture);
            if (f.Groups["in"].Success && f.Groups["in"].Value.Length > 0) inches = double.Parse(f.Groups["in"].Value, CultureInfo.InvariantCulture);
            if (f.Groups["num"].Success && f.Groups["num"].Value.Length > 0)
            {
                var den = double.Parse(f.Groups["den"].Value, CultureInfo.InvariantCulture);
                if (den == 0) { error = "fracción con denominador 0"; return false; }
                inches += double.Parse(f.Groups["num"].Value, CultureInfo.InvariantCulture) / den;
            }
            feet = ft + inches / 12.0;
            if (f.Groups["sign"].Success && f.Groups["sign"].Value == "-") feet = -feet;
            return Finite(feet, out error);
        }

        private static bool Finite(double v, out string error)
        {
            error = null;
            if (double.IsNaN(v) || double.IsInfinity(v)) { error = "longitud no finita"; return false; }
            if (Math.Abs(v) > 100000) { error = "longitud fuera de rango (más de 100 000 pies)"; return false; }
            return true;
        }

        /// <summary>10.5 → 10'-6"; redondeado a 1/16".</summary>
        public static string FeetInches16(double ft)
        {
            if (double.IsNaN(ft)) return "";
            var sign = ft < 0 ? "-" : "";
            var sixteenths = (long)Math.Round(Math.Abs(ft) * 192);
            var feet = sixteenths / 192;
            var rem = sixteenths % 192;
            var inches = rem / 16;
            var frac = rem % 16;
            var fracText = "";
            if (frac != 0)
            {
                long den = 16, num = frac;
                while (num % 2 == 0) { num /= 2; den /= 2; }
                fracText = " " + num + "/" + den;
            }
            return sign + feet + "'-" + inches + fracText + "\"";
        }

        /// <summary>Pulgadas con signo y 1/16 de resolución, para holguras: 0.0156 ft → "3/16\"".</summary>
        public static string Inches16(double ft)
        {
            if (double.IsNaN(ft)) return "";
            var sign = ft < 0 ? "-" : "";
            var sixteenths = (long)Math.Round(Math.Abs(ft) * 192);
            var whole = sixteenths / 16;
            var frac = sixteenths % 16;
            string fracText = "";
            if (frac != 0)
            {
                long den = 16, num = frac;
                while (num % 2 == 0) { num /= 2; den /= 2; }
                fracText = num + "/" + den;
            }
            if (whole == 0 && frac == 0) return "0\"";
            return sign + (whole > 0 ? whole.ToString(CultureInfo.InvariantCulture) + (fracText.Length > 0 ? " " : "") : "") + fracText + "\"";
        }

        public static double Round(double ft, bool mm) => mm ? Math.Round(ft * MmPerFoot, 1) : Math.Round(ft, 4);
    }
}
