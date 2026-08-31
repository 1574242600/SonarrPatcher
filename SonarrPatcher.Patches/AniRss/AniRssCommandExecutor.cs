using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// Periodic ani-rss style task: for every subscribed series it walks the
    /// priority-ordered RSS feeds, matches episode numbers, and pushes missing or
    /// higher-priority releases to the configured download client. Every pushed
    /// release is tagged with <c>#ANIRSS{index}</c> in its title, which is persisted
    /// into the grab history so later runs can detect ANIRSS-downloaded episodes and
    /// decide whether a better source should replace them.
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
                _logger.Warn("ANIRSS_SUBSCRIBE_FILE is not set, skipping execution.");
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
            var epRegex = sub.EpRegex.IsNullOrWhiteSpace() ? " ([0-9]{2,}) " : sub.EpRegex;

            _logger.Info("processing {0} S{1} ({2} episodes, {3} rss sources)", series.Title, sub.Season, episodesByNumber.Count, sub.Rss?.Count ?? 0);

            for (var rssIndex = 0; rssIndex < (sub.Rss?.Count ?? 0); rssIndex++)
            {
                ProcessFeed(sub, series, rssIndex, episodesByNumber, latestGrabByEpisodeId, downloadClientId, epRegex);
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

                if (ShouldSkipExistingFile(episode, sub.Season, targetEp, rssIndex, latestGrabByEpisodeId))
                {
                    continue;
                }

                try
                {
                    DownloadHelper.Download(_downloadService, item, series, episode, rssIndex, downloadClientId);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to queue download for '{0}': {1}", item.Title, ex.Message);
                }
            }
        }

        /// <summary>
        /// Decides whether an episode that already has a file should be left alone:
        /// files not grabbed by ANIRSS are never touched, and an ANIRSS-grabbed file
        /// is only replaced when the current feed has a higher priority (lower index).
        /// </summary>
        private bool ShouldSkipExistingFile(Episode episode,
                                            int season,
                                            int episodeNumber,
                                            int rssIndex,
                                            Dictionary<int, EpisodeHistory> latestGrabByEpisodeId)
        {
            if (!episode.HasFile)
            {
                return false;
            }

            var existingIndex = GetAniRssIndex(latestGrabByEpisodeId, episode.Id);
            if (existingIndex == null)
            {
                // Episode file exists but was not downloaded by ANIRSS; leave it alone.
                _logger.Info("S{0}E{1} already has a file (not ANIRSS), skipping.", season, episodeNumber);
                return true;
            }

            if (rssIndex >= existingIndex.Value)
            {
                // Current source is not better than the one that grabbed the file.
                _logger.Info("S{0}E{1} already grabbed from ANIRSS index {2}, current {3} not better, skipping.", season, episodeNumber, existingIndex.Value, rssIndex);
                return true;
            }

            // Higher priority source: push again; Sonarr's import/upgrade
            // machinery replaces the old file.
            _logger.Info("S{0}E{1} upgrading from ANIRSS index {2} to {3}.", season, episodeNumber, existingIndex.Value, rssIndex);
            return false;
        }

        private static int? GetAniRssIndex(Dictionary<int, EpisodeHistory> latestGrabByEpisodeId, int episodeId)
        {
            if (!latestGrabByEpisodeId.TryGetValue(episodeId, out var entry))
            {
                return null;
            }

            var match = AniRssMarker.Match(entry.SourceTitle ?? string.Empty);
            return ParseAniRssIndex(match);
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

        internal static int? ParseAniRssIndex(Match match)
        {
            return match.Success && int.TryParse(match.Groups[1].Value, out var index) ? index : (int?)null;
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
                _logger.Warn("ANIRSS_SUBSCRIBE_FILE is not set, cannot persist subscribe config.");
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
