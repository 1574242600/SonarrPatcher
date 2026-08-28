using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using SonarrPatcher.Patches.CustomParseRules;
using Xunit;

namespace SonarrPatcher.Tests
{
    public class CustomParseRulesIntegrationTests
    {
        private const string PatchId = "tv.sonarr.customparserulespatch";

        [SkippableFact]
        public void FansubRule_ForcesLanguageAndQuality_DropsOriginalTail()
        {
            EnsureCoreLoaded();

            var config = WriteConfig(
                "[{ \"id\": \"rules.fansub\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>示例字幕组)\\\\](?<title>.*?)[ ._-]S(?<season>\\\\d{1,2})E(?<episode>\\\\d{1,3})(?<tokens>.*)$\", " +
                "\"language\": [\"简繁\", \"eng\"], " +
                "\"quality\": { \"resolution\": 1080, \"source\": \"WEB-DL\" } }]");

            try
            {
                Apply(config);

                var parsed = ParseTitle("[示例字幕组] 某番 S01E01 720p HDTV");

                Assert.NotNull(parsed);
                Assert.Equal("某番", Get<string>(parsed, "SeriesTitle"));
                Assert.Equal(1, Get<int>(parsed, "SeasonNumber"));
                Assert.Equal(new[] { 1 }, Get<int[]>(parsed, "EpisodeNumbers"));
                Assert.Equal("示例字幕组", Get<string>(parsed, "ReleaseGroup"));

                var languages = GetLanguageNames(parsed);
                Assert.Contains("Chinese", languages);
                Assert.Contains("English", languages);

                var quality = Get(parsed, "Quality");
                var qualityValue = Get(quality, "Quality");
                Assert.Equal(1080, Get<int>(qualityValue, "Resolution"));
                Assert.Equal("Web", Get(qualityValue, "Source").ToString());
            }
            finally
            {
                Cleanup(config);
            }
        }

        [SkippableFact]
        public void RomanSeasonRule_ParsesSeason()
        {
            EnsureCoreLoaded();

            var config = WriteConfig(
                "[{ \"id\": \"rules.roman\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>罗马组)\\\\](?<title>.*?)[ ._-]Season(?<season>[IVXLCDM]{1,7})(?<tokens>.*)$\" }]");

            try
            {
                Apply(config);

                var parsed = ParseTitle("[罗马组] 某番 SeasonII");

                Assert.NotNull(parsed);
                Assert.Equal("某番", Get<string>(parsed, "SeriesTitle"));
                Assert.Equal(2, Get<int>(parsed, "SeasonNumber"));
                Assert.Equal("罗马组", Get<string>(parsed, "ReleaseGroup"));
                Assert.True(Get<bool>(parsed, "FullSeason"));
            }
            finally
            {
                Cleanup(config);
            }
        }

        [SkippableFact]
        public void ChineseSeasonRule_ParsesSeasonAndAutoDetectsFromPreservedTail()
        {
            EnsureCoreLoaded();

            var config = WriteConfig(
                "[{ \"id\": \"rules.chinese\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>中文组)\\\\](?<title>.*?)[ ._-]第(?<season>[一二三四五六七八九壹贰叁肆伍陆柒捌玖十拾]+)季(?<tokens>.*)$\" }]");

            try
            {
                Apply(config);

                var parsed = ParseTitle("[中文组] 某番 第三季 1080p WEB-DL 简繁");

                Assert.NotNull(parsed);
                Assert.Equal("某番", Get<string>(parsed, "SeriesTitle"));
                Assert.Equal(3, Get<int>(parsed, "SeasonNumber"));
                Assert.Equal("中文组", Get<string>(parsed, "ReleaseGroup"));
                Assert.True(Get<bool>(parsed, "FullSeason"));

                var languages = GetLanguageNames(parsed);
                Assert.Contains("Chinese", languages);

                var quality = Get(parsed, "Quality");
                var qualityValue = Get(quality, "Quality");
                Assert.Equal(1080, Get<int>(qualityValue, "Resolution"));
                Assert.Equal("Web", Get(qualityValue, "Source").ToString());
            }
            finally
            {
                Cleanup(config);
            }
        }

