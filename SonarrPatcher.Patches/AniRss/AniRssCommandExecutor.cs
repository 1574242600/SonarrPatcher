using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
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
    /// without a file means it is queued, failed or was deleted). A subscription the pass
    /// finds nothing left to do for is dropped from the subscribe file, which keeps the
    /// file a watch list rather than a log of everything ever watched.
    /// <para>
    /// This class only orchestrates: where the subscriptions come from and how they are
    /// written back is <see cref="AniRssSubscribeStore"/>, and what may be pushed or
    /// dropped is <see cref="AniRssSourcePolicy"/>. What is left here is the pass itself -
    /// resolve the download client, run one subscription, walk its feeds, report a
    /// summary - so the flow reads top to bottom without leaving the file.
    /// </para>
    /// <para>
    /// Logging: one summary line per subscription per run, plus one line per push,
    /// upgrade or failure. The per-item detail (each unparsed title, each unmapped
    /// episode, each skip) is logged at Debug, which Sonarr filters out at its
    /// default log level - a feed is re-listed in full on every run, so per-item
    /// Info/Warn lines would drown the few lines that carry information. Only
    /// misconfigurations warn, because the counts cannot show them: a feed whose
    /// regex matched nothing, and a subscription with no feeds at all.
    /// </para>
    /// </summary>
    public class AniRssCommandExecutor : IExecute<AniRssCommand>
    {
        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly IHistoryService _historyService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IDownloadService _downloadService;
        private readonly IHttpClient _httpClient;
        private readonly AniRssParser _parser;
        private readonly AniRssSubscribeStore _store;
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

            // The store needs nothing but the logger, so it is built here instead of being
            // injected: a dependency no caller has to choose is noise in the signature.
            _store = new AniRssSubscribeStore(logger);

            // The executor is built by Sonarr's DI container (AutoAddServices scans this
            // assembly), so these parameters are the same singletons the import path uses.
            // Forwarding them here gives the import binder its services without any runtime
            // capture - constructor postfixes are unreliable (compiled ctor calls get
            // inlined past the Harmony detour), and there is no service locator to query.
            AniRssImportBinder.Configure(historyService, manualImportService, commandQueue);
        }

        public void Execute(AniRssCommand message)
        {
            var config = _store.LoadFor(message);
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

            var finished = new List<AniRssSubscribeItem>();

            foreach (var sub in config)
            {
                if (ProcessSubscribeItem(sub, downloadClientId.Value))
                {
                    finished.Add(sub);
                }
            }

            _store.DropFinished(config, finished);
        }

        /// <summary>
        /// Id of the client the releases are queued to: the one named by
        /// <see cref="AniRssPatch.DownloadClientName"/>, or the first configured client
        /// when no name is set. Null aborts the pass.
        /// </summary>
        private int? ResolveDownloadClientId()
        {
            var clients = _downloadClientProvider.GetDownloadClients().ToList();
            if (clients.Count == 0)
            {
                _logger.Warn("No download client is configured, aborting AniRss execution.");
                return null;
            }

            var clientName = AniRssPatch.DownloadClientName;
            if (clientName.IsNullOrWhiteSpace())
            {
                return clients.First().Definition.Id;
            }

            var client = clients.FirstOrDefault(c => c.Definition.Name == clientName);
            if (client == null)
            {
                _logger.Warn("Download client '{0}' not found, aborting AniRss execution.", clientName);
                return null;
            }

            return client.Definition.Id;
        }

        /// <summary>
        /// Runs one subscription's pass: resolves the series, builds the lookups the
        /// feed walk shares, walks every feed and reports the one summary line.
        /// Returns true when the subscription has nothing left to do
        /// (<see cref="AniRssSourcePolicy.IsFinished"/>) and may be dropped from the
        /// subscribe file.
        /// </summary>
        private bool ProcessSubscribeItem(AniRssSubscribeItem sub, int downloadClientId)
        {
            var series = _seriesService.FindByTvdbId(sub.TvdbId);
            if (series == null)
            {
                _logger.Warn("No series with tvdbId {0} found, skipping.", sub.TvdbId);
                return false;
            }

            // A subscription with no feeds has nothing to walk. Warn before spending the
            // lookups below: a pass that ran anyway would report all zeros, and that line
            // is indistinguishable from "every episode is already in place".
            var feedCount = sub.Rss?.Count ?? 0;
            if (feedCount == 0)
            {
                _logger.Warn("{0} S{1}: no rss sources configured, skipping.", series.Title, sub.Season);
                return false;
            }

            var episodes = _episodeService.GetEpisodesBySeason(series.Id, sub.Season);
            var run = BuildRun(sub, series, downloadClientId, episodes);

            _logger.Debug("processing {0} S{1} ({2} episodes, {3} rss sources)", series.Title, sub.Season, run.EpisodesByNumber.Count, feedCount);

            for (var rssIndex = 0; rssIndex < feedCount; rssIndex++)
            {
                ProcessFeed(run, rssIndex);
            }

            LogRunSummary(run);

            // Judged against the snapshot taken while the run was built, not against a
            // re-read: a download queued by this pass has no file yet, and its grab record
            // must not make an episode still waiting for its upgrade look settled.
            if (!AniRssSourcePolicy.IsFinished(sub, episodes, run.LatestGrabByEpisodeId))
            {
                return false;
            }

            _logger.Info("{0} S{1}: complete from the top feed, dropping the subscription.", series.Title, sub.Season);

            return true;
        }

        /// <summary>
        /// The state one subscription pass shares with all of its feeds: the lookups, built
        /// once per subscription instead of being rescanned inside the item loop, plus the
        /// in-flight pushes and counters the walk fills in.
        /// </summary>
        private SubscriptionRun BuildRun(AniRssSubscribeItem sub, Series series, int downloadClientId, List<Episode> episodes)
        {
            // A duplicate episode number keeps the first episode.
            var episodesByNumber = episodes
                .GroupBy(e => e.EpisodeNumber)
                .ToDictionary(g => g.Key, g => g.First());

            // Newest grab per episode: it identifies the ANIRSS source that owns an
            // episode, and records that the episode has a download behind it at all
            // (the reason an episode without a file is not pushed again).
            var latestGrabByEpisodeId = AniRssSourcePolicy.LatestGrabByEpisodeId(
                _historyService.GetBySeason(series.Id, sub.Season, EpisodeHistoryEventType.Grabbed));

            return new SubscriptionRun(sub, series, downloadClientId, episodesByNumber, latestGrabByEpisodeId);
        }

        /// <summary>
        /// The only line a quiet run produces, and the one that makes a broken
        /// subscription obvious: pushed 0 next to unparsed &lt;item count&gt; means the
        /// regex matched nothing (warned per feed), pushed 0 next to unmapped &lt;n&gt;
        /// means the episode numbers do not exist in Sonarr.
        /// </summary>
        private void LogRunSummary(SubscriptionRun run)
        {
            _logger.Info("{0} S{1}: pushed {2}, upgraded {3}, skipped {4}, unparsed {5}, unmapped {6}",
                run.Series.Title,
                run.Subscribe.Season,
                run.Stats.Pushed,
                run.Stats.Upgraded,
                run.Stats.Skipped,
                run.Stats.Unparsed,
                run.Stats.Unmapped);
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
            // Both are per-feed: entry i belongs to feed i, with entry 0 as the
            // default for feeds the arrays do not reach.
            var epRegex = sub.EpRegexFor(rssIndex);
            var epOffset = sub.EpOffsetFor(rssIndex);

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

            var anyParsed = false;

            foreach (var item in items)
            {
                if (ProcessItem(run, rssIndex, item, epRegex, epOffset))
                {
                    anyParsed = true;
                }
            }

            // A feed that matched nothing is the one per-item warning worth keeping:
            // it cannot be seen in the counts alone (a regex can fail for every item
            // of every feed) and it points straight at the misconfiguration.
            if (items.Count > 0 && !anyParsed)
            {
                _logger.Warn("epRegex matched no item in rss{0} ({1} items), check the pattern '{2}'", rssIndex, items.Count, epRegex);
            }
        }

        /// <summary>
        /// One feed item: episode number out of the title, mapped to a Sonarr episode,
        /// then either pushed or skipped. Returns true once the title yielded an episode
        /// number, whether or not the release ended up being pushed - that is what tells a
        /// feed whose regex matched nothing apart from one that simply had nothing new.
        /// </summary>
        private bool ProcessItem(SubscriptionRun run, int rssIndex, TorrentInfo item, string epRegex, int epOffset)
        {
            var epNumber = ParseEpisodeNumber(item.Title, epRegex);
            if (epNumber == null)
            {
                run.Stats.Unparsed++;
                _logger.Debug("could not parse episode number from '{0}'", item.Title);
                return false;
            }

            var targetEp = epNumber.Value + epOffset;
            if (!run.EpisodesByNumber.TryGetValue(targetEp, out var episode))
            {
                run.Stats.Unmapped++;
                _logger.Debug("no episode S{0}E{1} found for series {2} (from '{3}')", run.Subscribe.Season, targetEp, run.Series.Title, item.Title);
                return true;
            }

            if (ShouldSkipEpisode(episode, run, rssIndex))
            {
                return true;
            }

            try
            {
                Push(run, rssIndex, item, episode);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to queue download for '{0}': {1}", item.Title, ex.Message);
            }

            return true;
        }

        /// <summary>
        /// Queues the release to the download client and records the push for the feeds
        /// that come after: only a successful queue counts, because when the download
        /// client rejects the push a lower-priority feed may still take the episode later.
        /// </summary>
        private void Push(SubscriptionRun run, int rssIndex, TorrentInfo item, Episode episode)
        {
            DownloadHelper.Download(_downloadService, item, run.Series, episode, rssIndex, run.Subscribe.Rss[rssIndex], run.DownloadClientId);

            run.PushedThisRun[episode.Id] = rssIndex;
            run.Stats.Pushed++;
            _logger.Info("S{0}E{1} pushed from rss{2}: {3}", episode.SeasonNumber, episode.EpisodeNumber, rssIndex, item.Title);
        }

        /// <summary>
        /// Decides whether an episode should be left alone, resolving the context
        /// <see cref="AniRssSourcePolicy.ShouldSkipEpisodeCore"/> needs: the ANIRSS source
        /// that owns the episode and whether any download is on record for it. The rule
        /// itself lives in the policy - what happens here is applying it and reporting it.
        /// </summary>
        private bool ShouldSkipEpisode(Episode episode, SubscriptionRun run, int rssIndex)
        {
            var existingIndex = AniRssSourcePolicy.ResolveExistingSourceIndex(run.PushedThisRun, run.LatestGrabByEpisodeId, run.Subscribe, episode.Id);
            var hasGrabHistory = run.LatestGrabByEpisodeId.ContainsKey(episode.Id);

            if (!AniRssSourcePolicy.ShouldSkipEpisodeCore(episode.HasFile, hasGrabHistory, existingIndex, rssIndex))
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

        /// <summary>
        /// State of a single subscription pass, shared by every feed of that
        /// subscription: the subscription, the series, the download client, the lookups
        /// built once per pass, the episodes pushed so far, and the counters the
        /// summary line reports. It exists so the feed walk is told which feed to
        /// process instead of being handed the same values at every level - the
        /// per-feed regex and offset are read off the subscription with the feed index.
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
            }

            /// <summary>Subscription being processed; its <c>Rss</c> list drives the walk.</summary>
            public AniRssSubscribeItem Subscribe { get; }

            /// <summary>Sonarr series the subscription maps to.</summary>
            public Series Series { get; }

            /// <summary>Client the releases are queued to.</summary>
            public int DownloadClientId { get; }

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
