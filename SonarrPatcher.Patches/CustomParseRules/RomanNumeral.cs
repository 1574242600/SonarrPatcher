using System;

namespace SonarrPatcher.Patches
{
    /// <summary>
    /// Strict Roman numeral (I, IV, V, IX, X, XL, L, XC) to integer conversion, capped
    /// at the tens place (1-99, e.g. XC=90, XCIX=99). Only canonical forms are accepted,
    /// e.g. "IV" (4) but not "IIII"; hundreds/thousands (C, D, M) are rejected.
    /// </summary>
    internal static class RomanNumeral
    {
        public static bool TryParse(string input, out int result)
        {
            result = 0;

            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var text = input.Trim().ToUpperInvariant();

            if (text.Length == 0 || text.Length > 15)
            {
                return false;
            }

            var total = 0;
            var i = 0;

            while (i < text.Length)
            {
                if (i + 1 < text.Length && PairValue(text.Substring(i, 2)) is int pair)
                {
                    total += pair;
                    i += 2;
                    continue;
                }

                if (SingleValue(text[i]) is int single)
                {
                    total += single;
                    i += 1;
                    continue;
                }

                return false;
            }

            if (total <= 0 || total > 99 || ToRoman(total) != text)
            {
                result = 0;
                return false;
            }

            result = total;
            return true;
        }

        internal static string ToRoman(int value)
        {
            if (value <= 0 || value > 99)
            {
                return string.Empty;
            }

            var roman = string.Empty;
            var remaining = value;

            foreach (var pair in Pairs)
            {
                while (remaining >= pair.Value)
                {
                    roman += pair.Key;
                    remaining -= pair.Value;
                }
            }

            return roman;
        }

        private static readonly (string Key, int Value)[] Pairs =
        {
            ("XC", 90), ("L", 50), ("XL", 40), ("X", 10), ("IX", 9), ("V", 5), ("IV", 4), ("I", 1)
        };

        private static int? PairValue(string pair)
        {
            switch (pair)
            {
                case "XC": return 90;
                case "XL": return 40;
                case "IX": return 9;
                case "IV": return 4;
                default: return null;
            }
        }

        private static int? SingleValue(char c)
        {
            switch (c)
            {
                case 'I': return 1;
                case 'V': return 5;
                case 'X': return 10;
                case 'L': return 50;
                default: return null;
            }
        }
    }
}
