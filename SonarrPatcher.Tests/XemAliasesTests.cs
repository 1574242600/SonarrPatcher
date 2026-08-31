using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using HarmonyLib;
using SonarrPatcher.Patches.XemAliases;
using Xunit;

namespace SonarrPatcher.Tests
{
    public class XemAliasesTests
    {
        private const string PatchId = "tv.sonarr.xemaliasespatch";

        // ---- Unit tests for the IsEnglish replacement (no Sonarr needed) ----

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("無職転生")]
        [InlineData("境界線上のホライゾン II")]
        public void NonAscii_WithinLimit_Allowed(string title)
        {
            Assert.True(XemAliases.IsEnglishReplacement(title));
        }

        [Fact]
        public void Exactly255_Allowed()
        {
            Assert.True(XemAliases.IsEnglishReplacement(new string('a', 255)));
        }

        [Fact]
        public void Over255_Rejected()
        {
            Assert.False(XemAliases.IsEnglishReplacement(new string('a', 256)));
        }

        // ---- Integration tests against the real Sonarr assemblies ----

        [SkippableFact]
        public void IsEnglish_Patch_AllowsNonAsciiAndCapsLength()
        {
            var (type, method) = EnsureIsEnglishAvailable();

            Unpatch();
            SetAllNamesUrl(null);
            SetDisableEnglishFilter(false);

            // Before the patch: ASCII-only (CJK rejected, long ASCII accepted).
            Assert.False(InvokeIsEnglish(type, method, "無職転生"));
            Assert.True(InvokeIsEnglish(type, method, new string('a', 256)));

            // Apply the patch and verify CJK is allowed while the 255 length cap remains.
            new XemAliases().Run();
            Assert.True(InvokeIsEnglish(type, method, "無職転生"));
            Assert.True(InvokeIsEnglish(type, method, "境界線上のホライゾン II"));
            Assert.False(InvokeIsEnglish(type, method, new string('a', 256)));
        }

        [SkippableFact]
        public void AllNamesRequest_UsesCustomUrlWithQueryParams()
        {
            EnsureCommonLoaded();

            var url = XemAliases.BuildAllNamesRequestUrl("http://xem.example.org/map/allNames");

            Assert.StartsWith("http://xem.example.org/map/allNames", url);
            Assert.Contains("origin=tvdb", url);
            Assert.Contains("seasonNumbers", url);
        }

        [SkippableFact]
        public void AllNamesRequest_UrlWithoutParams_KeepsCustomHost()
        {
            EnsureCommonLoaded();

            var url = XemAliases.BuildAllNamesRequestUrl("https://mirror.example.net/map/allNames");

            Assert.StartsWith("https://mirror.example.net/map/allNames", url);
            Assert.Contains("origin=tvdb", url);
            Assert.Contains("seasonNumbers", url);
        }

        [SkippableFact]
        public void AllNamesRedirect_Patch_InstallsPrefixOnGetSceneTvdbNames()
        {
            EnsureCoreLoaded();

            Unpatch();
            SetAllNamesUrl("http://xem.example.org/map/allNames");
            SetDisableEnglishFilter(false);

            try
            {
                new XemAliases().Run();

                var type = AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Xem.XemProxy");
                var method = AccessTools.DeclaredMethod(type, "GetSceneTvdbNames");
                var info = Harmony.GetPatchInfo(method);

                Assert.NotNull(info);
                Assert.Contains(info.Prefixes, p => p.PatchMethod.DeclaringType == typeof(XemAliases));
            }
            finally
            {
                SetAllNamesUrl(null);
                Unpatch();
            }
        }

        [SkippableFact]
        public void NoUrl_GetSceneTvdbNames_Unpatched()
        {
            EnsureCoreLoaded();

            Unpatch();
            SetAllNamesUrl(null);
            SetDisableEnglishFilter(true);

            try
            {
                new XemAliases().Run();

                var type = AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Xem.XemProxy");
                var method = AccessTools.DeclaredMethod(type, "GetSceneTvdbNames");
                var info = Harmony.GetPatchInfo(method);

                Assert.True(info == null || info.Prefixes.Count == 0, "GetSceneTvdbNames should not be patched without XEM_ALLNAMES_URL");
            }
            finally
            {
                SetDisableEnglishFilter(false);
                Unpatch();
            }
        }

        private static (Type Type, MethodInfo Method) EnsureIsEnglishAvailable()
        {
            var baseDir = AppContext.BaseDirectory;
            var corePath = Path.Combine(baseDir, "Sonarr.Core.dll");
            Skip.If(!File.Exists(corePath), "Sonarr.Core.dll not found in " + baseDir + "; skipping Sonarr integration tests");

            LoadIfAbsent(corePath);
            LoadIfAbsent(Path.Combine(baseDir, "Sonarr.Common.dll"));
            LoadIfAbsent(Path.Combine(baseDir, "0Harmony.dll"));

            var type = AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Scene.SceneMappingService");
            var method = type.GetMethod("IsEnglish", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            return (type, method);
        }

        private static bool InvokeIsEnglish(Type type, MethodInfo method, string title)
        {
            var instance = FormatterServices.GetUninitializedObject(type);
            return (bool)method.Invoke(instance, new object[] { title });
        }

        private static void EnsureCommonLoaded()
        {
            var baseDir = AppContext.BaseDirectory;
            var commonPath = Path.Combine(baseDir, "Sonarr.Common.dll");
            Skip.If(!File.Exists(commonPath), "Sonarr.Common.dll not found in " + baseDir + "; skipping Sonarr integration tests");

            LoadIfAbsent(commonPath);
            LoadIfAbsent(Path.Combine(baseDir, "0Harmony.dll"));
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

        private static void SetAllNamesUrl(string url)
        {
            typeof(XemAliases).GetField("_allNamesUrl", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, url);
        }

        // The patch reads its config in the static constructor, so tests reset the
        // captured static state (instead of flipping the env var at run time).
        private static void SetDisableEnglishFilter(bool disabled)
        {
            typeof(XemAliases).GetField("_disableEnglishFilter", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, disabled);
        }

        private static void Unpatch()
        {
            new Harmony(PatchId).UnpatchAll(PatchId);
        }
    }
}
