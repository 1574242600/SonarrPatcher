using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.EpisodeImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// Periodic ani-rss style task: for every subscribed series it walks the
    /// priority-ordered RSS feeds, matches episode numbers, and pushes missing or
    /// higher-priority releases to the configured download client. Every pushed
    /// release is tagged with <c>#ANIRSS{index}-{urlCrc32}</c> in its title, which is
    /// persisted into the grab history so later runs can detect ANIRSS-downloaded
    /// episodes and decide whether a better source should replace them.
    /// </summary>
    public class AniRssCommandExecutor : IExecute<AniRssCommand>
    {
        // Shared with the import binder, which recognises the same marker in the grab history.
        private static readonly Regex AniRssMarker = AniRssImportBinder.MarkerRegex;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly IHistoryService _historyService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IDownloadService _downloadService;
        private readonly IHttpClient _httpClient;
        private readonly AniRssParser _parser;
        private readonly Logger _logger;

        public AniRssCommandExecutor(ISeriesService seriesService,
                                     IEpisodeService episodeService,
                                     IHistoryService historyService,
                                     IProvideDownloadClient downloadClientProvider,
                                     IDownloadService downloadService,
                                     IHttpClient httpClient,
                                     IManualImportService manualImportService,
                                     IManageCommandQueue commandQueue,
                                     AniRssParser parser,
                                     Logger logger)
        {
            _seriesService = seriesService;
            _episodeService = episodeService;
            _historyService = historyService;
            _downloadClientProvider = downloadClientProvider;
            _downloadService = downloadService;
            _httpClient = httpClient;
            _parser = parser;
            _logger = logger;

            // The executor is built by Sonarr's DI container (AutoAddServices scans this
            // assembly), so these parameters are the same singletons the import path uses.
            // Forwarding them here gives the import binder its services without any runtime
            // capture - constructor postfixes are unreliable (compiled ctor calls get
            // inlined past the Harmony detour), and there is no service locator to query.
            AniRssImportBinder.HistoryService = historyService;
            AniRssImportBinder.ManualImportService = manualImportService;
            AniRssImportBinder.CommandQueue = commandQueue;
        }

        public void Execute(AniRssCommand message)
        {
            var config = LoadConfig(message);
            if (config == null || config.Count == 0)
            {
                _logger.Warn("No AniRss subscriptions to process.");
                return;
            }

            var downloadClientId = ResolveDownloadClientId();
            if (!downloadClientId.HasValue)
            {
                return;
            }

            foreach (var sub in config)
            {
                ProcessSubscribeItem(sub, downloadClientId.Value);
            }
        }

        /// <summary>
        /// Prefers the subscriptions carried by the command (persisting them to the
        /// subscribe file), otherwise falls back to the subscribe file on disk.
        /// Returns null when there is nothing to process.
        /// </summary>
        private List<AniRssSubscribeItem> LoadConfig(AniRssCommand message)
        {
            if (message.Subscribe != null && message.Subscribe.Count > 0)
            {
                WriteConfigFile(AniRssPatch.SubscribeFile, message.Subscribe);
                return message.Subscribe;
            }

            var configPath = AniRssPatch.SubscribeFile;
            if (configPath.IsNullOrWhiteSpace())
            {
                _logger.Warn("subscribe file path is empty, skipping execution.");
                return null;
            }

            if (!File.Exists(configPath))
            {
                _logger.Warn("subscribe file not found: {0}", configPath);
                return null;
            }

            return ReadConfigFile(configPath);
        }

        private int? ResolveDownloadClientId()
        {
            var clients = _downloadClientProvider.GetDownloadClients().ToList();
            if (clients.Count == 0)
            {
                _logger.Warn("No download client is configured, aborting AniRss execution.");
                return null;
            }

            var clientName = AniRssPatch.DownloadClientName;
            IDownloadClient client;
            if (clientName.IsNotNullOrWhiteSpace())
            {
                client = clients.FirstOrDefault(c => c.Definition.Name == clientName);
                if (client == null)
                {
                    _logger.Warn("Download client '{0}' not found, aborting AniRss execution.", clientName);
                    return null;
                }
            }
            else
            {
                client = clients.First();
            }

            return client.Definition.Id;
        }

        private void ProcessSubscribeItem(AniRssSubscribeItem sub, int downloadClientId)
        {
            var series = _seriesService.FindByTvdbId(sub.TvdbId);
            if (series == null)
            {
                _logger.Warn("No series with tvdbId {0} found, skipping.", sub.TvdbId);
                return;
            }

            // Lookups used by every item of every feed are built once per subscription
            // instead of being rescanned inside the item loop.
            var episodesByNumber = IndexEpisodesByNumber(series.Id, sub.Season);
            var latestGrabByEpisodeId = LatestGrabByEpisodeId(
                _historyService.GetBySeason(series.Id, sub.Season, EpisodeHistoryEventType.Grabbed));
            var epRegex = sub.EpRegex.IsNullOrWhiteSpace() ? AniRssSubscribeItem.DefaultEpRegex : sub.EpRegex;

            // The grab snapshot above only knows history that predates this run. Feeds
            // are walked sequentially, so an episode pushed from rss0 must be remembered
            // in-process or rss1 would queue it again (the download is still in flight,
            // HasFile is false, and the snapshot has no record of it yet).
            var pushedThisRun = new Dictionary<int, int>();

            _logger.Info("processing {0} S{1} ({2} episodes, {3} rss sources)", series.Title, sub.Season, episodesByNumber.Count, sub.Rss?.Count ?? 0);

            for (var rssIndex = 0; rssIndex < (sub.Rss?.Count ?? 0); rssIndex++)
            {
                ProcessFeed(sub, series, rssIndex, episodesByNumber, latestGrabByEpisodeId, pushedThisRun, downloadClientId, epRegex);
            }
        }

        /// <summary>Episode-per-number index; a duplicate number keeps the first episode.</summary>
        private Dictionary<int, Episode> IndexEpisodesByNumber(int seriesId, int season)
        {
            return _episodeService.GetEpisodesBySeason(seriesId, season)
                .GroupBy(e => e.EpisodeNumber)
                .ToDictionary(g => g.Key, g => g.First());
        }

        /// <summary>
        /// For each episode the newest grabbed history entry. Single pass, O(n); the
        /// ">" comparison keeps the earlier entry on equal dates, matching the old
        /// stable OrderByDescending(..).First() selection.
        /// </summary>
        internal static Dictionary<int, EpisodeHistory> LatestGrabByEpisodeId(List<EpisodeHistory> history)
        {
            var latest = new Dictionary<int, EpisodeHistory>();
            foreach (var entry in history)
            {
                if (!latest.TryGetValue(entry.EpisodeId, out var current) || entry.Date > current.Date)
                {
                    latest[entry.EpisodeId] = entry;
                }
            }

            return latest;
        }

        private void ProcessFeed(AniRssSubscribeItem sub,
                                 Series series,
                                 int rssIndex,
                                 Dictionary<int, Episode> episodesByNumber,
                                 Dictionary<int, EpisodeHistory> latestGrabByEpisodeId,
                                 Dictionary<int, int> pushedThisRun,
                                 int downloadClientId,
                                 string epRegex)
        {
            var url = sub.Rss[rssIndex];
            List<TorrentInfo> items;
            try
            {
                items = FetchAndParse(url);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to fetch RSS {0}: {1}", url, ex.Message);
                return;
            }

            foreach (var item in items)
            {
                var epNumber = ParseEpisodeNumber(item.Title, epRegex);
                if (epNumber == null)
                {
                    _logger.Warn("Could not parse episode number from '{0}'", item.Title);
                    continue;
                }

                var targetEp = epNumber.Value + sub.EpOffset;
                if (!episodesByNumber.TryGetValue(targetEp, out var episode))
                {
                    _logger.Warn("No episode S{0}E{1} found for series {2} (from '{3}')", sub.Season, targetEp, series.Title, item.Title);
                    continue;
                }

                if (ShouldSkipExistingFile(episode, sub, rssIndex, latestGrabByEpisodeId, pushedThisRun))
                {
                    continue;
                }

                try
                {
                    DownloadHelper.Download(_downloadService, item, series, episode, rssIndex, url, downloadClientId);

                    // Queued successfully - record it for the remaining feeds. Only a
                    // successful queue counts: when the download client rejects the push,
                    // a lower-priority feed may still try the episode later.
                    pushedThisRun[episode.Id] = rssIndex;
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to queue download for '{0}': {1}", item.Title, ex.Message);
                }
            }
        }

        /// <summary>
        /// Decides whether an episode should be left alone:
        /// files not grabbed by ANIRSS are never touched, and an ANIRSS-grabbed
        /// episode is only re-pushed when the current feed has a higher priority
        /// (lower index). Episodes already grabbed from the same or a worse source
        /// are skipped whether or not the file has landed yet - re-pushing while the
        /// download is still in progress makes the download client reject the
        /// duplicate torrent.
        /// <para>
        /// The source that grabbed the episode is identified by its RSS URL's CRC32
        /// rather than the index stored in the history marker: the subscribe file can
        /// be edited and reorder feeds, so a recorded index refers to an old list.
        /// The grabbed source is located in the <em>current</em> list by fingerprint,
        /// and only that position is compared against <paramref name="rssIndex"/>.
        /// A source that pushed the episode earlier <em>in this run</em> takes
        /// precedence, because the run-start history snapshot cannot see it.
        /// </para>
        /// </summary>
        private bool ShouldSkipExistingFile(Episode episode,
                                            AniRssSubscribeItem sub,
                                            int rssIndex,
                                            Dictionary<int, EpisodeHistory> latestGrabByEpisodeId,
                                            Dictionary<int, int> pushedThisRun)
        {
            var existingIndex = ResolveExistingSourceIndex(pushedThisRun, latestGrabByEpisodeId, sub, episode.Id);

            if (existingIndex != null && rssIndex >= existingIndex.Value)
            {
                // Current source is not better than the one that grabbed the episode,
                // whether the file has been imported yet or the download is still in
                // flight (the duplicate would be rejected by the download client).
                _logger.Info("S{0}E{1} already grabbed from ANIRSS index {2}, current {3} not better, skipping.", episode.SeasonNumber, episode.EpisodeNumber, existingIndex.Value, rssIndex);
                return true;
            }

            if (!episode.HasFile)
            {
                return false;
            }

            if (existingIndex == null)
            {
                // Episode file exists but was not downloaded by ANIRSS; leave it alone.
                _logger.Info("S{0}E{1} already has a file (not ANIRSS), skipping.", episode.SeasonNumber, episode.EpisodeNumber);
                return true;
            }

            // Higher priority source: push again; Sonarr's import/upgrade
            // machinery replaces the old file.
            _logger.Info("S{0}E{1} upgrading from ANIRSS index {2} to {3}.", episode.SeasonNumber, episode.EpisodeNumber, existingIndex.Value, rssIndex);
            return false;
        }

        /// <summary>
        /// Pure decision rule behind <see cref="ShouldSkipExistingFile"/>, unit-testable
        /// without Sonarr: skip when the episode is already grabbed from the same or a
        /// worse source (download in progress or file present), or when a file exists
        /// that ANIRSS did not grab. <paramref name="existingAniRssIndex"/> is the
        /// grabbed source's position in the <em>current</em> feed list (see
        /// <see cref="GetAniRssSourceIndex"/>), not the marker's recorded index.
        /// </summary>
        internal static bool ShouldSkipEpisode(bool episodeHasFile, int? existingAniRssIndex, int rssIndex)
        {
            return (existingAniRssIndex != null && rssIndex >= existingAniRssIndex.Value)
                || (episodeHasFile && existingAniRssIndex == null);
        }

        /// <summary>
        /// Position of the source that grabbed the episode in the <em>current</em>
        /// subscription's feed list, resolved by matching the marker's URL CRC32
        /// against each feed's fingerprint. Null when the episode was not grabbed by
        /// ANIRSS, or when the grabbing feed no longer exists in the current list
        /// (it was removed or renamed in the subscribe file).
        /// </summary>
        internal static int? GetAniRssSourceIndex(AniRssSubscribeItem sub,
                                                  Dictionary<int, EpisodeHistory> latestGrabByEpisodeId,
                                                  int episodeId)
        {
            if (!latestGrabByEpisodeId.TryGetValue(episodeId, out var entry))
            {
                return null;
            }

            var match = AniRssMarker.Match(entry.SourceTitle ?? string.Empty);
            if (!match.Success)
            {
                return null;
            }

            var crc = match.Groups[2].Value;
            var rss = sub.Rss;
            if (rss == null)
            {
                return null;
            }

            for (var i = 0; i < rss.Count; i++)
            {
                if (HashUtil.CalculateCrc(rss[i]) == crc)
                {
                    return i;
                }
            }

            return null;
        }

        /// <summary>
        /// The AniRss source that currently owns an episode. A feed that pushed the
        /// episode earlier in this run wins over the run-start history snapshot - the
        /// snapshot cannot see downloads queued after it was taken, so without this
        /// precedence a later feed would queue the same episode again. Falls back to
        /// the snapshot when this run has not touched the episode yet.
        /// </summary>
        internal static int? ResolveExistingSourceIndex(Dictionary<int, int> pushedThisRun,
                                                        Dictionary<int, EpisodeHistory> latestGrabByEpisodeId,
                                                        AniRssSubscribeItem sub,
                                                        int episodeId)
        {
            if (pushedThisRun.TryGetValue(episodeId, out var inRunIndex))
            {
                return inRunIndex;
            }

            return GetAniRssSourceIndex(sub, latestGrabByEpisodeId, episodeId);
        }

        private List<TorrentInfo> FetchAndParse(string url)
        {
            var request = new HttpRequest(url, HttpAccept.Rss)
            {
                RateLimit = TimeSpan.FromMilliseconds(500),
                RateLimitKey = "anirss",
                SuppressHttpError = true
            };

            var httpResponse = _httpClient.Execute(request);
            var indexerResponse = new IndexerResponse(new IndexerRequest(request), httpResponse);
            var releases = _parser.ParseResponse(indexerResponse);

            return releases.OfType<TorrentInfo>().ToList();
        }

        internal static int? ParseEpisodeNumber(string title, string epRegex)
        {
            var match = Regex.Match(title, epRegex, RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            // Prefer the first capture group when present, otherwise use the whole match.
            var value = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            var digits = Regex.Match(value, @"\d+");
            return digits.Success && int.TryParse(digits.Value, out var number) ? number : (int?)null;
        }

        private static List<AniRssSubscribeItem> ReadConfigFile(string configPath)
        {
            try
            {
                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<List<AniRssSubscribeItem>>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to read subscribe file " + configPath + ": " + ex.Message, ex);
            }
        }

        private void WriteConfigFile(string configPath, List<AniRssSubscribeItem> config)
        {
            if (configPath.IsNullOrWhiteSpace())
            {
                _logger.Warn("subscribe file path is empty, cannot persist subscribe config.");
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(configPath);
                if (directory.IsNotNullOrWhiteSpace())
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(configPath, json);
                _logger.Info("subscribe config written to {0}", configPath);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to write subscribe config to {0}: {1}", configPath, ex.Message);
            }
        }
    }
}
