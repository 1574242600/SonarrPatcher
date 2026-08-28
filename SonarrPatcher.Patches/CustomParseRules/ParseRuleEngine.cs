using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using SonarrPatcher.Common;

namespace SonarrPatcher.Patches.CustomParseRules
{
    /// <summary>
    /// A parsed rule plus its compiled regex. <see cref="Rule"/> carries the user
    /// config (id/enabled/language/quality/useAbsolute) and <see cref="Regex"/> is the
    /// compiled pattern.
    /// </summary>
    internal sealed class CompiledRule
    {
        public CompiledRule(ParseRule rule, Regex regex)
        {
            Rule = rule;
            Regex = regex;
        }

        public ParseRule Rule { get; }
        public Regex Regex { get; }
    }

    /// <summary>
    /// Rewrites release titles that match a custom rule into a standard form Sonarr's
    /// built-in parser understands (e.g. <c>[subgroup] series S01E01 tokens</c>), then
    /// lets the original <c>Parser.ParseTitle</c> do the actual parsing. No reflection,
    /// no direct ParsedEpisodeInfo construction.
    /// <para>
    /// Season numbers accept Arabic digits (incl. full-width), Roman numerals (1-99)
    /// and Chinese numerals (1-99, single digits and 十/拾 tens compounds); episode
    /// numbers accept Arabic digits (incl. full-width) only. When a rule specifies
    /// <c>language</c> and/or <c>quality</c>, the original title tail is dropped and
    /// replaced by synthetic tokens the built-in LanguageParser/QualityParser recognise,
    /// forcing those values.
    /// </para>
    /// </summary>
    internal sealed class ParseRuleEngine
    {
        private static readonly Regex ExtensionRegex = new Regex(@"\.[a-z0-9]{2,4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly List<CompiledRule> _rules;

        public ParseRuleEngine(string configPath)
        {
            _rules = CompileRules(LoadRules(configPath));
        }

        public int RuleCount => _rules.Count;

        public bool HasRules => _rules.Count > 0;

        /// <summary>
        /// Resolves the config path: the <c>CUSTOM_PARSE_RULES_FILE</c> environment
        /// variable when set, otherwise <c>&lt;patch dir&gt;/config/custom-parse-rules.json</c>.
        /// </summary>
        public static string ResolveConfigPath(string envOverride)
        {
            if (!string.IsNullOrWhiteSpace(envOverride))
            {
                return envOverride;
            }

            var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            return Path.Combine(dir, "config", "custom-parse-rules.json");
        }

        public static List<ParseRule> LoadRules(string configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                return new List<ParseRule>();
            }

            try
            {
                var json = File.ReadAllText(configPath);
                var rules = JsonConvert.DeserializeObject<List<ParseRule>>(json);
                return rules ?? new List<ParseRule>();
            }
            catch (Exception ex)
            {
                new Logger("CustomParseRules").Error("Failed to read " + configPath + ": " + ex);
                return new List<ParseRule>();
            }
        }

        public static List<CompiledRule> CompileRules(IEnumerable<ParseRule> rules)
        {
            var compiled = new List<CompiledRule>();

            foreach (var rule in rules ?? Enumerable.Empty<ParseRule>())
            {
                if (rule == null || !rule.Enabled)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Pattern))
                {
                    new Logger("CustomParseRules").Warn("Skipping rule without id or pattern");
                    continue;
                }

