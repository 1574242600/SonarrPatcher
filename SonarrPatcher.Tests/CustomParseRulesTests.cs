using System;
using System.IO;
using SonarrPatcher.Patches.CustomParseRules;
using Xunit;

namespace SonarrPatcher.Tests
{
    public class CustomParseRulesTests
    {
        // ---- Config loading ----

        [Fact]
        public void LoadRules_MissingFile_ReturnsEmpty()
        {
            var rules = ParseRuleEngine.LoadRules(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json"));
            Assert.Empty(rules);
        }

        [Fact]
        public void LoadRules_EmptyArray_ReturnsEmpty()
        {
            var path = WriteTempJson("[]");
            try
            {
                Assert.Empty(ParseRuleEngine.LoadRules(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LoadRules_ValidJson_ParsesFields()
        {
            var path = WriteTempJson(
                "[{ \"id\": \"rules.a\", \"pattern\": \"^a$\", \"language\": [\"Chinese\", \"English\"], " +
                "\"quality\": { \"resolution\": 1080, \"source\": \"WEBDL\" }, \"useAbsolute\": true }]");

            try
            {
                var rules = ParseRuleEngine.LoadRules(path);
                var rule = Assert.Single(rules);
                Assert.Equal("rules.a", rule.Id);
                Assert.True(rule.Enabled);
                Assert.Equal("^a$", rule.Pattern);
                Assert.Equal(new[] { "Chinese", "English" }, rule.Language);
                Assert.Equal(1080, rule.Quality.Resolution);
                Assert.Equal("WEBDL", rule.Quality.Source);
                Assert.True(rule.UseAbsolute);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LoadRules_InvalidJson_ReturnsEmpty()
        {
            var path = WriteTempJson("{ not valid json ");
            try
            {
                Assert.Empty(ParseRuleEngine.LoadRules(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ExampleConfig_LoadsAndCompilesAllRules()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "custom-parse-rules.example.json");
            Assert.True(File.Exists(path), "example config not copied to test output");

            var rules = ParseRuleEngine.LoadRules(path);
            Assert.Equal(3, rules.Count);
            Assert.Contains(rules, r => r.Id == "rules.example.fansub" && r.Language.Count == 2 && r.Quality.Resolution == 1080);
            Assert.Contains(rules, r => r.Id == "rules.example.roman-season");
            Assert.Contains(rules, r => r.Id == "rules.example.chinese-season");

            Assert.Equal(3, ParseRuleEngine.CompileRules(rules).Count);
        }

        // ---- Rule compilation ----

        [Fact]
        public void CompileRules_SkipsDisabledAndInvalid()
        {
            var rules = new[]
            {
                new ParseRule { Id = "disabled", Enabled = false, Pattern = "^a$" },
                new ParseRule { Id = null, Pattern = "^b$" },
                new ParseRule { Id = "nopattern" },
                new ParseRule { Id = "badregex", Pattern = "([" },
                new ParseRule { Id = "good", Pattern = "^c$" }
            };

            var compiled = ParseRuleEngine.CompileRules(rules);

            var single = Assert.Single(compiled);
            Assert.Equal("good", single.Rule.Id);
            Assert.Matches(single.Regex, "c");
        }

        // ---- Season resolution (Arabic / full-width / Roman / Chinese) ----

        [Theory]
        [InlineData("1", 1)]
        [InlineData("01", 1)]
        [InlineData("12", 12)]
        [InlineData("２３", 23)]
        public void ResolveSeason_Arabic(string input, int expected)
        {
            Assert.Equal(expected, ParseRuleEngine.ResolveSeason(input));
        }

        [Theory]
        [InlineData("I", 1)]
        [InlineData("IV", 4)]
        [InlineData("V", 5)]
        [InlineData("IX", 9)]
        [InlineData("X", 10)]
        [InlineData("XII", 12)]
        [InlineData("XXIII", 23)]
        [InlineData("XL", 40)]
        [InlineData("XC", 90)]
        [InlineData("XCIX", 99)]
        public void ResolveSeason_Roman(string input, int expected)
        {
            Assert.Equal(expected, ParseRuleEngine.ResolveSeason(input));
        }

        [Theory]
        [InlineData("一", 1)]
        [InlineData("三", 3)]
        [InlineData("九", 9)]
        [InlineData("壹", 1)]
        [InlineData("叁", 3)]
        [InlineData("玖", 9)]
        [InlineData("十", 10)]
        [InlineData("十一", 11)]
        [InlineData("二十", 20)]
        [InlineData("二十三", 23)]
        [InlineData("九十九", 99)]
        [InlineData("拾", 10)]
        [InlineData("壹拾", 10)]
        public void ResolveSeason_ChineseNumeral(string input, int expected)
        {
            Assert.Equal(expected, ParseRuleEngine.ResolveSeason(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("零")]
        [InlineData("两")]
        [InlineData("二二")]
        [InlineData("第2季")]
        [InlineData("IIIX")]
        [InlineData("C")]
        [InlineData("MMXX")]
        [InlineData("一百")]
        [InlineData("百")]
        public void ResolveSeason_Unsupported_ReturnsNull(string input)
        {
            Assert.Null(ParseRuleEngine.ResolveSeason(input));
        }

        // ---- Episode resolution (Arabic only) ----

        [Theory]
        [InlineData("1", 1)]
        [InlineData("12", 12)]
        [InlineData("１２", 12)]
        public void ResolveEpisode_Arabic(string input, int expected)
        {
            Assert.Equal(expected, ParseRuleEngine.ResolveEpisode(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData("IV")]
        [InlineData("一")]
        [InlineData("abc")]
        public void ResolveEpisode_Unsupported_ReturnsNull(string input)
        {
            Assert.Null(ParseRuleEngine.ResolveEpisode(input));
        }

        // ---- Title helpers ----

        [Theory]
        [InlineData("abc.mkv", "abc")]
        [InlineData("abc.mp4", "abc")]
        [InlineData("abc", "abc")]
        [InlineData("show.2020.mkv", "show.2020")]
        public void RemoveFileExtension_StripsKnownExtensions(string input, string expected)
        {
            Assert.Equal(expected, ParseRuleEngine.RemoveFileExtension(input));
        }

        [Theory]
        [InlineData("１２３", "123")]
        [InlineData("abc１２", "abc12")]
        [InlineData("４５６", "456")]
        public void NormalizeDigits_ConvertsFullWidth(string input, string expected)
        {
            Assert.Equal(expected, ParseRuleEngine.NormalizeDigits(input));
        }

        // ---- TryNormalize (title rewrite) ----

        [Fact]
        public void TryNormalize_ChineseSeason_RewritesToSxx()
        {
            var engine = CreateEngine(
                "[{ \"id\": \"r1\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>中文组)\\\\](?<title>.*?)[ ._-]第(?<season>[一二三四五六七八九壹贰叁肆伍陆柒捌玖十拾]+)季(?<tokens>.*)$\" }]");

            var title = "[中文组] 某番 第三季";
            Assert.True(engine.TryNormalize(ref title));
            Assert.Equal("[中文组] 某番 S03", title);
        }

        [Fact]
        public void TryNormalize_ChineseTensSeason_RewritesToSxx()
        {
            var engine = CreateEngine(
                "[{ \"id\": \"r1\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>中文组)\\\\](?<title>.*?)[ ._-]第(?<season>[一二三四五六七八九壹贰叁肆伍陆柒捌玖十拾]+)季(?<tokens>.*)$\" }]");

            var title = "[中文组] 某番 第二十三季";
            Assert.True(engine.TryNormalize(ref title));
            Assert.Equal("[中文组] 某番 S23", title);
        }

        [Fact]
        public void TryNormalize_RomanSeasonWithEpisode_RewritesToSxxExx()
        {
            var engine = CreateEngine(
                "[{ \"id\": \"r2\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>罗马组)\\\\](?<title>.*?)[ ._-]Season(?<season>[IVXLCDM]{1,7})[-_. ]E(?<episode>\\\\d{1,3})(?<tokens>.*)$\" }]");

            var title = "[罗马组] 某番 SeasonII E01";
            Assert.True(engine.TryNormalize(ref title));
            Assert.Equal("[罗马组] 某番 S02E01", title);
        }

        [Fact]
        public void TryNormalize_RomanSeasonNoEpisode_RewritesToFullSeason()
        {
            var engine = CreateEngine(
                "[{ \"id\": \"r2\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>罗马组)\\\\](?<title>.*?)[ ._-]Season(?<season>[IVXLCDM]{1,7})(?<tokens>.*)$\" }]");

            var title = "[罗马组] 某番 SeasonII";
            Assert.True(engine.TryNormalize(ref title));
            Assert.Equal("[罗马组] 某番 S02", title);
        }

        [Fact]
        public void TryNormalize_ForcedLanguageQuality_DropsOriginalTail()
        {
            var engine = CreateEngine(
                "[{ \"id\": \"r3\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>示例字幕组)\\\\](?<title>.*?)[ ._-]S(?<season>\\\\d{1,2})E(?<episode>\\\\d{1,3})(?<tokens>.*)$\", " +
                "\"language\": [\"简繁\", \"eng\"], " +
                "\"quality\": { \"resolution\": 1080, \"source\": \"WEB-DL\" } }]");

            var title = "[示例字幕组] 某番 S01E01 720p HDTV";
            Assert.True(engine.TryNormalize(ref title));
            Assert.Equal("[示例字幕组] 某番 S01E01 简繁 eng 1080p WEB-DL", title);
            Assert.DoesNotContain("720p", title);
            Assert.DoesNotContain("HDTV", title);
        }

        [Fact]
        public void TryNormalize_UseAbsolute_ZeroPadsEpisode()
        {
            var engine = CreateEngine(
                "[{ \"id\": \"r4\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>绝对组)\\\\](?<title>.*?)[ ._-](?<episode>\\\\d{1,3})(?<tokens>.*)$\", " +
                "\"useAbsolute\": true }]");

            var title = "[绝对组] 某番 1";
            Assert.True(engine.TryNormalize(ref title));
            Assert.Equal("[绝对组] 某番 01", title);
        }

        [Fact]
        public void TryNormalize_AlreadyStandard_LeavesTitleUntouched()
        {
            var engine = CreateEngine(
                "[{ \"id\": \"r5\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>组)\\\\](?<title>.*?) S(?<season>\\\\d{1,2})E(?<episode>\\\\d{2})$\" }]");

            var title = "[组] 某番 S01E01";
            Assert.False(engine.TryNormalize(ref title));
            Assert.Equal("[组] 某番 S01E01", title);
        }

        [Fact]
        public void TryNormalize_NoMatch_LeavesTitleUntouched()
        {
            var engine = CreateEngine(
                "[{ \"id\": \"r3\", " +
                "\"pattern\": \"^\\\\[(?<subgroup>示例字幕组)\\\\](?<title>.*?)[ ._-]S(?<season>\\\\d{1,2})E(?<episode>\\\\d{1,3})(?<tokens>.*)$\" }]");

            var title = "Futurama S01E01";
            Assert.False(engine.TryNormalize(ref title));
            Assert.Equal("Futurama S01E01", title);
        }

        // ---- RomanNumeral ----

        [Theory]
        [InlineData("I", 1)]
        [InlineData("IV", 4)]
        [InlineData("V", 5)]
        [InlineData("IX", 9)]
        [InlineData("X", 10)]
        [InlineData("XL", 40)]
        [InlineData("XC", 90)]
        [InlineData("XII", 12)]
        [InlineData("XCIX", 99)]
        public void RomanNumeral_Valid(string input, int expected)
        {
            Assert.True(RomanNumeral.TryParse(input, out var value));
            Assert.Equal(expected, value);
        }

        [Theory]
        [InlineData("")]
        [InlineData("IIII")]
        [InlineData("VX")]
        [InlineData("ABC")]
        [InlineData("0")]
        [InlineData("MMMM")]
        [InlineData("C")]
        [InlineData("MCMXC")]
        [InlineData("MMXX")]
        public void RomanNumeral_Invalid(string input)
        {
            Assert.False(RomanNumeral.TryParse(input, out _));
        }

        // ---- ChineseNumeral (1-99, single digits and 十/拾 tens compounds) ----

        [Theory]
        [InlineData("一", 1)]
        [InlineData("五", 5)]
        [InlineData("九", 9)]
        [InlineData("壹", 1)]
        [InlineData("肆", 4)]
        [InlineData("玖", 9)]
        [InlineData("十", 10)]
        [InlineData("拾", 10)]
        [InlineData("十一", 11)]
        [InlineData("十二", 12)]
        [InlineData("二十", 20)]
        [InlineData("二十三", 23)]
        [InlineData("九十九", 99)]
        [InlineData("壹拾", 10)]
        [InlineData("贰拾叁", 23)]
        public void ChineseNumeral_Valid(string input, int expected)
        {
            Assert.True(ChineseNumeral.TryParse(input, out var value));
            Assert.Equal(expected, value);
        }

        [Theory]
        [InlineData("")]
        [InlineData("百")]
        [InlineData("一百")]
        [InlineData("二十十")]
        [InlineData("零")]
        [InlineData("两")]
        [InlineData("二二")]
        [InlineData("第二季")]
        [InlineData("abc")]
        public void ChineseNumeral_OtherChars_NotHandled(string input)
        {
            Assert.False(ChineseNumeral.TryParse(input, out _));
        }

        private static ParseRuleEngine CreateEngine(string json)
        {
            var path = WriteTempJson(json);
            try
            {
                return new ParseRuleEngine(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string WriteTempJson(string json)
        {
            var path = Path.Combine(Path.GetTempPath(), "SonarrPatcher.CPR." + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }
    }
}
