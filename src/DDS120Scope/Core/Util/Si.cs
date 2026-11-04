using System;
using System.Globalization;

namespace Dds120.Util
{
    // Engineering-notation formatting: 0.0025 s -> "2.5 ms", 1500 Hz -> "1.5 kHz".
    public static class Si
    {
        static readonly string[] Prefixes = { "p", "n", "µ", "m", "", "k", "M", "G" };

        public static string Format(double value, string unit, int digits = 3)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "--- " + unit;
            if (value == 0)
                return "0 " + unit;
            double a = Math.Abs(value);
            int exp = (int)Math.Floor(Math.Log10(a) / 3);
            exp = Math.Max(-4, Math.Min(3, exp));
            double scaled = value / Math.Pow(1000, exp);
            // Round to the requested significant digits, then re-check for 1000.0 overflow.
            int intDigits = (int)Math.Floor(Math.Log10(Math.Abs(scaled))) + 1;
            int decimals = Math.Max(0, digits - intDigits);
            scaled = Math.Round(scaled, decimals);
            if (Math.Abs(scaled) >= 1000 && exp < 3)
            {
                scaled /= 1000;
                exp++;
                decimals = Math.Max(0, digits - 1);
            }
            return scaled.ToString("F" + decimals, CultureInfo.InvariantCulture) + " " + Prefixes[exp + 4] + unit;
        }

        // Compact form for settings labels, trims trailing zeros: 0.02 -> "20 mV".
        public static string Short(double value, string unit)
        {
            if (value == 0)
                return "0 " + unit;
            double a = Math.Abs(value);
            int exp = (int)Math.Floor(Math.Log10(a * 1.0000001) / 3);
            exp = Math.Max(-4, Math.Min(3, exp));
            double scaled = value / Math.Pow(1000, exp);
            return scaled.ToString("0.###", CultureInfo.InvariantCulture) + " " + Prefixes[exp + 4] + unit;
        }
    }
}
