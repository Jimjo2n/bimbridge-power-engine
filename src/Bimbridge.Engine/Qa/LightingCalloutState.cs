// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;

namespace Bimbridge.Engine.Qa
{
    public static class LightingCalloutState
    {
        public static string Text(string code, double heightFeet)
        {
            var inches = (long)Math.Round(Math.Abs(heightFeet) * 12, MidpointRounding.AwayFromZero);
            var sign = heightFeet < 0 && inches > 0 ? "-" : "";
            return code.Trim() + " " + sign + (inches / 12).ToString(CultureInfo.InvariantCulture) + "'-" +
                (inches % 12).ToString(CultureInfo.InvariantCulture) + "\"";
        }

        public static bool IsCurrent(string text, string type, string expectedText, string expectedType, double distanceFeet) =>
            text == expectedText && type == expectedType && distanceFeet <= 1.0 / 304.8;
    }
}
