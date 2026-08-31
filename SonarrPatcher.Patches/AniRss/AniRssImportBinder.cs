using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.EpisodeImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;
using SonarrPatcher.Common;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// Routes finished AniRss downloads through Sonarr's own manual-import command so the
    /// episode AniRss picked from the RSS feed is the one that gets imported.
    /// <para>
    /// Sonarr imports finished downloads in <c>DownloadProcessingService</c>: every minute
    /// it walks the tracked downloads and calls <c>CompletedDownloadService.Import</c> for
    /// the ones sitting in <c>ImportPending</c>. That import re-derives the episode from
    /// the file name, the folder name and the download-client title — none of which come
    /// from the feed — and rejects anything it cannot map.
    /// </para>
    /// <para>
    /// This patch hooks that entry point as a Harmony prefix. For downloads whose grab
    /// history carries the AniRss <c>#ANIRSS{index}</c> marker it builds a
    /// <see cref="ManualImportCommand"/> with the grabbed episode ids and lets that
    /// command do the importing, then skips Sonarr's automatic import entirely —
    /// <c>ManualImportService</c> applies the requested episodes after aggregation and
    /// completes the download itself (marks it imported, publishes
    /// <c>DownloadCompletedEvent</c>), so nothing is left dangling.
    /// </para>
    /// <para>
    /// Everything else is left alone: non-AniRss downloads keep Sonarr's normal behaviour,
    /// and only Sonarr's own public command types are used — no internals are re-implemented.
    /// </para>
    /// </summary>
    internal static class AniRssImportBinder
    {
        /// <summary>
        /// Marker AniRss appends to every pushed release title, and that Sonarr persists
        /// in the grab history's source title.
        /// </summary>
        internal static readonly Regex MarkerRegex = new Regex(@"#ANIRSS(\d+)", RegexOptions.Compiled);

        /// <summary>
        /// How long a download id is remembered after queueing its manual import. Sonarr
        /// retries <c>Import</c> every minute while the download stays pending, so without
        /// this the same command would be queued over and over.
        /// </summary>
        internal static TimeSpan RequeueCooldown = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Rejection reasons the manual import is allowed to override. These are all about
        /// matching a file to an episode or deciding whether it is an upgrade — exactly the
        /// decisions AniRss already made when it pushed the release. Anything else (sample,
        /// unpacking, free space, dangerous file...) keeps the file excluded.
        /// </summary>
        private static readonly HashSet<ImportRejectionReason> OverridableReasons = new HashSet<ImportRejectionReason>
        {
            ImportRejectionReason.InvalidSeasonOrEpisode,
            ImportRejectionReason.UnableToParse,
            ImportRejectionReason.NoEpisodes,
            ImportRejectionReason.MissingAbsoluteEpisodeNumber,
            ImportRejectionReason.EpisodeNotFoundInRelease,
            ImportRejectionReason.EpisodeUnexpected,
            ImportRejectionReason.EpisodeAlreadyImported,
            ImportRejectionReason.UnverifiedSceneMapping,
            ImportRejectionReason.ExistingFileHasMoreEpisodes,
            ImportRejectionReason.SplitEpisode,
            ImportRejectionReason.FullSeason,
            ImportRejectionReason.PartialSeason,
            ImportRejectionReason.SeasonExtra,
            ImportRejectionReason.NotQualityUpgrade,
            ImportRejectionReason.NotRevisionUpgrade,
            ImportRejectionReason.NotCustomFormatUpgrade
        };

        private static readonly ILogger Log = new Logger("AniRssPatch");

        private static readonly ConcurrentDictionary<string, DateTime> Queued = new ConcurrentDictionary<string, DateTime>();

        private static IHistoryService _historyService;
        private static IManualImportService _manualImportService;
        private static IManageCommandQueue _commandQueue;
        private static bool _servicesWarned;

        /// <summary>DI-built history service, captured by a constructor postfix.</summary>
        internal static IHistoryService HistoryService
        {
            get => _historyService;
            set => _historyService = value;
        }

        /// <summary>DI-built manual import service, captured by a constructor postfix.</summary>
        internal static IManualImportService ManualImportService
        {
            get => _manualImportService;
            set => _manualImportService = value;
        }

        /// <summary>DI-built command queue, captured by a constructor postfix.</summary>
        internal static IManageCommandQueue CommandQueue
        {
            get => _commandQueue;
            set => _commandQueue = value;
        }

        /// <summary>Constructor postfix that remembers the container's <c>HistoryService</c>.</summary>
        public static void CaptureHistoryService(object __instance)
        {
            _historyService = __instance as IHistoryService;
        }

        /// <summary>Constructor postfix that remembers the container's <c>ManualImportService</c>.</summary>
        public static void CaptureManualImportService(object __instance)
        {
            _manualImportService = __instance as IManualImportService;
        }

        /// <summary>Constructor postfix that remembers the container's <c>CommandQueueManager</c>.</summary>
        public static void CaptureCommandQueue(object __instance)
        {
            _commandQueue = __instance as IManageCommandQueue;
        }

        /// <summary>
        /// Prefix for <c>CompletedDownloadService.Import</c>. Returning false skips
        /// Sonarr's automatic import because the download has been handed to the manual
        /// import command instead.
        /// </summary>
        public static bool ImportPrefix(TrackedDownload trackedDownload)
        {
            try
            {
                return !DivertToManualImport(trackedDownload);
            }
            catch (Exception ex)
            {
                Log.Warn("AniRss: failed to divert download to manual import: " + ex.Message);
                return true;
            }
        }

        /// <summary>
        /// Queues a manual import for AniRss downloads. Returns true when the download was
        /// taken over (already queued or queued just now), false when Sonarr should import
        /// it the usual way.
        /// </summary>
        internal static bool DivertToManualImport(TrackedDownload trackedDownload)
        {
            var downloadItem = trackedDownload?.DownloadItem;
            var downloadId = downloadItem?.DownloadId;

            if (downloadId.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (_historyService == null || _manualImportService == null || _commandQueue == null)
            {
                if (!_servicesWarned)
                {
                    _servicesWarned = true;
                    Log.Warn("AniRss manual import inactive: Sonarr services were not captured.");
                }

                return false;
            }

            var grabbed = (_historyService.FindByDownloadId(downloadId) ?? new List<EpisodeHistory>())
                .Where(h => h.EventType == EpisodeHistoryEventType.Grabbed)
                .ToList();

            // Only ever take over releases AniRss itself pushed.
            if (!grabbed.Any(h => IsAniRssTitle(h.SourceTitle)))
            {
                return false;
            }

            // Already handed over, the command just has not run yet. Keep the automatic
            // import out of the way but do not queue it again.
            if (Queued.TryGetValue(downloadId, out var queuedAt) && DateTime.UtcNow - queuedAt < RequeueCooldown)
            {
                return true;
            }

            var outputPath = trackedDownload.ImportItem?.OutputPath.FullPath;
            if (outputPath.IsNullOrWhiteSpace())
            {
                Log.Warn("AniRss: no import path for '" + downloadItem.Title + "', leaving it to Sonarr.");
                return false;
            }

            var seriesId = grabbed.First().SeriesId;
            var episodeIds = grabbed.Select(h => h.EpisodeId).Distinct().ToList();

            // Let Sonarr list the files itself: the items come back with quality, languages
            // and release group already parsed, plus the rejections the normal import would
            // produce. Only the episode mapping is ours to decide.
            var items = _manualImportService.GetMediaFiles(outputPath, downloadId, seriesId, true);
            var files = SelectFiles(items, seriesId, episodeIds, downloadId);

            if (files.Count == 0)
            {
                Log.Warn("AniRss: no importable file in '" + outputPath + "', leaving it to Sonarr.");
                return false;
            }

            _commandQueue.Push(new ManualImportCommand { Files = files, ImportMode = ImportMode.Auto });

            Queued[downloadId] = DateTime.UtcNow;
            PruneQueued();

            Log.Info("AniRss: handed '" + downloadItem.Title + "' to manual import as episode(s) [" +
                string.Join(", ", episodeIds) + "] over " + files.Count + " file(s).");

            return true;
        }

        /// <summary>
        /// Turns Sonarr's manual-import items into command files, forcing the grabbed
        /// episodes where it is unambiguous. Single-file downloads always get the grabbed
        /// episodes; with several files Sonarr's own mapping is kept and only the largest
        /// unmapped file is bound to the grabbed episode, so a batch can never collapse
        /// onto one episode. Items rejected for anything other than episode matching or
        /// upgrade checks (samples, unpacking, free space...) are dropped.
        /// </summary>
        internal static List<ManualImportFile> SelectFiles(List<ManualImportItem> items, int seriesId, List<int> episodeIds, string downloadId)
        {
            var files = new List<ManualImportFile>();

            var usable = (items ?? new List<ManualImportItem>())
                .Where(IsOverridable)
                .OrderByDescending(i => i.Size)
                .ToList();

            if (usable.Count == 0)
            {
                return files;
            }

            var boundUnmappedFile = false;

            foreach (var item in usable)
            {
                List<int> ids;

                if (usable.Count == 1)
                {
                    ids = episodeIds;
                }
                else if (item.Episodes != null && item.Episodes.Count > 0)
                {
                    ids = item.Episodes.Select(e => e.Id).ToList();
                }
                else if (!boundUnmappedFile)
                {
                    ids = episodeIds;
                    boundUnmappedFile = true;
                }
                else
                {
                    continue;
                }

                files.Add(new ManualImportFile
                {
                    Path = item.Path,
                    FolderName = item.FolderName,
                    SeriesId = seriesId,
                    EpisodeIds = ids,
                    Quality = item.Quality ?? new QualityModel(Quality.Unknown),
                    Languages = item.Languages,
                    ReleaseGroup = item.ReleaseGroup,
                    ReleaseType = item.ReleaseType,
                    IndexerFlags = item.IndexerFlags,
                    DownloadId = downloadId
                });
            }

            return files;
        }

        private static bool IsOverridable(ManualImportItem item)
        {
            var rejections = item.Rejections;

            return rejections == null || rejections.All(r => OverridableReasons.Contains(r.Reason));
        }

        /// <summary>True when a release/history title was produced by AniRss.</summary>
        internal static bool IsAniRssTitle(string title)
        {
            return title != null && MarkerRegex.IsMatch(title);
        }

        private static void PruneQueued()
        {
            if (Queued.Count < 100)
            {
                return;
            }

            foreach (var entry in Queued.Where(e => DateTime.UtcNow - e.Value >= RequeueCooldown).ToList())
            {
                Queued.TryRemove(entry.Key, out _);
            }
        }
    }
}
