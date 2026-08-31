using System;
using System.Linq;
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

        /// <summary>
        /// Mikan publishes the date inside a namespaced element
        /// (<c>&lt;torrent xmlns="https://mikanani.me/0.1/"&gt;&lt;pubDate&gt;...&lt;/pubDate&gt;&lt;/torrent&gt;</c>),
        /// so Sonarr's base parser (which only reads the item-level, namespace-free
        /// <c>&lt;pubDate&gt;</c>) sees it as missing and throws an
        /// <c>UnsupportedFeedException</c> that aborts the whole feed. Read the
        /// item-level date first, then any <c>pubDate</c> element inside the item
        /// (covers Mikan), and only fall back to "now" when there is genuinely no
        /// usable date - a missing/invalid date must never fail the whole feed.
        /// </summary>
        protected override DateTime GetPublishDate(XElement item)
        {
            var dateString = (string)item.Element("pubDate");
            if (dateString.IsNullOrWhiteSpace())
            {
                dateString = (string)item.Descendants().FirstOrDefault(e => e.Name.LocalName == "pubDate");
            }

            try
            {
                return XElementExtensions.ParseDate(dateString);
            }
            catch (Exception)
            {
                return DateTime.UtcNow;
            }
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
