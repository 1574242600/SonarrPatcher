using System.Text.RegularExpressions;
using NzbDrone.Common;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// The <c>#ANIRSS{index}-{urlCrc32}</c> marker AniRss appends to every pushed release
    /// title, and that Sonarr persists in the grab history's source title. Both ends of
    /// the format live here so it has one authority: <see cref="Append"/> writes it on the
    /// push path (<see cref="DownloadHelper"/>), <see cref="Regex"/> reads it back from the
    /// grab history (<see cref="AniRssSourcePolicy"/>) and from the import path
    /// (<see cref="AniRssImportBinder"/>).
    /// <para>
    /// The feed URL's CRC32 is part of the marker because the subscribe file can be edited
    /// and reorder feeds: a recorded position would then point at a different source, while
    /// the fingerprint still identifies the feed that pushed the release.
    /// </para>
    /// </summary>
    internal static class AniRssMarker
    {
        /// <summary>Matches a marker and captures its feed position and URL fingerprint.</summary>
        internal static readonly Regex Regex = new Regex(@"#ANIRSS(\d+)-([0-9a-f]{8})", RegexOptions.Compiled);

        /// <summary>Release title as the download client and the grab history will see it.</summary>
        internal static string Append(string title, int rssIndex, string rssUrl)
        {
            return title + " #ANIRSS" + rssIndex + "-" + HashUtil.CalculateCrc(rssUrl);
        }
    }
}
