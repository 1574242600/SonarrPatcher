using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SonarrPatcher.Common;
using SonarrPatcher.Patches;

namespace SonarrPatcher.Patches.XemAliases
{
    /// <summary>
    /// Patches Xem alias handling: optionally redirects the allNames request to a
    /// custom URL (via the <c>XEM_ALLNAMES_URL</c> environment variable) and relaxes
    /// the <c>IsEnglish</c> alias filter from ASCII-only to a 255-character ceiling.
    /// </summary>
    public sealed class XemAliases : Patch
    {
        private static string _allNamesUrl;

        static XemAliases()
        {
            Name = "XemAliasesPatch";
            Log = new Logger(Name);
            _allNamesUrl = Environment.GetEnvironmentVariable("XEM_ALLNAMES_URL");
        }

        public override bool ShouldPatch()
        {
            var disableEnglishFilter = Environment.GetEnvironmentVariable("DISABLE_NONENGLISH_ALIASES_PATCH") == "1";

            return !string.IsNullOrEmpty(_allNamesUrl) || !disableEnglishFilter;
        }

        protected override void Apply(Harmony harmony)
        {
            if (!string.IsNullOrEmpty(_allNamesUrl))
            {
                var xemProxyType = AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Xem.XemProxy");
                if (xemProxyType == null)
                {
                    throw new InvalidOperationException("XemProxy type not found");
                }

                var getSceneTvdbNames = AccessTools.DeclaredMethod(xemProxyType, "GetSceneTvdbNames");
                if (getSceneTvdbNames == null)
                {
                    throw new InvalidOperationException("XemProxy.GetSceneTvdbNames not found");
                }

                var prefix = typeof(XemAliases).GetMethod(nameof(GetSceneTvdbNamesPrefix), BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(getSceneTvdbNames, prefix: new HarmonyMethod(prefix));
                Log.Info("Patched XemProxy.GetSceneTvdbNames to redirect allNames to " + _allNamesUrl);
            }

            if (Environment.GetEnvironmentVariable("DISABLE_NONENGLISH_ALIASES_PATCH") != "1")
            {
                var sceneMappingServiceType = AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Scene.SceneMappingService");
                if (sceneMappingServiceType == null)
                {
                    throw new InvalidOperationException("SceneMappingService type not found");
                }

                var isEnglish = AccessTools.DeclaredMethod(sceneMappingServiceType, "IsEnglish", new[] { typeof(string) });
                if (isEnglish == null)
                {
                    throw new InvalidOperationException("SceneMappingService.IsEnglish not found");
                }

                var prefix = typeof(XemAliases).GetMethod(nameof(IsEnglishPrefix), BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(isEnglish, prefix: new HarmonyMethod(prefix));
                Log.Info("Patched SceneMappingService.IsEnglish: alias filter is now character count (<=255) instead of ASCII-only");
            }

            Log.Info("Patch applied. allNamesUrl=" + (_allNamesUrl ?? "(unset)"));
        }

        /// <summary>
        /// Replaces <c>IsEnglish(string)</c>: the original returned true only when every
        /// character was &lt;= 255 (ASCII). The replacement drops the encoding restriction
        /// but keeps a 255 character-count ceiling, so CJK / accented aliases are allowed
        /// while absurdly long strings are still rejected.
        /// </summary>
        private static bool IsEnglishPrefix(ref bool __result, string title)
        {
            __result = IsEnglishReplacement(title);
            return false;
        }

        internal static bool IsEnglishReplacement(string title)
        {
            return title.Length <= 255;
        }

        /// <summary>
        /// Fully replaces <c>XemProxy.GetSceneTvdbNames()</c> so the allNames request is
        /// issued against <see cref="_allNamesUrl"/> instead of the hardcoded thexem.info
        /// root, leaving /havemap and /all untouched. On any failure the prefix returns
        /// true so the original implementation runs against thexem.info as a fallback.
        /// </summary>
        private static bool GetSceneTvdbNamesPrefix(object __instance, ref object __result)
        {
            try
            {
                var httpClient = GetInstanceField(__instance, "_httpClient");
                var request = BuildAllNamesRequest(_allNamesUrl);
                var response = ExecuteGet(httpClient, request);
                var data = GetResponseData(response);
                __result = ParseMappings(data);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("allNames redirect failed, falling back to original: " + ex);
                return true;
            }
        }

        private static object BuildAllNamesRequest(string allNamesUrl)
        {
            var builderType = AccessTools.TypeByName("NzbDrone.Common.Http.HttpRequestBuilder");
            if (builderType == null)
            {
                throw new InvalidOperationException("HttpRequestBuilder type not found");
            }

            var builder = Activator.CreateInstance(builderType, allNamesUrl);

            var addSuffix = builderType.GetMethod("AddSuffixQueryParam", new[] { typeof(string), typeof(object), typeof(bool) });
            var addQuery = builderType.GetMethod("AddQueryParam", new[] { typeof(string), typeof(object), typeof(bool) });

            if (addSuffix == null || addQuery == null)
            {
                throw new InvalidOperationException("HttpRequestBuilder query param methods not found");
            }

            addSuffix.Invoke(builder, new object[] { "origin", "tvdb", false });
            addQuery.Invoke(builder, new object[] { "seasonNumbers", true, false });

            return builderType.GetMethod("Build").Invoke(builder, null);
        }

        internal static string BuildAllNamesRequestUrl(string allNamesUrl)
        {
            var request = BuildAllNamesRequest(allNamesUrl);
            return request.GetType().GetProperty("Url").GetValue(request).ToString();
        }

        private static object ExecuteGet(object httpClient, object request)
        {
            var httpClientType = httpClient.GetType();
            var getMethod = httpClientType.GetMethods()
                .FirstOrDefault(m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1);
            if (getMethod == null)
            {
                throw new InvalidOperationException("IHttpClient.Get<T> not found");
            }

            var xemResultType = AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Xem.Model.XemResult`1");
            if (xemResultType == null)
            {
                throw new InvalidOperationException("XemResult<T> type not found");
            }

            var generic = xemResultType.MakeGenericType(typeof(Dictionary<int, List<JObject>>));
            var closedGet = getMethod.MakeGenericMethod(generic);

            return closedGet.Invoke(httpClient, new object[] { request });
        }

        private static Dictionary<int, List<JObject>> GetResponseData(object response)
        {
            var resource = response.GetType().GetProperty("Resource")?.GetValue(response);
            if (resource == null)
            {
                throw new InvalidOperationException("HttpResponse.Resource is null");
            }

            return (Dictionary<int, List<JObject>>)resource.GetType().GetProperty("Data").GetValue(resource);
        }

        private static object ParseMappings(Dictionary<int, List<JObject>> data)
        {
            var sceneMappingType = AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Scene.SceneMapping");
            if (sceneMappingType == null)
            {
                throw new InvalidOperationException("SceneMapping type not found");
            }

            var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sceneMappingType));

            foreach (var series in data)
            {
                foreach (var name in series.Value)
                {
                    foreach (var n in name)
                    {
                        if (!int.TryParse(n.Value.ToString(), out var seasonNumber))
                        {
                            continue;
                        }

                        // hack to deal with Fate/Zero
                        if (series.Key == 79151 && seasonNumber > 1)
                        {
                            continue;
                        }

                        var mapping = Activator.CreateInstance(sceneMappingType);
                        sceneMappingType.GetProperty("Title").SetValue(mapping, n.Key);
                        sceneMappingType.GetProperty("SearchTerm").SetValue(mapping, n.Key);
                        sceneMappingType.GetProperty("SceneSeasonNumber").SetValue(mapping, seasonNumber);
                        sceneMappingType.GetProperty("TvdbId").SetValue(mapping, series.Key);
                        result.Add(mapping);
                    }
                }
            }

            return result;
        }

        private static object GetInstanceField(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException(instance.GetType().Name + "." + fieldName + " not found");
            }

            return field.GetValue(instance);
        }
    }
}
