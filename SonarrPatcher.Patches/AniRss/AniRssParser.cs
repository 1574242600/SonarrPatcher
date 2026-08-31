using System.Xml.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Indexers;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// Reuses Sonarr's <see cref="TorrentRssParser"/> for RSS parsing (entity/unicode
    /// cleaning, pubDate/size parsing, per-item fault tolerance) while also reading the
    /// magnet link from the item's <c>&lt;link&gt;</c> element when the enclosure only
    /// carries the .torrent URL.
    /// </summary>
    public sealed class AniRssParser : TorrentRssParser
    {
        public AniRssParser()
        {
            // DownloadUrl = enclosure (.torrent), Size = enclosure length.
            UseEnclosureUrl = true;
            UseEnclosureLength = true;
        }

        protected override string GetMagnetUrl(XElement item)
        {
            var magnet = base.GetMagnetUrl(item);
            if (magnet.IsNotNullOrWhiteSpace())
            {
                return magnet;
            }

            var link = (string)item.Element("link");
            return link != null && link.StartsWith("magnet:") ? link : null;
        }
    }
}
