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
    /// history carries the AniRss <c>#ANIRSS{index}-{urlCrc32}</c> marker it builds a
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
        /// in the grab history's source title. Format: <c>#ANIRSS{index}-{urlCrc32}</c>.
        /// </summary>
        internal static readonly Regex MarkerRegex = new Regex(@"#ANIRSS(\d+)-([0-9a-f]{8})", RegexOptions.Compiled);

        /// <summary>
        /// How long a download id is remembered after queueing its manual import. Sonarr
        /// retries <c>Import</c> every minute while the download stays pending, so without
        /// this the same command would be queued over and over.
        /// </summary>
        internal static TimeSpan RequeueCooldown = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Rejection reasons the manual import is allowed to override, and only ever for a
        /// download that holds a single usable file. These are the checks AniRss already
        /// answered when it pushed the release: identifying the series, parsing the episode out
        /// of the file, matching it against the series, and deciding whether it is an upgrade.
        /// The first two matter most in practice — Sonarr only has the file name to go on, and
        /// for an AniRss title that says little. Anything else (sample, unpacking, free space,
        /// dangerous file...) keeps the file excluded.
        /// </summary>
        private static readonly HashSet<ImportRejectionReason> OverridableReasons = new HashSet<ImportRejectionReason>
        {
            ImportRejectionReason.UnknownSeries,
            ImportRejectionReason.UnableToParse,
            ImportRejectionReason.InvalidSeasonOrEpisode,
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

        /// <summary>
        /// Logger shared with <see cref="AniRssPatch"/> (same prefix), created
        /// automatically from the patch name when the patch type is initialized.
        /// Safe to use from the binder's Harmony methods: they only run after
        /// <c>AniRssPatch</c> was applied, so its static constructor has run.
        /// </summary>
        private static ILogger Log => AniRssPatch.Log;

        private static readonly ConcurrentDictionary<string, DateTime> Queued = new ConcurrentDictionary<string, DateTime>();

        private static IHistoryService _historyService;
        private static IManualImportService _manualImportService;
        private static IManageCommandQueue _commandQueue;
        private static bool _servicesWarned;

        /// <summary>
        /// DI-built history service, forwarded by <see cref="AniRssCommandExecutor"/> (which
        /// is constructed by Sonarr's container).
        /// </summary>
        internal static IHistoryService HistoryService
        {
            get => _historyService;
            set => _historyService = value;
        }

        /// <summary>
        /// DI-built manual import service, forwarded by <see cref="AniRssCommandExecutor"/>.
        /// </summary>
        internal static IManualImportService ManualImportService
        {
            get => _manualImportService;
            set => _manualImportService = value;
        }

        /// <summary>
        /// DI-built command queue, forwarded by <see cref="AniRssCommandExecutor"/>.
        /// </summary>
        internal static IManageCommandQueue CommandQueue
        {
            get => _commandQueue;
            set => _commandQueue = value;
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
                Log.Warn("failed to divert download to manual import: " + ex.Message);
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

            // Services are forwarded by the DI-built AniRssCommandExecutor on its first run,
            // which always precedes any AniRss download completing. If they are still missing
            // (e.g. standalone startup without the task), fall back to Sonarr's own import.
            if (_historyService == null || _manualImportService == null || _commandQueue == null)
            {
                if (!_servicesWarned)
                {
                    _servicesWarned = true;
                    Log.Warn("manual import inactive: Sonarr services were not captured.");
                }

                return false;
            }

            var grabbed = GetAniRssGrabbedHistory(downloadId);

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
                Log.Warn("no import path for '" + downloadItem.Title + "', leaving it to Sonarr.");
                return false;
            }

            var seriesId = grabbed.First().SeriesId;
            var episodeIds = grabbed.Select(h => h.EpisodeId).Distinct().ToList();

            // Let Sonarr list the files itself: the items come back with quality, languages
            // and release group already parsed, plus the rejections the normal import would
            // produce. Only the episode mapping is ours to decide.
            var items = _manualImportService.GetMediaFiles(outputPath, downloadId, seriesId, true);
            var usable = UsableItems(items);

            if (usable.Count == 0)
            {
                Log.Warn("no importable file in '" + outputPath + "', leaving it to Sonarr.");
                return false;
            }

            // AniRss pushes one episode per release, so more than one importable file means the
            // download is not what it pushed. Do not guess which of them is the episode.
            if (usable.Count > 1)
            {
                Log.Warn("'" + downloadItem.Title + "' holds " + usable.Count + " importable files, leaving it to Sonarr.");
                return false;
            }

            var files = new List<ManualImportFile> { BuildFile(usable[0], seriesId, episodeIds, downloadId) };

            _commandQueue.Push(new ManualImportCommand { Files = files, ImportMode = ImportMode.Auto });

            Queued[downloadId] = DateTime.UtcNow;
            PruneQueued();

            Log.Info("handed '" + downloadItem.Title + "' to manual import as episode(s) [" +
                string.Join(", ", episodeIds) + "] over " + files.Count + " file(s).");

            return true;
        }

        /// <summary>
        /// The items the manual import is allowed to use. Anything rejected for a reason other
        /// than episode matching or upgrade checks (sample, unpacking, free space...) is
        /// dropped: those are the checks AniRss did not make when it pushed the release.
        /// </summary>
        internal static List<ManualImportItem> UsableItems(List<ManualImportItem> items)
        {
            return (items ?? new List<ManualImportItem>()).Where(IsOverridable).ToList();
        }

        /// <summary>
        /// Turns one usable item into a command file, forcing the grabbed episodes: for an
        /// AniRss release the episode picked from the feed is the right one even when Sonarr
        /// mapped the file to something else, or could not map it at all. Everything Sonarr
        /// parsed (quality, languages, release group) is passed through untouched.
        /// </summary>
        internal static ManualImportFile BuildFile(ManualImportItem item, int seriesId, List<int> episodeIds, string downloadId)
        {
            return new ManualImportFile
            {
                Path = item.Path,
                FolderName = item.FolderName,
                SeriesId = seriesId,
                EpisodeIds = episodeIds,
                Quality = item.Quality ?? new QualityModel(Quality.Unknown),
                Languages = item.Languages,
                ReleaseGroup = item.ReleaseGroup,
                ReleaseType = item.ReleaseType,
                IndexerFlags = item.IndexerFlags,
                DownloadId = downloadId
            };
        }

        private static bool IsOverridable(ManualImportItem item)
        {
            var rejections = item.Rejections;

            return rejections == null || rejections.All(r => OverridableReasons.Contains(r.Reason));
        }

        /// <summary>
        /// The grabbed history entries for a download. Non-AniRss downloads have no
        /// marker in any entry and are therefore left to Sonarr's automatic import.
        /// </summary>
        private static List<EpisodeHistory> GetAniRssGrabbedHistory(string downloadId)
        {
            return (_historyService.FindByDownloadId(downloadId) ?? new List<EpisodeHistory>())
                .Where(h => h.EventType == EpisodeHistoryEventType.Grabbed)
                .ToList();
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
