using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using SonarrPatcher.Common;
using SonarrPatcher.Patches;

namespace SonarrPatcher.Patches.NameTruncate
{
    /// <summary>
    /// Replaces <c>NzbDrone.Core.Organizer.FileNameBuilder.Truncate(string, string)</c> so that
    /// the <c>{name:N}</c> format means "N characters" (Unicode text elements / grapheme
    /// clusters) instead of Sonarr's inconsistent mixture of a UTF-16 <c>string.Length</c> gate
    /// and a UTF-8 byte-based cut. This fixes truncation for non-English text (CJK, emoji,
    /// combining characters) which was either silently left untruncated or cut to ~1/3 of the
    /// requested length.
    /// </summary>
    public sealed class NameTruncate : Patch
    {
        /// <summary>Characters reserved for the trailing "{ellipsis}" placeholder.</summary>
        private const int EllipsisLength = 3;

        private static bool _disabled;

        static NameTruncate()
        {
            Name = "NameTruncatePatch";
            _disabled = Environment.GetEnvironmentVariable("DISABLE_NAMETRUNCATE_PATCH") == "1";
        }

        public override bool ShouldPatch()
        {
            return !_disabled;
        }

        protected override void Apply(Harmony harmony)
        {
            var builderType = ReflectionHelper.RequireType(AccessTools.TypeByName("NzbDrone.Core.Organizer.FileNameBuilder"), "FileNameBuilder");
            var truncate = ReflectionHelper.RequireMethod(AccessTools.DeclaredMethod(builderType, "Truncate", new[] { typeof(string), typeof(string) }), "FileNameBuilder.Truncate(string,string)");

            var prefix = typeof(NameTruncate).GetMethod(nameof(TruncatePrefix), BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(truncate, prefix: new HarmonyMethod(prefix));
            Log.Info("Patched FileNameBuilder.Truncate: {name:N} now means N characters");
        }

        /// <summary>
        /// Harmony prefix that fully replaces the original method. Returns false to skip
        /// the original implementation, writing the replacement into <c>__result</c>.
        /// </summary>
        private static bool TruncatePrefix(ref string __result, string input, string formatter)
        {
            __result = TruncateReplacement(input, formatter);
            return false;
        }

        /// <summary>
        /// Character-based truncation replacing Sonarr's <c>Truncate</c>. <c>N</c> in
        /// <c>{name:N}</c> is the maximum number of text elements (grapheme clusters),
        /// <c>-N</c> truncates from the front, and a "..." ellipsis reserves 3 characters.
        /// </summary>
        internal static string TruncateReplacement(string input, string formatter)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            int.TryParse(formatter, out var maxLength);

            if (maxLength == 0)
            {
                return input;
            }

            // Single pass over the grapheme-cluster boundaries: each entry is the char
            // index where a text element starts, so element i spans
            // boundaries[i] .. (i == Length - 1 ? input.Length : boundaries[i + 1]).
            var boundaries = StringInfo.ParseCombiningCharacters(input);
            var limit = Math.Abs(maxLength);

            if (boundaries.Length <= limit)
            {
                return input;
            }

            if (maxLength < 0)
            {
                return "{ellipsis}" + LastTextElements(input, boundaries, limit - EllipsisLength).TrimStart(' ', '.');
            }

            return FirstTextElements(input, boundaries, maxLength - EllipsisLength).TrimEnd(' ', '.') + "{ellipsis}";
        }

        private static string FirstTextElements(string s, int[] boundaries, int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            if (count >= boundaries.Length)
            {
                return s;
            }

            // boundaries[count] is where the (count + 1)-th element starts,
            // so taking everything before it yields exactly `count` elements.
            return s.Substring(0, boundaries[count]);
        }

        private static string LastTextElements(string s, int[] boundaries, int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            if (count >= boundaries.Length)
            {
                return s;
            }

            return s.Substring(boundaries[boundaries.Length - count]);
        }
    }
}