        [SkippableFact]
        public void ChineseTensSeasonRule_ParsesSeason()
        {
            EnsureCoreLoaded();

            var config = WriteConfig(
                "[{ \"id\": \"rules.chinese\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>中文组)\\\\](?<title>.*?)[ ._-]第(?<season>[一二三四五六七八九壹贰叁肆伍陆柒捌玖十拾]+)季(?<tokens>.*)$\" }]");

            try
            {
                Apply(config);

                var parsed = ParseTitle("[中文组] 某番 第二十三季");

                Assert.NotNull(parsed);
                Assert.Equal("某番", Get<string>(parsed, "SeriesTitle"));
                Assert.Equal(23, Get<int>(parsed, "SeasonNumber"));
                Assert.Equal("中文组", Get<string>(parsed, "ReleaseGroup"));
                Assert.True(Get<bool>(parsed, "FullSeason"));
            }
            finally
            {
                Cleanup(config);
            }
        }

        [SkippableFact]
        public void NonMatchingTitle_FallsBackToOriginalParser()
        {
            EnsureCoreLoaded();

            var config = WriteConfig(
                "[{ \"id\": \"rules.fansub\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>示例字幕组)\\\\](?<title>.*?)[ ._-]S(?<season>\\\\d{1,2})E(?<episode>\\\\d{1,3})(?<tokens>.*)$\" }]");

            try
            {
                Apply(config);

                var parsed = ParseTitle("Futurama S01E01");

                Assert.NotNull(parsed);
                Assert.Contains("Futurama", Get<string>(parsed, "SeriesTitle"));
                Assert.Equal(1, Get<int>(parsed, "SeasonNumber"));
            }
            finally
            {
                Cleanup(config);
            }
        }

        // ---- Helpers ----

        private static void Apply(string configPath)
        {
            Unpatch();
            Environment.SetEnvironmentVariable("CUSTOM_PARSE_RULES_FILE", configPath);
            new CustomParseRules().Run();
        }

        private static void Cleanup(string configPath)
        {
            Environment.SetEnvironmentVariable("CUSTOM_PARSE_RULES_FILE", null);
            CustomParseRules.Engine = null;
            Unpatch();

            try
            {
                File.Delete(configPath);
            }
            catch
            {
            }
        }

        private static string WriteConfig(string json)
        {
            var dir = Path.Combine(Path.GetTempPath(), "SonarrPatcher.CPR." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "custom-parse-rules.json");
            File.WriteAllText(path, json);
            return path;
        }

        private static object ParseTitle(string title)
        {
            var parserType = AccessTools.TypeByName("NzbDrone.Core.Parser.Parser");
            var method = parserType.GetMethod("ParseTitle", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            return method.Invoke(null, new object[] { title });
        }

        private static object Get(object instance, string name)
        {
            return instance.GetType().GetProperty(name).GetValue(instance);
        }

        private static T Get<T>(object instance, string name)
        {
            return (T)Get(instance, name);
        }

        private static List<string> GetLanguageNames(object parsed)
        {
            var names = new List<string>();

            foreach (var language in (IList)Get(parsed, "Languages"))
            {
                names.Add(Get<string>(language, "Name"));
            }

            return names;
        }

        private static void Unpatch()
        {
            new Harmony(PatchId).UnpatchAll(PatchId);
        }

        private static void EnsureCoreLoaded()
        {
            var baseDir = AppContext.BaseDirectory;
            var corePath = Path.Combine(baseDir, "Sonarr.Core.dll");
            Skip.If(!File.Exists(corePath), "Sonarr.Core.dll not found in " + baseDir + "; skipping Sonarr integration tests");

            LoadIfAbsent(corePath);
            LoadIfAbsent(Path.Combine(baseDir, "Sonarr.Common.dll"));
            LoadIfAbsent(Path.Combine(baseDir, "NLog.dll"));
            LoadIfAbsent(Path.Combine(baseDir, "0Harmony.dll"));
        }

        private static void LoadIfAbsent(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var name = AssemblyName.GetAssemblyName(path);
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().FullName == name.FullName);
            if (!loaded)
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
        }
    }
}
