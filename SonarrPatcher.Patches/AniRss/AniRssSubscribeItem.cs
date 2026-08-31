using System.Collections.Generic;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// One subscription entry: a tvdbId + season watched through a priority-ordered
    /// list of RSS feeds. Lower indexes in <see cref="Rss"/> have higher priority.
    /// </summary>
    public class AniRssSubscribeItem
    {
        public int TvdbId { get; set; }

        public int Season { get; set; }

        /// <summary>Regex applied to each RSS item title to extract the episode number.</summary>
        public string EpRegex { get; set; } = " ([0-9]{2,}) ";

        /// <summary>Offset added to the parsed episode number (for series starting at a non-1 episode).</summary>
        public int EpOffset { get; set; }

        /// <summary>RSS feed URLs; lower index = higher priority.</summary>
        public List<string> Rss { get; set; } = new List<string>();
    }
}
