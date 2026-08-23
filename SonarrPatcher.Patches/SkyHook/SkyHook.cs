using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SonarrPatcher.Common;

namespace SonarrPatcher.Patches
{
    /// <summary>
    /// Public entry point used by the StartupHook and tests. The actual patch is
    /// the internal <see cref="SkyHookPatch"/> deriving from the shared Patch base.
    /// </summary>
    public static class SkyHook
    {
        public static void Initialize()
        {
            SkyHookPatch.Initialize(standalone: true);
        }

        public static void InitializeForLoader()
        {
            SkyHookPatch.Initialize(standalone: false);
        }
    }

    internal class SkyHookPatch : Patch
    {
        private static string _host;
        private static string _lang;

        public SkyHookPatch()
            : base("SkyHookPatch")
        {
        }

        /// <summary>
        /// Standalone mode bootstraps its own dependencies (0Harmony,
        /// Sonarr.Common) from the application base directory; loader mode skips
        /// that because the Loader has already ensured they are loaded.
        /// </summary>
        public static void Initialize(bool standalone)
        {
            ConfigureFromEnv();

            if (string.IsNullOrEmpty(_host))
            {
                return;
            }

            try
            {
                if (standalone)
                {
                    SonarrDependencyLoader.EnsureLoaded("0Harmony.dll", "Sonarr.Common.dll");
                }

                new SkyHookPatch().Run(new Harmony("tv.sonarr.skyhookpatch"));
                new Logger("SkyHookPatch").Info("Patch applied. host=" + _host + " lang=" + _lang + (standalone ? "" : " (loader)"));
            }
            catch (Exception ex)
            {
                new Logger("SkyHookPatch").Error("Failed to apply patch: " + ex);
            }
        }

        private static void ConfigureFromEnv()
        {
            _host = Environment.GetEnvironmentVariable("SKYHOOK_HOST");
            _lang = Environment.GetEnvironmentVariable("SKYHOOK_LANG");

            if (string.IsNullOrEmpty(_lang))
            {
                _lang = "eng";
            }
        }

        public override bool ShouldPatch()
        {
            return !string.IsNullOrEmpty(_host);
        }

        protected override void Apply(Harmony harmony)
        {
            var builderType = AccessTools.TypeByName("NzbDrone.Common.Cloud.SonarrCloudRequestBuilder");
            if (builderType == null)
            {
                throw new InvalidOperationException("SonarrCloudRequestBuilder type not found");
            }

            var constructor = AccessTools.Constructor(builderType);
            if (constructor == null)
            {
                throw new InvalidOperationException("SonarrCloudRequestBuilder constructor not found");
            }

            var postfixMethod = typeof(SkyHookPatch).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(constructor, postfix: new HarmonyMethod(postfixMethod));
        }

        private static void Postfix(object __instance)
        {
            if (string.IsNullOrEmpty(_host))
            {
                return;
            }

            try
            {
                var type = __instance.GetType();
                var builderHelperType = AccessTools.TypeByName("NzbDrone.Common.Http.HttpRequestBuilder");
                var factoryInterfaceType = AccessTools.TypeByName("NzbDrone.Common.Http.IHttpRequestBuilderFactory");

                if (builderHelperType == null || factoryInterfaceType == null)
                {
                    new Logger("SkyHookPatch").Warn("HttpRequestBuilder types not found, skipping");
                    return;
                }

                var url = (_host.StartsWith("http://") || _host.StartsWith("https://") ? _host : "http://" + _host) + "/v1/tvdb/{route}/{language}/";

                var builder = Activator.CreateInstance(builderHelperType, url);
                var setSegment = builderHelperType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "SetSegment" && m.GetParameters().Length == 3
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType == typeof(string));

                if (setSegment == null)
                {
                    throw new InvalidOperationException("SetSegment(string,string,bool) not found");
                }

                setSegment.Invoke(builder, new object[] { "language", _lang, false });

                var createFactory = builderHelperType.GetMethod("CreateFactory");
                var factory = createFactory.Invoke(builder, null);

                var field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(f => f.Name.Contains("SkyHookTvdb") && factoryInterfaceType.IsAssignableFrom(f.FieldType));

                if (field == null)
                {
                    new Logger("SkyHookPatch").Warn("SkyHookTvdb field not found, skipping");
                    return;
                }

                field.SetValue(__instance, factory);
            }
            catch (Exception ex)
            {
                new Logger("SkyHookPatch").Error("Postfix error: " + ex);
            }
        }
    }
}
