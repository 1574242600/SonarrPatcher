using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// One subscription entry: a tvdbId + season watched through a priority-ordered
    /// list of RSS feeds. Lower indexes in <see cref="Rss"/> have higher priority.
    /// </summary>
    public class AniRssSubscribeItem
    {
        /// <summary>
        /// Optional human-readable name for this subscription. Exists only to make
        /// the config file easier to read and edit by hand; no business logic reads it.
        /// Omitted from the written file when not set.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Title { get; set; }

        public int TvdbId { get; set; }

        public int Season { get; set; }

        /// <summary>Default <see cref="EpRegex"/>: a plain episode number surrounded by spaces.</summary>
        public const string DefaultEpRegex = " ([0-9]{2,}) ";

        /// <summary>
        /// Optional regex applied to each RSS item title to extract the episode number.
        /// Unset (null/whitespace) means <see cref="DefaultEpRegex"/>, so callers must
        /// fall back to <see cref="DefaultEpRegex"/>; omitted from the written file when
        /// unset. Mirrors <see cref="EpOffset"/>: the type's default marks "not set".
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string EpRegex { get; set; }

        /// <summary>
        /// Optional offset added to the parsed episode number (for series starting at a
        /// non-1 episode). Defaults to 0; omitted from the written file when 0.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int EpOffset { get; set; }

        /// <summary>RSS feed URLs; lower index = higher priority.</summary>
        public List<string> Rss { get; set; } = new List<string>();
    }
}
