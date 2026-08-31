using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SonarrPatcher.Common;
using SonarrPatcher.Patches;

namespace SonarrPatcher.Patches.SkyHook
{
    /// <summary>
    /// Redirects Sonarr's SkyHook (tvdb data provider) requests to a custom
    /// host/language via the <c>SKYHOOK_HOST</c> / <c>SKYHOOK_LANG</c> environment
    /// variables.
    /// </summary>
    public sealed class SkyHook : Patch
    {
        /// <summary>Path template the SkyHook builder is rooted at.</summary>
        private const string TvdbRouteTemplate = "/v1/tvdb/{route}/{language}/";

        private static string _host;
        private static string _lang;

        static SkyHook()
        {
            Name = "SkyHookPatch";
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
            var builderType = ReflectionHelper.RequireType(AccessTools.TypeByName("NzbDrone.Common.Cloud.SonarrCloudRequestBuilder"), "SonarrCloudRequestBuilder");
            var constructor = ReflectionHelper.RequireConstructor(AccessTools.Constructor(builderType), "SonarrCloudRequestBuilder constructor");

            var postfixMethod = typeof(SkyHook).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(constructor, postfix: new HarmonyMethod(postfixMethod));

            Log.Info("Patch applied. host=" + _host + " lang=" + _lang);
        }

        /// <summary>
        /// Swaps the freshly built <c>SkyHookTvdb</c> factory for one rooted at the
        /// custom host. Non-fatal problems are logged and the original factory is kept,
        /// so a missing or changed Sonarr type never breaks startup.
        /// </summary>
        private static void Postfix(object __instance)
        {
            // ShouldPatch() only installs this postfix when a host is set; the guard
            // keeps the postfix a no-op if that contract ever changes.
            if (string.IsNullOrEmpty(_host))
            {
                return;
            }

            try
            {
                if (!TryResolveBuilderTypes(out var builderType, out var factoryInterfaceType))
                {
                    Log.Warn("HttpRequestBuilder types not found, skipping");
                    return;
                }

                var field = FindSkyHookTvdbField(__instance.GetType(), factoryInterfaceType);
                if (field == null)
                {
                    Log.Warn("SkyHookTvdb field not found, skipping");
                    return;
                }

                field.SetValue(__instance, CreateSkyHookTvdbFactory(builderType));
            }
            catch (Exception ex)
            {
                Log.Error("Postfix error: " + ex);
            }
        }

        private static bool TryResolveBuilderTypes(out Type builderType, out Type factoryInterfaceType)
        {
            builderType = AccessTools.TypeByName("NzbDrone.Common.Http.HttpRequestBuilder");
            factoryInterfaceType = AccessTools.TypeByName("NzbDrone.Common.Http.IHttpRequestBuilderFactory");
            return builderType != null && factoryInterfaceType != null;
        }

        private static object CreateSkyHookTvdbFactory(Type builderType)
        {
            var builder = Activator.CreateInstance(builderType, BuildTvdbBaseUrl());

            var setSegment = FindSetSegmentMethod(builderType);
            if (setSegment == null)
            {
                throw new InvalidOperationException("SetSegment(string,string,bool) not found");
            }

            setSegment.Invoke(builder, new object[] { "language", _lang, false });

            var createFactory = ReflectionHelper.RequireMethod(builderType.GetMethod("CreateFactory"), "HttpRequestBuilder.CreateFactory");
            return createFactory.Invoke(builder, null);
        }

        private static MethodInfo FindSetSegmentMethod(Type builderType)
        {
            return builderType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "SetSegment" && m.GetParameters().Length == 3
                    && m.GetParameters()[0].ParameterType == typeof(string)
                    && m.GetParameters()[1].ParameterType == typeof(string));
        }

        private static FieldInfo FindSkyHookTvdbField(Type instanceType, Type factoryInterfaceType)
        {
            return instanceType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.Name.Contains("SkyHookTvdb") && factoryInterfaceType.IsAssignableFrom(f.FieldType));
        }

        /// <summary>Normalises the host to an absolute base URL (http implied when no scheme).</summary>
        private static string BuildTvdbBaseUrl()
        {
            var hasScheme = _host.StartsWith("http://") || _host.StartsWith("https://");
            return (hasScheme ? _host : "http://" + _host) + TvdbRouteTemplate;
        }
    }
}
