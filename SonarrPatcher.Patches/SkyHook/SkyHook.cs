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
        private static string _host;
        private static string _lang;

        static SkyHook()
        {
            Name = "SkyHookPatch";
            Log = new Logger(Name);
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

            var postfixMethod = typeof(SkyHook).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(constructor, postfix: new HarmonyMethod(postfixMethod));

            Log.Info("Patch applied. host=" + _host + " lang=" + _lang);
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
                    Log.Warn("HttpRequestBuilder types not found, skipping");
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
                    Log.Warn("SkyHookTvdb field not found, skipping");
                    return;
                }

                field.SetValue(__instance, factory);
            }
            catch (Exception ex)
            {
                Log.Error("Postfix error: " + ex);
            }
        }
    }
}
