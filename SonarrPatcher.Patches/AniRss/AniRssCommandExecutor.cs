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
            var configPath = AniRssPatch.subscribeFile;
            List<AniRssSubscribeItem> config;

            if (message.Subscribe != null && message.Subscribe.Count > 0)
            {
                config = message.Subscribe;
                WriteConfigFile(configPath, config);
            }
            else
            {
                if (configPath.IsNullOrWhiteSpace())
                {
                    _logger.Warn("ANIRSS_SUBSCRIBE_FILE is not set, skipping execution.");
                    return;
                }

                if (!File.Exists(configPath))
                {
                    _logger.Warn("AniRss subscribe file not found: {0}", configPath);
                    return;
                }

                config = ReadConfigFile(configPath);
            }

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

        private int? ResolveDownloadClientId()
        {
            var clients = _downloadClientProvider.GetDownloadClients().ToList();
            if (clients.Count == 0)
            {
                _logger.Warn("No download client is configured, aborting AniRss execution.");
                return null;
            }

            var clientName = AniRssPatch.downClientName;
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

            var episodes = _episodeService.GetEpisodesBySeason(series.Id, sub.Season);
            var history = _historyService.GetBySeason(series.Id, sub.Season, EpisodeHistoryEventType.Grabbed);
            var epRegex = sub.EpRegex.IsNullOrWhiteSpace() ? " ([0-9]{2,}) " : sub.EpRegex;

            _logger.Info("AniRss: processing {0} S{1} ({2} episodes, {3} rss sources)", series.Title, sub.Season, episodes.Count, sub.Rss?.Count ?? 0);

            for (var rssIndex = 0; rssIndex < (sub.Rss?.Count ?? 0); rssIndex++)
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
                    continue;
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
                    var episode = episodes.FirstOrDefault(e => e.EpisodeNumber == targetEp);
                    if (episode == null)
                    {
                        _logger.Warn("No episode S{0}E{1} found for series {2} (from '{3}')", sub.Season, targetEp, series.Title, item.Title);
                        continue;
                    }

                    if (episode.HasFile)
                    {
                        var existingIndex = GetAniRssIndex(history, episode.Id);
                        if (existingIndex == null)
                        {
                            // Episode file exists but was not downloaded by ANIRSS; leave it alone.
                            _logger.Info("S{0}E{1} already has a file (not ANIRSS), skipping.", sub.Season, targetEp);
                            continue;
                        }

                        if (rssIndex >= existingIndex.Value)
                        {
                            // Current source is not better than the one that grabbed the file.
                            _logger.Info("S{0}E{1} already grabbed from ANIRSS index {2}, current {3} not better, skipping.", sub.Season, targetEp, existingIndex.Value, rssIndex);
                            continue;
                        }

                        // Higher priority source: push again; Sonarr's import/upgrade
                        // machinery replaces the old file.
                        _logger.Info("S{0}E{1} upgrading from ANIRSS index {2} to {3}.", sub.Season, targetEp, existingIndex.Value, rssIndex);
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

        private static int? GetAniRssIndex(List<EpisodeHistory> history, int episodeId)
        {
            var entry = history
                .Where(h => h.EpisodeId == episodeId)
                .OrderByDescending(h => h.Date)
                .FirstOrDefault();

            if (entry == null)
            {
                return null;
            }

            var match = AniRssMarker.Match(entry.SourceTitle ?? string.Empty);
            return ParseAniRssIndex(match);
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
                _logger.Info("AniRss subscribe config written to {0}", configPath);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to write subscribe config to {0}: {1}", configPath, ex.Message);
            }
        }
    }
}