                try
                {
                    compiled.Add(new CompiledRule(rule, new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase)));
                }
                catch (ArgumentException ex)
                {
                    new Logger("CustomParseRules").Warn("Rule '" + rule.Id + "' has an invalid pattern: " + ex.Message);
                }
            }

            return compiled;
        }

        /// <summary>
        /// When <paramref name="title"/> matches a rule, rewrites it in place to a
        /// standard form. Returns true when the title was modified; false when no rule
        /// applied (the original title is left untouched).
        /// </summary>
        public bool TryNormalize(ref string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            foreach (var compiled in _rules)
            {
                var match = compiled.Regex.Match(title);

                if (!match.Success)
                {
                    continue;
                }

                try
                {
                    var normalized = BuildNormalized(compiled, match, title);

                    if (normalized != null && normalized != title)
                    {
                        title = normalized;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    new Logger("CustomParseRules").Error("Rule '" + compiled.Rule.Id + "' failed: " + ex);
                }
            }

            return false;
        }

        private static string BuildNormalized(CompiledRule compiled, Match match, string title)
        {
            var rule = compiled.Rule;

            if (!match.Groups["title"].Success)
            {
                return null;
            }

            var seriesTitle = match.Groups["title"].Value.Replace('.', ' ').Replace('_', ' ').Trim();

            if (seriesTitle.Length == 0)
            {
                return null;
            }

            var season = ResolveSeason(match.Groups["season"].Success ? match.Groups["season"].Value : null);
            var episodeNumbers = new List<int>();

            if (match.Groups["episode"].Success)
            {
                foreach (Capture capture in match.Groups["episode"].Captures)
                {
                    var episode = ResolveEpisode(capture.Value);

                    if (!episode.HasValue)
                    {
                        return null;
                    }

                    episodeNumbers.Add(episode.Value);
                }
            }

            var builder = new StringBuilder();

            if (match.Groups["subgroup"].Success)
            {
                builder.Append('[').Append(match.Groups["subgroup"].Value).Append("] ");
            }

            builder.Append(seriesTitle);

            if (episodeNumbers.Count > 0)
            {
                if (rule.UseAbsolute)
                {
                    builder.Append(' ').Append(episodeNumbers[0].ToString("D2"));

                    for (var i = 1; i < episodeNumbers.Count; i++)
                    {
                        builder.Append('-').Append(episodeNumbers[i].ToString("D2"));
                    }
                }
                else
                {
                    builder.Append(" S").Append((season ?? 1).ToString("D2"));
                    builder.Append('E').Append(episodeNumbers[0].ToString("D2"));

                    for (var i = 1; i < episodeNumbers.Count; i++)
                    {
                        builder.Append("-E").Append(episodeNumbers[i].ToString("D2"));
                    }
                }
            }
            else if (season.HasValue)
            {
                builder.Append(" S").Append(season.Value.ToString("D2"));
            }

            var tail = (rule.Language != null && rule.Language.Count > 0) || rule.Quality != null
                ? BuildForcedTail(rule)
                : ComputeReleaseTokens(match, RemoveFileExtension(title));

            if (!string.IsNullOrWhiteSpace(tail))
            {
                builder.Append(' ').Append(tail.Trim());
            }

            return builder.ToString();
        }

        /// <summary>
        /// Builds synthetic language/quality tokens for a forced rule. The original
        /// title tail is discarded in this case.
        /// </summary>
        private static string BuildForcedTail(ParseRule rule)
        {
            var parts = new List<string>();

            if (rule.Language != null)
            {
                foreach (var token in rule.Language)
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        parts.Add(token.Trim());
                    }
                }
            }

            if (rule.Quality != null && rule.Quality.Resolution > 0)
            {
                var resolution = rule.Quality.Resolution.ToString() + "p";
                var source = rule.Quality.Source?.Trim();
                parts.Add(string.IsNullOrEmpty(source) ? resolution : resolution + " " + source);
            }

            return string.Join(" ", parts);
        }

        /// <summary>
        /// Returns the portion of the release title after the last matched
        /// season/episode token (mirrors Sonarr's ReleaseTokens concept, used by the
        /// LanguageParser). Empty when the match consumes the whole title.
        /// </summary>
        private static string ComputeReleaseTokens(Match match, string releaseTitle)
        {
            if (match.Groups["tokens"].Success)
            {
                return match.Groups["tokens"].Value;
            }

            var lastIndex = match.Groups["title"].Index + match.Groups["title"].Length;

            foreach (var groupName in new[] { "season", "episode", "absoluteepisode" })
            {
                var group = match.Groups[groupName];

                foreach (Capture capture in group.Captures)
                {
                    lastIndex = Math.Max(lastIndex, capture.Index + capture.Length);
                }
            }

            if (lastIndex >= releaseTitle.Length)
            {
                return string.Empty;
            }

            return releaseTitle.Substring(lastIndex);
        }

        internal static string RemoveFileExtension(string title)
        {
            return ExtensionRegex.Replace(title, string.Empty);
        }

        internal static int? ResolveSeason(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (int.TryParse(NormalizeDigits(value), out var arabic))
            {
                return arabic;
            }

            if (RomanNumeral.TryParse(value, out var roman))
            {
                return roman;
            }

            if (ChineseNumeral.TryParse(value, out var chinese))
            {
                return chinese;
            }

            return null;
        }

        internal static int? ResolveEpisode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (int.TryParse(NormalizeDigits(value), out var arabic))
            {
                return arabic;
            }

            return null;
        }

        /// <summary>
        /// Normalises full-width / Unicode digits to ASCII digits (same behaviour as
        /// Sonarr's Parser.ConvertToNumerals).
        /// </summary>
        internal static string NormalizeDigits(string input)
        {
            var result = new StringBuilder(input.Length);

            foreach (var c in input)
            {
                if (char.IsNumber(c))
                {
                    result.Append(char.GetNumericValue(c));
                }
                else
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }
    }
}
