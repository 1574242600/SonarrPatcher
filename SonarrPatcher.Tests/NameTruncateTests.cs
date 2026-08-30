using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Text;
using HarmonyLib;
using SonarrPatcher.Patches.NameTruncate;
using Xunit;

namespace SonarrPatcher.Tests
{
    public class NameTruncateTests
    {
        private const string PatchId = "tv.sonarr.nametruncatepatch";

        // ---- Unit tests for the replacement logic (no Sonarr needed) ----

        [Theory]
        [InlineData("", "30", "")]
        [InlineData("   ", "30", "")]
        public void Whitespace_ReturnsEmpty(string input, string formatter, string expected)
        {
            Assert.Equal(expected, NameTruncate.TruncateReplacement(input, formatter));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("xyz")]
        [InlineData("0")]
        public void NonApplicableFormatter_ReturnsInput(string formatter)
        {
            Assert.Equal("abc", NameTruncate.TruncateReplacement("abc", formatter));
        }

        [Fact]
        public void WithinLimit_ReturnsInputUnchanged()
        {
            Assert.Equal(new string('a', 30), NameTruncate.TruncateReplacement(new string('a', 30), "30"));
        }

        [Fact]
        public void Ascii_TruncatesToCharactersPlusEllipsis()
        {
            Assert.Equal(new string('a', 27) + "{ellipsis}", NameTruncate.TruncateReplacement(new string('a', 40), "30"));
        }

        [Fact]
        public void Cjk_CountsCharactersNotBytes()
        {
            Assert.Equal(new string('东', 27) + "{ellipsis}", NameTruncate.TruncateReplacement(new string('东', 40), "30"));
        }

        [Fact]
        public void Negative_TruncatesFromFront()
        {
            Assert.Equal("{ellipsis}" + new string('东', 27), NameTruncate.TruncateReplacement(new string('东', 40), "-30"));
        }

        [Fact]
        public void ZwjEmoji_NotSplit()
        {
            var family = "👨\u200d👩\u200d👧\u200d👦";
            Assert.Equal(Repeat(family, 7) + "{ellipsis}", NameTruncate.TruncateReplacement(Repeat(family, 12), "10"));
        }

        [Fact]
        public void CombiningMark_NotSplit()
        {
            var eAcute = "e\u0301";
            Assert.Equal(Repeat(eAcute, 7) + "{ellipsis}", NameTruncate.TruncateReplacement(Repeat(eAcute, 12), "10"));
        }

        [Fact]
        public void TrailingSpaces_TrimmedBeforeEllipsis()
        {
            Assert.Equal("abc{ellipsis}", NameTruncate.TruncateReplacement("abc" + new string(' ', 8) + "x", "10"));
        }

        [Fact]
        public void TinyLimit_ReturnsJustEllipsis()
        {
            Assert.Equal("{ellipsis}", NameTruncate.TruncateReplacement("abcd", "2"));
        }

        [Fact]
        public void Disabled_SkipsPatch()
        {
            SetDisabled(true);
            try
            {
                Assert.False(new NameTruncate().ShouldPatch());
            }
            finally
            {
                SetDisabled(false);
            }
        }

        private static void SetDisabled(bool disabled)
        {
            typeof(NameTruncate).GetField("_disabled", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, disabled);
        }

        // ---- Integration tests against the real Sonarr.Core assembly ----

        [SkippableFact]
        public void Patch_ChangesTruncationToCharacterSemantics()
        {
            var (type, method) = EnsureTruncateAvailable();

            // Before the patch: Sonarr's original byte-based truncation keeps only
            // 9 CJK characters out of a ":30" budget (30 bytes / 3 bytes per char).
            Unpatch();
            Assert.Equal(new string('东', 9) + "{ellipsis}", InvokeTruncate(type, method, new string('东', 40), "30"));

            // Apply the patch and verify character-based truncation (27 + ellipsis).
            new NameTruncate().Run();
            Assert.Equal(new string('东', 27) + "{ellipsis}", InvokeTruncate(type, method, new string('东', 40), "30"));
        }

        [SkippableFact]
        public void Patch_Ascii_BehaviorUnchanged()
        {
            var (type, method) = EnsureTruncateAvailable();

            Unpatch();
            var before = InvokeTruncate(type, method, new string('a', 40), "30");

            new NameTruncate().Run();
            var after = InvokeTruncate(type, method, new string('a', 40), "30");

            Assert.Equal(before, after);
            Assert.Equal(new string('a', 27) + "{ellipsis}", after);
        }

        [SkippableFact]
        public void Patch_KeepsZwjSequencesIntact()
        {
            var (type, method) = EnsureTruncateAvailable();

            new NameTruncate().Run();

            var family = "👨\u200d👩\u200d👧\u200d👦";
            var result = InvokeTruncate(type, method, Repeat(family, 12), "10");

            Assert.Equal(Repeat(family, 7) + "{ellipsis}", result);
        }

        [SkippableFact]
        public void Patch_LeavesShortNamesUntouched()
        {
            var (type, method) = EnsureTruncateAvailable();

            new NameTruncate().Run();

            var name = new string('东', 25);
            Assert.Equal(name, InvokeTruncate(type, method, name, "30"));
        }

        private static (Type Type, MethodInfo Method) EnsureTruncateAvailable()
        {
            var baseDir = AppContext.BaseDirectory;
            var corePath = Path.Combine(baseDir, "Sonarr.Core.dll");
            Skip.If(!File.Exists(corePath), "Sonarr.Core.dll not found in " + baseDir + "; skipping Sonarr integration tests");

            LoadIfAbsent(corePath);
            LoadIfAbsent(Path.Combine(baseDir, "Sonarr.Common.dll"));
            LoadIfAbsent(Path.Combine(baseDir, "0Harmony.dll"));

            var type = AccessTools.TypeByName("NzbDrone.Core.Organizer.FileNameBuilder");
            var method = type.GetMethod("Truncate", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string) }, null);
            return (type, method);
        }

        private static string InvokeTruncate(Type type, MethodInfo method, string input, string formatter)
        {
            var instance = FormatterServices.GetUninitializedObject(type);
            return (string)method.Invoke(instance, new object[] { input, formatter });
        }

        private static void Unpatch()
        {
            new Harmony(PatchId).UnpatchAll(PatchId);
        }

        private static string Repeat(string s, int n)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < n; i++)
            {
                builder.Append(s);
            }

            return builder.ToString();
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
