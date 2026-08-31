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
        /// <summary>
        /// Sonarr's own exception for Fate/Zero (tvdb 79151): the seasons after the
        /// first are a different show, so only season 1 aliases are mapped.
        /// </summary>
        private const int FateZeroTvdbId = 79151;

        private static string _allNamesUrl;
        private static bool _disableEnglishFilter;

        static XemAliases()
        {
            Name = "XemAliasesPatch";
            _allNamesUrl = Environment.GetEnvironmentVariable("XEM_ALLNAMES_URL");
            _disableEnglishFilter = Environment.GetEnvironmentVariable("DISABLE_NONENGLISH_ALIASES_PATCH") == "1";
        }

        public override bool ShouldPatch()
        {
            return !string.IsNullOrEmpty(_allNamesUrl) || !_disableEnglishFilter;
        }

        protected override void Apply(Harmony harmony)
        {
            if (!string.IsNullOrEmpty(_allNamesUrl))
            {
                var xemProxyType = ReflectionHelper.RequireType(AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Xem.XemProxy"), "XemProxy");
                var getSceneTvdbNames = ReflectionHelper.RequireMethod(AccessTools.DeclaredMethod(xemProxyType, "GetSceneTvdbNames"), "XemProxy.GetSceneTvdbNames");

                var prefix = typeof(XemAliases).GetMethod(nameof(GetSceneTvdbNamesPrefix), BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(getSceneTvdbNames, prefix: new HarmonyMethod(prefix));
                Log.Info("Patched XemProxy.GetSceneTvdbNames to redirect allNames to " + _allNamesUrl);
            }

            if (!_disableEnglishFilter)
            {
                var sceneMappingServiceType = ReflectionHelper.RequireType(AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Scene.SceneMappingService"), "SceneMappingService");
                var isEnglish = ReflectionHelper.RequireMethod(AccessTools.DeclaredMethod(sceneMappingServiceType, "IsEnglish", new[] { typeof(string) }), "SceneMappingService.IsEnglish");

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
                var httpClient = ReflectionHelper.GetInstanceField(__instance, "_httpClient");
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
            var builderType = ReflectionHelper.RequireType(AccessTools.TypeByName("NzbDrone.Common.Http.HttpRequestBuilder"), "HttpRequestBuilder");

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

            var xemResultType = ReflectionHelper.RequireType(AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Xem.Model.XemResult`1"), "XemResult<T>");

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
            var sceneMappingType = ReflectionHelper.RequireType(AccessTools.TypeByName("NzbDrone.Core.DataAugmentation.Scene.SceneMapping"), "SceneMapping");

            var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sceneMappingType));

            foreach (var series in data)
            {
                foreach (var name in series.Value)
                {
                    foreach (var property in name)
                    {
                        if (!int.TryParse(property.Value.ToString(), out var seasonNumber))
                        {
                            continue;
                        }

                        // Fate/Zero hack: seasons after 1 belong to a different show.
                        if (series.Key == FateZeroTvdbId && seasonNumber > 1)
                        {
                            continue;
                        }

                        result.Add(CreateMapping(sceneMappingType, series.Key, property.Key, seasonNumber));
                    }
                }
            }

            return result;
        }

        private static object CreateMapping(Type sceneMappingType, int tvdbId, string title, int seasonNumber)
        {
            var mapping = Activator.CreateInstance(sceneMappingType);
            sceneMappingType.GetProperty("Title").SetValue(mapping, title);
            sceneMappingType.GetProperty("SearchTerm").SetValue(mapping, title);
            sceneMappingType.GetProperty("SceneSeasonNumber").SetValue(mapping, seasonNumber);
            sceneMappingType.GetProperty("TvdbId").SetValue(mapping, tvdbId);
            return mapping;
        }
    }
}
