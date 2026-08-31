using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using SonarrPatcher.Common;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// Registers the AniRss scheduled task, makes this assembly discoverable by
    /// Sonarr's container/KnownTypes so <see cref="AniRssCommand"/> and
    /// <see cref="AniRssCommandExecutor"/> are wired up natively, and routes finished
    /// AniRss downloads through Sonarr's own manual-import command (see
    /// <see cref="AniRssImportBinder"/>).
    /// </summary>
    public sealed class AniRssPatch : Patch
    {
        private static int _intervalMinutes;
        public static string subscribeFile;
        public static string downClientName;

        static AniRssPatch()
        {
            Name = "AniRssPatch";
            Log = new Logger(Name);
            _intervalMinutes = int.TryParse(Environment.GetEnvironmentVariable("ANIRSS_INTERVAL_MINUTES"), out var interval) ? interval : 60;
            subscribeFile = Environment.GetEnvironmentVariable("ANIRSS_SUBSCRIBE_FILE");
            downClientName = Environment.GetEnvironmentVariable("ANIRSS_DOWNLOAD_CLIENT_NAME");
        }

        public override bool ShouldPatch()
        {
            if (_intervalMinutes == 0 || string.IsNullOrWhiteSpace(subscribeFile))
            {
                Log.Info("AniRss disabled (ANIRSS_INTERVAL_MINUTES=0 or ANIRSS_SUBSCRIBE_FILE empty)");
                return false;
            }

            return true;
        }

        protected override void Apply(Harmony harmony)
        {
            var assemblyLoaderType = AccessTools.TypeByName("NzbDrone.Common.Composition.AssemblyLoader");
            if (assemblyLoaderType == null)
            {
                throw new InvalidOperationException("AssemblyLoader type not found");
            }

            var loadMethod = AccessTools.Method(assemblyLoaderType, "Load");
            if (loadMethod == null)
            {
                throw new InvalidOperationException("AssemblyLoader.Load not found");
            }

            harmony.Patch(loadMethod, postfix: new HarmonyMethod(typeof(AniRssPatch).GetMethod(nameof(AssemblyLoaderLoadPostfix), BindingFlags.NonPublic | BindingFlags.Static)));

            var taskManagerType = AccessTools.TypeByName("NzbDrone.Core.Jobs.TaskManager");
            if (taskManagerType == null)
            {
                throw new InvalidOperationException("TaskManager type not found");
            }

            var handleMethod = AccessTools.Method(taskManagerType, "Handle", new[] { typeof(ApplicationStartedEvent) });
            if (handleMethod == null)
            {
                throw new InvalidOperationException("TaskManager.Handle not found");
            }

            harmony.Patch(handleMethod, postfix: new HarmonyMethod(typeof(AniRssPatch).GetMethod(nameof(TaskManagerHandlePostfix), BindingFlags.NonPublic | BindingFlags.Static)));

            PatchManualImport(harmony);

            Log.Info("Patch applied. interval=" + _intervalMinutes + " min");
        }

        /// <summary>
        /// Takes finished AniRss downloads away from Sonarr's automatic import and hands
        /// them to the manual-import command with the episodes AniRss grabbed, and captures
        /// the services that command needs. There is no service locator in Sonarr, so the
        /// container-built instances are picked up through constructor postfixes.
        /// </summary>
        private static void PatchManualImport(Harmony harmony)
        {
            var completedDownloadService = AccessTools.TypeByName("NzbDrone.Core.Download.CompletedDownloadService");
            var importMethod = completedDownloadService == null
                ? null
                : AccessTools.Method(completedDownloadService, "Import", new[] { typeof(TrackedDownload) });

            if (importMethod == null)
            {
                Log.Warn("CompletedDownloadService.Import not found, AniRss downloads import the regular way");
                return;
            }

            harmony.Patch(importMethod, prefix: Method(nameof(AniRssImportBinder.ImportPrefix)));

            CaptureService(harmony, "NzbDrone.Core.History.HistoryService", Method(nameof(AniRssImportBinder.CaptureHistoryService)));
            CaptureService(harmony, "NzbDrone.Core.MediaFiles.EpisodeImport.Manual.ManualImportService", Method(nameof(AniRssImportBinder.CaptureManualImportService)));
            CaptureService(harmony, "NzbDrone.Core.Messaging.Commands.CommandQueueManager", Method(nameof(AniRssImportBinder.CaptureCommandQueue)));
        }

        private static void CaptureService(Harmony harmony, string typeName, HarmonyMethod postfix)
        {
            var type = AccessTools.TypeByName(typeName);
            var ctor = type == null ? null : AccessTools.Constructor(type);

            if (ctor == null)
            {
                Log.Warn(typeName + " constructor not found, AniRss manual import disabled");
                return;
            }

            harmony.Patch(ctor, postfix: postfix);
        }

        private static HarmonyMethod Method(string methodName)
        {
            return new HarmonyMethod(typeof(AniRssImportBinder).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static));
        }

        /// <summary>
        /// Appends this assembly to the list of assemblies Sonarr scans at startup
        /// (AutoAddServices). This makes <see cref="AniRssCommand"/> visible to
        /// KnownTypes and registers <see cref="AniRssCommandExecutor"/> for
        /// IExecute&lt;AniRssCommand&gt; without any further patching.
        /// </summary>
        private static void AssemblyLoaderLoadPostfix(IList<Assembly> __result)
        {
            var asm = typeof(AniRssCommand).Assembly;
            if (!__result.Contains(asm))
            {
                __result.Add(asm);
            }
        }

        /// <summary>
        /// Inserts the AniRss scheduled task into the repository and cache after
        /// TaskManager.Handle has finished its default task bookkeeping.
        /// </summary>
        private static void TaskManagerHandlePostfix(object __instance)
        {
            var repository = GetField(__instance, "_scheduledTaskRepository") as IScheduledTaskRepository;
            var cache = GetField(__instance, "_cache") as ICached<ScheduledTask>;
            if (repository == null || cache == null)
            {
                Log.Warn("TaskManager fields not found, AniRss task not registered");
                return;
            }

            var task = new ScheduledTask
            {
                TypeName = typeof(AniRssCommand).FullName,
                Interval = _intervalMinutes,
                LastExecution = DateTime.UtcNow,
                Priority = CommandPriority.Low
            };

            repository.Upsert(task);
            cache.Set(task.TypeName, task);
            Log.Info("AniRss task registered. interval=" + _intervalMinutes + " min");
        }

        private static object GetField(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(instance);
        }
    }
}
