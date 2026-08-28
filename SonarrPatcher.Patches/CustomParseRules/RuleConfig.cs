using System.Collections.Generic;

namespace SonarrPatcher.Patches.CustomParseRules
{
    /// <summary>
    /// Optional forced quality for a rule. <see cref="Source"/> maps to
    /// NzbDrone.Core.Qualities.QualitySource (WEBDL/WEBDL/WEBRip/Bluray/HDTV/...)
    /// and <see cref="Resolution"/> to the vertical resolution (480/576/720/1080/2160).
    /// </summary>
    internal class QualityRule
    {
        public int Resolution { get; set; }
        public string Source { get; set; }
    }

    /// <summary>
    /// One custom parse rule. <see cref="Id"/> is a required unique identifier used
    /// only for logging/dedup; it never participates in the parsed result.
    /// </summary>
    internal class ParseRule
    {
        public string Id { get; set; }
        public bool Enabled { get; set; } = true;
        public string Pattern { get; set; }
        public List<string> Language { get; set; }
        public QualityRule Quality { get; set; }
        public bool UseAbsolute { get; set; }
    }
}
