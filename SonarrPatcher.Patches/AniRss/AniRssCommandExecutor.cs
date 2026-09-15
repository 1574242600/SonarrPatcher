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
    /// priority-ordered RSS feeds, matches episode numbers, and pushes releases for
    /// episodes that are neither on disk nor already grabbed (a download on record
    /// without a file means it is queued, failed or was deleted). Every pushed
    /// release is tagged with <c>#ANIRSS{index}-{urlCrc32}</c> in its title, which is
    /// persisted into the grab history so later runs can detect ANIRSS-downloaded
    /// episodes and decide whether a better source should replace them.
    /// <para>
    /// Logging: one summary line per subscription per run, plus one line per push,
    /// upgrade or failure. The per-item detail (each unparsed title, each unmapped
    /// episode, each skip) is logged at Debug, which Sonarr filters out at its
    /// default log level - a feed is re-listed in full on every run, so per-item
    /// Info/Warn lines would drown the few lines that carry information.
    /// </para>
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
            // Newest grab per episode: it identifies the ANIRSS source that owns an
            // episode, and records that the episode has a download behind it at all
            // (the reason an episode without a file is not pushed again).
            var latestGrabByEpisodeId = LatestGrabByEpisodeId(
                _historyService.GetBySeason(series.Id, sub.Season, EpisodeHistoryEventType.Grabbed));

            // Everything the feed walk shares - inputs, lookups, in-flight pushes and
            // the counters behind the summary line - lives in one run object, so the
            // walk only has to be told which feed it is on.
            var run = new SubscriptionRun(sub, series, downloadClientId, episodesByNumber, latestGrabByEpisodeId);

            _logger.Debug("processing {0} S{1} ({2} episodes, {3} rss sources)", series.Title, sub.Season, episodesByNumber.Count, sub.Rss?.Count ?? 0);

            for (var rssIndex = 0; rssIndex < (sub.Rss?.Count ?? 0); rssIndex++)
            {
                ProcessFeed(run, rssIndex);
            }

            // The only line a quiet run produces, and the one that makes a broken
            // subscription obvious: pushed 0 next to unparsed <item count> means the
            // regex matched nothing (warned per feed), pushed 0 next to unmapped <n>
            // means the episode numbers do not exist in Sonarr.
            _logger.Info("{0} S{1}: pushed {2}, upgraded {3}, skipped {4}, unparsed {5}, unmapped {6}",
                series.Title,
                sub.Season,
                run.Stats.Pushed,
                run.Stats.Upgraded,
                run.Stats.Skipped,
                run.Stats.Unparsed,
                run.Stats.Unmapped);
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

        /// <summary>
        /// Walks one RSS source of a subscription run: parses each item's episode
        /// number, resolves it to a Sonarr episode and pushes the release when the
        /// skip policy lets it through.
        /// </summary>
        private void ProcessFeed(SubscriptionRun run, int rssIndex)
        {
            var sub = run.Subscribe;
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

            var parsedItems = 0;

            foreach (var item in items)
            {
                var epNumber = ParseEpisodeNumber(item.Title, run.EpRegex);
                if (epNumber == null)
                {
                    run.Stats.Unparsed++;
                    _logger.Debug("could not parse episode number from '{0}'", item.Title);
                    continue;
                }

                parsedItems++;

                var targetEp = epNumber.Value + sub.EpOffset;
                if (!run.EpisodesByNumber.TryGetValue(targetEp, out var episode))
                {
                    run.Stats.Unmapped++;
                    _logger.Debug("no episode S{0}E{1} found for series {2} (from '{3}')", sub.Season, targetEp, run.Series.Title, item.Title);
                    continue;
                }

                if (ShouldSkipEpisode(episode, run, rssIndex))
                {
                    continue;
                }

                try
                {
                    DownloadHelper.Download(_downloadService, item, run.Series, episode, rssIndex, url, run.DownloadClientId);

                    // Queued successfully - record it for the remaining feeds. Only a
                    // successful queue counts: when the download client rejects the push,
                    // a lower-priority feed may still try the episode later.
                    run.PushedThisRun[episode.Id] = rssIndex;
                    run.Stats.Pushed++;
                    _logger.Info("S{0}E{1} pushed from rss{2}: {3}", episode.SeasonNumber, episode.EpisodeNumber, rssIndex, item.Title);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to queue download for '{0}': {1}", item.Title, ex.Message);
                }
            }

            // A feed that matched nothing is the one per-item warning worth keeping:
            // it cannot be seen in the counts alone (a regex can fail for every item
            // of every feed) and it points straight at the misconfiguration.
            if (items.Count > 0 && parsedItems == 0)
            {
                _logger.Warn("epRegex matched no item in rss{0} ({1} items), check the pattern '{2}'", rssIndex, items.Count, run.EpRegex);
            }
        }

        /// <summary>
        /// Decides whether an episode should be left alone, resolving the context
        /// <see cref="ShouldSkipEpisodeCore"/> needs: the ANIRSS source that owns the
        /// episode and whether any download is on record for it.
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
        private bool ShouldSkipEpisode(Episode episode, SubscriptionRun run, int rssIndex)
        {
            var existingIndex = ResolveExistingSourceIndex(run.PushedThisRun, run.LatestGrabByEpisodeId, run.Subscribe, episode.Id);
            var hasGrabHistory = run.LatestGrabByEpisodeId.ContainsKey(episode.Id);

            if (!ShouldSkipEpisodeCore(episode.HasFile, hasGrabHistory, existingIndex, rssIndex))
            {
                if (existingIndex != null)
                {
                    // Higher priority source: push again; Sonarr's import/upgrade
                    // machinery replaces the old file.
                    run.Stats.Upgraded++;
                    _logger.Info("S{0}E{1} upgrading ANIRSS index {2} -> {3}.", episode.SeasonNumber, episode.EpisodeNumber, existingIndex.Value, rssIndex);
                }

                return false;
            }

            run.Stats.Skipped++;
            _logger.Debug("S{0}E{1} skipping (file={2}, grabbed={3}, anirssIndex={4}, currentIndex={5}).",
                episode.SeasonNumber,
                episode.EpisodeNumber,
                episode.HasFile,
                hasGrabHistory,
                existingIndex.HasValue ? existingIndex.Value.ToString() : "-",
                rssIndex);

            return true;
        }

        /// <summary>
        /// Pure decision rule behind <see cref="ShouldSkipEpisode"/>, unit-testable
        /// without Sonarr: an episode is only pushed when nothing better is on record
        /// for it.  A file on disk that ANIRSS did not grab is never touched, and an
        /// episode with a download behind it - a file on disk, a download still in
        /// flight, or a grab whose download failed or was deleted - is not pushed
        /// again: the download client would reject the duplicate, and re-pushing an
        /// episode ANIRSS already handed over only spams the log.
        /// <paramref name="existingAniRssIndex"/> is the grabbed source's position in
        /// the <em>current</em> feed list (see <see cref="GetAniRssSourceIndex"/>), not
        /// the marker's recorded index.
        /// </summary>
        internal static bool ShouldSkipEpisodeCore(bool episodeHasFile, bool episodeHasGrabHistory, int? existingAniRssIndex, int rssIndex)
        {
            if (existingAniRssIndex != null && rssIndex >= existingAniRssIndex.Value)
            {
                // Current source is not better than the one that grabbed the episode,
                // whether the file has been imported yet or the download is still in
                // flight.
                return true;
            }

            if (!episodeHasFile)
            {
                // Nothing on disk, but a download is on record: it is queued, failed
                // or was deleted. Only an episode nobody has grabbed yet is pushed.
                return episodeHasGrabHistory;
            }

            // File on disk that ANIRSS did not grab: leave it alone. A file ANIRSS
            // grabbed itself is only replaced by a higher priority source.
            return existingAniRssIndex == null;
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

        /// <summary>
        /// State of a single subscription pass, shared by every feed of that
        /// subscription: the inputs the walk needs (subscription, series, download
        /// client, effective regex), the lookups built once per pass, the episodes
        /// pushed so far, and the counters the summary line reports. It exists so
        /// the feed walk is told which feed to process instead of being handed the
        /// same eight values at every level.
        /// </summary>
        private sealed class SubscriptionRun
        {
            public SubscriptionRun(AniRssSubscribeItem subscribe,
                                   Series series,
                                   int downloadClientId,
                                   Dictionary<int, Episode> episodesByNumber,
                                   Dictionary<int, EpisodeHistory> latestGrabByEpisodeId)
            {
                Subscribe = subscribe;
                Series = series;
                DownloadClientId = downloadClientId;
                EpisodesByNumber = episodesByNumber;
                LatestGrabByEpisodeId = latestGrabByEpisodeId;
                EpRegex = subscribe.EpRegex.IsNullOrWhiteSpace() ? AniRssSubscribeItem.DefaultEpRegex : subscribe.EpRegex;
            }

            /// <summary>Subscription being processed; its <c>Rss</c> list drives the walk.</summary>
            public AniRssSubscribeItem Subscribe { get; }

            /// <summary>Sonarr series the subscription maps to.</summary>
            public Series Series { get; }

            /// <summary>Client the releases are queued to.</summary>
            public int DownloadClientId { get; }

            /// <summary>Episode-number regex actually in use (configured, or the default).</summary>
            public string EpRegex { get; }

            /// <summary>Episodes of the watched season by episode number.</summary>
            public Dictionary<int, Episode> EpisodesByNumber { get; }

            /// <summary>
            /// Newest grab per episode as of the start of this pass. It only knows
            /// history that predates the run, so <see cref="PushedThisRun"/> takes
            /// precedence when both know an episode.
            /// </summary>
            public Dictionary<int, EpisodeHistory> LatestGrabByEpisodeId { get; }

            /// <summary>
            /// Episodes pushed earlier in this pass, by episode id to feed index. Feeds
            /// are walked sequentially, so an episode pushed from rss0 must be remembered
            /// here or rss1 would queue it again: the download is still in flight,
            /// HasFile is false, and the start-of-run snapshot has no record of it yet.
            /// </summary>
            public Dictionary<int, int> PushedThisRun { get; } = new Dictionary<int, int>();

            /// <summary>Counters behind the one summary line of this pass.</summary>
            public RunStats Stats { get; } = new RunStats();
        }

        /// <summary>
        /// Counters of a single subscription pass, reported by one summary line.
        /// Per-item detail is logged at Debug instead of being repeated per item:
        /// a feed re-lists every episode on every run, so the interesting numbers
        /// are the totals (<see cref="Pushed"/> next to <see cref="Unparsed"/> and
        /// <see cref="Unmapped"/>), not each individual skip.
        /// </summary>
        private sealed class RunStats
        {
            /// <summary>Releases queued to the download client this pass.</summary>
            public int Pushed;

            /// <summary>Episodes re-pushed because a higher priority source now has them.</summary>
            public int Upgraded;

            /// <summary>Episodes left alone because nothing better could be added.</summary>
            public int Skipped;

            /// <summary>Feed items whose title matched <c>epRegex</c> nowhere.</summary>
            public int Unparsed;

            /// <summary>Feed items whose episode number has no episode in Sonarr.</summary>
            public int Unmapped;
        }
    }
}
