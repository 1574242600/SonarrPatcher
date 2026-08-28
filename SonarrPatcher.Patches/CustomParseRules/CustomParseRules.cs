using System;
using System.Reflection;
using HarmonyLib;
using SonarrPatcher.Common;
using SonarrPatcher.Patches;

namespace SonarrPatcher.Patches.CustomParseRules
{
    /// <summary>
    /// Rewrites release titles that match user-defined rules (from the
    /// <c>CUSTOM_PARSE_RULES_FILE</c> environment variable or a default config path)
    /// before Sonarr's built-in parser sees them.
    /// </summary>
    public sealed class CustomParseRules : Patch
    {
        internal static ParseRuleEngine Engine;

        static CustomParseRules()
        {
            Name = "CustomParseRules";
            Log = new Logger(Name);
        }

        public CustomParseRules()
        {
            var configPath = ParseRuleEngine.ResolveConfigPath(Environment.GetEnvironmentVariable("CUSTOM_PARSE_RULES_FILE"));

            if (!System.IO.File.Exists(configPath))
            {
                Log.Info("No rules file at " + configPath + ", patch not applied");
                return;
            }

            Engine = new ParseRuleEngine(configPath);
            Log.Info("Loaded " + Engine.RuleCount + " rule(s) from " + configPath);

            if (!Engine.HasRules)
            {
                Log.Warn("No enabled rules, patch not applied");
            }
        }

        public override bool ShouldPatch()
        {
            return Engine != null && Engine.HasRules;
        }

        protected override void Apply(Harmony harmony)
        {
            var parserType = AccessTools.TypeByName("NzbDrone.Core.Parser.Parser");

            if (parserType == null)
            {
                throw new InvalidOperationException("Parser type not found");
            }

            var parseTitle = AccessTools.DeclaredMethod(parserType, "ParseTitle", new[] { typeof(string) });

            if (parseTitle == null)
            {
                throw new InvalidOperationException("Parser.ParseTitle not found");
            }

            var prefix = typeof(CustomParseRules).GetMethod(nameof(ParseTitlePrefix), BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(parseTitle, prefix: new HarmonyMethod(prefix));

            Log.Info("Patch applied. rules=" + Engine.RuleCount);
        }

        /// <summary>
        /// Rewrites the title when a custom rule matches, then always falls through to
        /// Sonarr's own <c>Parser.ParseTitle</c>, which parses the (possibly rewritten)
        /// title with its full machinery (title/season/episode/subgroup/languages/quality).
        /// </summary>
        private static bool ParseTitlePrefix(ref string title)
        {
            if (Engine != null && Engine.HasRules)
            {
                Engine.TryNormalize(ref title);
            }

            return true;
        }
    }
}
