using System;
using System.Reflection;
using HarmonyLib;
using SonarrPatcher.Common;

namespace SonarrPatcher.Patches
{
    /// <summary>
    /// Public entry point used by the StartupHook and tests. The actual patch is the
    /// internal <see cref="CustomParseRulesPatch"/> deriving from the shared Patch base.
    /// </summary>
    public static class CustomParseRules
    {
        public static void Initialize()
        {
            CustomParseRulesPatch.Initialize(standalone: true);
        }

        public static void InitializeForLoader()
        {
            CustomParseRulesPatch.Initialize(standalone: false);
        }
    }

    internal class CustomParseRulesPatch : Patch
    {
        internal static ParseRuleEngine Engine;

        public CustomParseRulesPatch()
            : base("CustomParseRules")
        {
        }

        public override string PatchId => "tv.sonarr.customparserulespatch";

        /// <summary>
        /// Standalone mode bootstraps its own dependencies (0Harmony, Sonarr.Common)
        /// from the application base directory; loader mode skips that because the
        /// Loader has already ensured they are loaded.
        /// </summary>
        public static void Initialize(bool standalone)
        {
            var configPath = ParseRuleEngine.ResolveConfigPath(Environment.GetEnvironmentVariable("CUSTOM_PARSE_RULES_FILE"));

            if (!System.IO.File.Exists(configPath))
            {
                new Logger("CustomParseRules").Info("No rules file at " + configPath + ", patch not applied");
                return;
            }

            try
            {
                if (standalone)
                {
                    SonarrDependencyLoader.EnsureLoaded("0Harmony.dll", "Sonarr.Common.dll");
                }

                Engine = new ParseRuleEngine(configPath);
                new Logger("CustomParseRules").Info("Loaded " + Engine.RuleCount + " rule(s) from " + configPath);

                if (!Engine.HasRules)
                {
                    new Logger("CustomParseRules").Warn("No enabled rules, patch not applied");
                    return;
                }

                new CustomParseRulesPatch().Run();
                new Logger("CustomParseRules").Info("Patch applied. rules=" + Engine.RuleCount);
            }
            catch (Exception ex)
            {
                new Logger("CustomParseRules").Error("Failed to apply patch: " + ex);
            }
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

            var prefix = typeof(CustomParseRulesPatch).GetMethod(nameof(ParseTitlePrefix), BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(parseTitle, prefix: new HarmonyMethod(prefix));
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
