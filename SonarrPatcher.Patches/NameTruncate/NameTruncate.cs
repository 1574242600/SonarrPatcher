using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
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
        private static bool _disabled;

        static NameTruncate()
        {
            Name = "NameTruncatePatch";
            Log = new Logger(Name);
            _disabled = Environment.GetEnvironmentVariable("DISABLE_NAMETRUNCATE_PATCH") == "1";
        }

        public override bool ShouldPatch()
        {
            return !_disabled;
        }

        protected override void Apply(Harmony harmony)
        {
            // The StartupHook runs before Sonarr's main entry point, so Sonarr.Core
            // may not be loaded yet; load it explicitly so the type can be resolved.
            SonarrDependencyLoader.EnsureLoaded("Sonarr.Core.dll");

            var builderType = AccessTools.TypeByName("NzbDrone.Core.Organizer.FileNameBuilder");
            if (builderType == null)
            {
                throw new InvalidOperationException("FileNameBuilder type not found");
            }

            var truncate = AccessTools.DeclaredMethod(builderType, "Truncate", new[] { typeof(string), typeof(string) });
            if (truncate == null)
            {
                throw new InvalidOperationException("FileNameBuilder.Truncate(string,string) not found");
            }

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

            if (TextElementCount(input) <= Math.Abs(maxLength))
            {
                return input;
            }

            if (maxLength < 0)
            {
                return "{ellipsis}" + LastTextElements(input, Math.Abs(maxLength) - 3).TrimStart(' ', '.');
            }

            return FirstTextElements(input, maxLength - 3).TrimEnd(' ', '.') + "{ellipsis}";
        }

        private static int TextElementCount(string s)
        {
            var enumerator = StringInfo.GetTextElementEnumerator(s);
            var count = 0;
            while (enumerator.MoveNext())
            {
                count++;
            }

            return count;
        }

        private static string FirstTextElements(string s, int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var enumerator = StringInfo.GetTextElementEnumerator(s);
            while (enumerator.MoveNext() && count-- > 0)
            {
                builder.Append(enumerator.GetTextElement());
            }

            return builder.ToString();
        }

        private static string LastTextElements(string s, int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            var elements = new List<string>();
            var enumerator = StringInfo.GetTextElementEnumerator(s);
            while (enumerator.MoveNext())
            {
                elements.Add(enumerator.GetTextElement());
            }

            var start = elements.Count - count;
            if (start < 0)
            {
                start = 0;
            }

            var builder = new StringBuilder();
            for (var i = start; i < elements.Count; i++)
            {
                builder.Append(elements[i]);
            }

            return builder.ToString();
        }
    }
}
