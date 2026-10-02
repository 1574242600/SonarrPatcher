using System.Collections.Generic;
using NzbDrone.Common;
using NzbDrone.Core.History;
using NzbDrone.Core.Tv;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// The rules AniRss decides by, as pure functions over episodes and grab history: which
    /// source owns an episode, whether an episode may be pushed, and whether a subscription
    /// has anything left to do. They take no Sonarr service and touch no file, which keeps
    /// them readable on their own and unit-testable without a running Sonarr.
    /// <para>
    /// Feed indexes are the subscribe file's positions and lower means higher priority. The
    /// source that owns an episode is identified by its feed URL's CRC32 rather than by the
    /// index recorded in the history marker, because the subscribe file can be edited and
    /// reorder feeds: <see cref="GetAniRssSourceIndex"/> locates the recorded fingerprint in
    /// the <em>current</em> list, and only that position is compared against the feed at hand.
    /// </para>
    /// </summary>
    internal static class AniRssSourcePolicy
    {
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

            var match = AniRssMarker.Regex.Match(entry.SourceTitle ?? string.Empty);
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

        /// <summary>
        /// The push decision: an episode is only pushed when nothing better is on record
        /// for it. A file on disk that ANIRSS did not grab is never touched, and an
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
        /// True when a subscription has nothing left to do: every episode of the
        /// watched season is on disk and none of them is owned by a feed below the top
        /// one, so no feed of the list can add or replace anything. A series Sonarr has
        /// no episode for is never finished - the subscription would silently stop
        /// watching a series whose episodes have not shown up yet.
        /// <para>
        /// A file ANIRSS did not grab - one that arrived by other means, or whose
        /// grabbing feed was removed from the list, which leaves no index to resolve -
        /// counts as the top feed's. The feed list exists to fill the gaps of and to
        /// replace what lesser ANIRSS sources delivered, and such a file is never
        /// touched (see <see cref="ShouldSkipEpisodeCore"/>), so keeping a
        /// subscription alive for it would only re-list every feed forever.
        /// </para>
        /// </summary>
        internal static bool IsFinished(AniRssSubscribeItem sub,
                                        List<Episode> episodes,
                                        Dictionary<int, EpisodeHistory> latestGrabByEpisodeId)
        {
            if (episodes.Count == 0)
            {
                return false;
            }

            foreach (var episode in episodes)
            {
                if (!episode.HasFile)
                {
                    // Not aired yet, still downloading, or lost: the subscription has
                    // to stay until every episode of the season is in place.
                    return false;
                }

                var source = GetAniRssSourceIndex(sub, latestGrabByEpisodeId, episode.Id);
                if (source.HasValue && source.Value != 0)
                {
                    // Owned by a lower priority feed: the top feed may still replace it.
                    return false;
                }
            }

            return true;
        }
    }
}
