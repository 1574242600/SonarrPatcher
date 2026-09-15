using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// One subscription entry: a tvdbId + season watched through a priority-ordered
    /// list of RSS feeds. <see cref="EpRegex"/> and <see cref="EpOffset"/> are indexed
    /// by position in <see cref="Rss"/>, whose lower indexes have higher priority:
    /// entry <c>i</c> configures feed <c>i</c>, and entry 0 also serves as the default
    /// for the feeds that have no entry of their own.
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

        /// <summary>Default episode-number regex: a plain episode number surrounded by spaces.</summary>
        public const string DefaultEpRegex = @" (\d{2,}) ";

        /// <summary>
        /// Optional per-feed regexes applied to each RSS item title to extract the
        /// episode number: <c>EpRegex[i]</c> belongs to <c>Rss[i]</c>. A feed the array
        /// does not cover - a shorter array, or a null/blank entry - falls back to
        /// <c>EpRegex[0]</c>, and to <see cref="DefaultEpRegex"/> when index 0 is unset
        /// as well. Unset (null) is omitted from the written file; callers must go
        /// through <see cref="EpRegexFor"/> rather than reading the list directly.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string> EpRegex { get; set; }

        /// <summary>
        /// Optional per-feed offsets added to the parsed episode number (for series
        /// starting at a non-1 episode): <c>EpOffset[i]</c> belongs to <c>Rss[i]</c>,
        /// falling back to <c>EpOffset[0]</c> exactly like <see cref="EpRegex"/>, and to
        /// 0 when neither is set. Unset (null) is omitted from the written file.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<int> EpOffset { get; set; }

        /// <summary>RSS feed URLs; lower index = higher priority.</summary>
        public List<string> Rss { get; set; } = new List<string>();

        /// <summary>
        /// Episode-number regex in force for the feed at <paramref name="rssIndex"/>:
        /// the feed's own entry, the default entry at index 0, or
        /// <see cref="DefaultEpRegex"/> when neither is configured.
        /// </summary>
        internal string EpRegexFor(int rssIndex)
        {
            return RegexAt(rssIndex) ?? RegexAt(0) ?? DefaultEpRegex;
        }

        /// <summary>
        /// Offset in force for the feed at <paramref name="rssIndex"/>, with the same
        /// fallback to the default entry at index 0. A missing entry is detected by
        /// index, not by value: 0 is a legitimate offset.
        /// </summary>
        internal int EpOffsetFor(int rssIndex)
        {
            var offsets = EpOffset;
            if (offsets == null || offsets.Count == 0)
            {
                return 0;
            }

            return rssIndex < offsets.Count ? offsets[rssIndex] : offsets[0];
        }

        /// <summary>
        /// The configured pattern at <paramref name="index"/>, or null when the array
        /// does not reach that index or the entry is blank - both mean "not configured
        /// for this feed", which is what makes the index-0 fallback kick in.
        /// </summary>
        private string RegexAt(int index)
        {
            var patterns = EpRegex;
            if (patterns == null || index >= patterns.Count)
            {
                return null;
            }

            var pattern = patterns[index];
            return string.IsNullOrWhiteSpace(pattern) ? null : pattern;
        }
    }
}
