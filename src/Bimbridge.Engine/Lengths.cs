// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Lengths written the way a person writes them: 10'-9", 10' 9 1/2", 1/2", 3277 mm, 3.2 m, 12 in, 0.5 ft.
    /// A number without a unit is taken as feet. That way the AI does not have to convert by hand (a source of errors in pyRevit:
    /// values in mm written where Revit expected feet).
    /// </summary>
    public static class Lengths
    {
        public const double MmPerFoot = 304.8;

        private static readonly Regex FeetInches = new Regex(
            @"^(?<sign>-)?\s*(?:(?<ft>\d+(?:\.\d+)?)\s*'\s*-?\s*)?(?:(?<in>\d+(?:\.\d+)?)?\s*(?:(?<num>\d+)\s*/\s*(?<den>\d+))?\s*(?:""|in|pulg)?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WithUnit = new Regex(
            @"^(?<v>[-+]?\d+(?:[.,]\d+)?)\s*(?<u>mm|cm|m|ft|pies|in|pulg)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Converts to feet. Accepts double/long (already in feet) or text with a unit.</summary>
        public static bool TryFeet(object value, out double feet, out string error)
        {
            feet = 0; error = null;
            switch (value)
            {
                case double d: feet = d; return Finite(feet, out error);
                case long l: feet = l; return true;
                case int i: feet = i; return true;
                case string s: return TryParse(s, out feet, out error);
                case null: error = "value is missing"; return false;
                default: error = "not a length: " + value; return false;
            }
        }

        public static bool TryParse(string text, out double feet, out string error)
        {
            feet = 0; error = null;
            var s = (text ?? "").Trim().Replace("″", "\"").Replace("′", "'").Replace("’", "'").Replace("”", "\"");
            if (s.Length == 0) { error = "empty length"; return false; }

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
                    case "pulg": feet = v / 12.0; break; // Spanish "inches": a unit users type, not a legacy key
                    default: feet = v; break;
                }
                return Finite(feet, out error);
            }

            // Feet and inches: requires at least ' or " so that "10 9" is not mistaken for something else.
            if (s.IndexOf('\'') < 0 && s.IndexOf('"') < 0)
            {
                error = "length '" + text + "' not recognized: use 10'-9\", 3277 mm, 3.2 m, 12 in or decimal feet";
                return false;
            }
            var f = FeetInches.Match(s);
            if (!f.Success || (f.Groups["ft"].Value.Length == 0 && f.Groups["in"].Value.Length == 0 && f.Groups["num"].Value.Length == 0))
            {
                error = "length '" + text + "' not recognized: use 10'-9\", 10' 9 1/2\", 1/2\" or 3277 mm";
                return false;
            }
            double ft = 0, inches = 0;
            if (f.Groups["ft"].Success && f.Groups["ft"].Value.Length > 0) ft = double.Parse(f.Groups["ft"].Value, CultureInfo.InvariantCulture);
            if (f.Groups["in"].Success && f.Groups["in"].Value.Length > 0) inches = double.Parse(f.Groups["in"].Value, CultureInfo.InvariantCulture);
            if (f.Groups["num"].Success && f.Groups["num"].Value.Length > 0)
            {
                var den = double.Parse(f.Groups["den"].Value, CultureInfo.InvariantCulture);
                if (den == 0) { error = "fraction with denominator 0"; return false; }
                inches += double.Parse(f.Groups["num"].Value, CultureInfo.InvariantCulture) / den;
            }
            feet = ft + inches / 12.0;
            if (f.Groups["sign"].Success && f.Groups["sign"].Value == "-") feet = -feet;
            return Finite(feet, out error);
        }

        private static bool Finite(double v, out string error)
        {
            error = null;
            if (double.IsNaN(v) || double.IsInfinity(v)) { error = "non-finite length"; return false; }
            if (Math.Abs(v) > 100000) { error = "length out of range (more than 100,000 feet)"; return false; }
            return true;
        }

        /// <summary>10.5 → 10'-6"; rounded to 1/16".</summary>
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

        /// <summary>Signed inches with 1/16 resolution, for clearances: 0.0156 ft → "3/16\"".</summary>
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
