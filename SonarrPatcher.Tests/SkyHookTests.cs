using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using SonarrPatcher.Patches.SkyHook;
using Xunit;

namespace SonarrPatcher.Tests
{
    public class SkyHookTests
    {
        [SkippableFact]
        public void NoEnv_KeepsOriginalSkyHookUrl()
        {
            EnsureSonarrLoaded();
            Environment.SetEnvironmentVariable("SKYHOOK_HOST", null);
            Environment.SetEnvironmentVariable("SKYHOOK_LANG", null);

            new SkyHook().Run();

            var url = GetBaseUrl();
            Assert.StartsWith("https://skyhook.sonarr.tv/", url);
        }

        [SkippableFact]
        public void HostAndLang_PatchesBaseUrlAndLanguage()
        {
            EnsureSonarrLoaded();
            Environment.SetEnvironmentVariable("SKYHOOK_HOST", "mysky.example.org");
            Environment.SetEnvironmentVariable("SKYHOOK_LANG", "zh-cn");

            new SkyHook().Run();

            var (baseUrl, lang) = GetBaseUrlAndLang();
            Assert.Equal("http://mysky.example.org/v1/tvdb/{route}/{language}/", baseUrl);
            Assert.Equal("zh-cn", lang);
        }

        [SkippableFact]
        public void HostWithProtocol_KeepsExplicitProtocol()
        {
            EnsureSonarrLoaded();
            Environment.SetEnvironmentVariable("SKYHOOK_HOST", "http://mysky.example.org");
            Environment.SetEnvironmentVariable("SKYHOOK_LANG", null);

            new SkyHook().Run();

            var baseUrl = GetBaseUrl();
            Assert.StartsWith("http://mysky.example.org/", baseUrl);
        }

        [SkippableFact]
        public void FullRequestBuild_UsesPatchedHostAndLang()
        {
            EnsureSonarrLoaded();
            Environment.SetEnvironmentVariable("SKYHOOK_HOST", "mysky.example.org");
            Environment.SetEnvironmentVariable("SKYHOOK_LANG", "zh-cn");

            new SkyHook().Run();

            var t = AccessTools.TypeByName("NzbDrone.Common.Cloud.SonarrCloudRequestBuilder");
            var inst = Activator.CreateInstance(t);
            var factory = inst.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                .First(f => f.Name.Contains("SkyHookTvdb"))
                .GetValue(inst);

            var reqBuilder = factory.GetType().GetMethod("Create").Invoke(factory, null);
            reqBuilder.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "SetSegment" && m.GetParameters().Length == 3)
                .Invoke(reqBuilder, new object[] { "route", "shows", false });
            reqBuilder.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "Resource")
                .Invoke(reqBuilder, new object[] { "123" });
            var request = reqBuilder.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "Build")
                .Invoke(reqBuilder, null);

            var fullUrl = request.GetType().GetProperty("Url").GetValue(request).ToString();
            Assert.StartsWith("http://mysky.example.org/", fullUrl);
            Assert.Contains("/zh-cn/", fullUrl);
        }

        private static void EnsureSonarrLoaded()
        {
            var baseDir = AppContext.BaseDirectory;
            var commonPath = Path.Combine(baseDir, "Sonarr.Common.dll");
            Skip.If(!File.Exists(commonPath), "Sonarr.Common.dll not found in " + baseDir + "; skipping Sonarr integration tests");

            LoadIfAbsent(commonPath);
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

        private static string GetBaseUrl()
        {
            var t = AccessTools.TypeByName("NzbDrone.Common.Cloud.SonarrCloudRequestBuilder");
            var inst = Activator.CreateInstance(t);
            var factory = inst.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                .First(f => f.Name.Contains("SkyHookTvdb"))
                .GetValue(inst);
            var rb = factory.GetType().GetField("_rootBuilder", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(factory);
            return rb.GetType().GetProperty("BaseUrl").GetValue(rb).ToString();
        }

        private static (string BaseUrl, string Lang) GetBaseUrlAndLang()
        {
            var t = AccessTools.TypeByName("NzbDrone.Common.Cloud.SonarrCloudRequestBuilder");
            var inst = Activator.CreateInstance(t);
            var factory = inst.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                .First(f => f.Name.Contains("SkyHookTvdb"))
                .GetValue(inst);
            var rb = factory.GetType().GetField("_rootBuilder", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(factory);
            var baseUrl = rb.GetType().GetProperty("BaseUrl").GetValue(rb).ToString();
            var segs = rb.GetType().GetProperty("Segments").GetValue(rb) as IDictionary;
            var lang = segs?["{language}"]?.ToString();
            return (baseUrl, lang);
        }
    }
}
