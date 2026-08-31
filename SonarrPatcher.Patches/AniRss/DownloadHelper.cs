using System.Collections.Generic;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// Builds a <see cref="TorrentInfo"/> + <see cref="RemoteEpisode"/> from an RSS
    /// item and pushes it to the configured download client, appending a
    /// <c>#ANIRSS{index}</c> marker to the release title so later runs can tell
    /// whether an episode was downloaded by AniRss and from which RSS source.
    /// </summary>
    internal static class DownloadHelper
    {
        public static void Download(IDownloadService downloadService,
                                    TorrentInfo item,
                                    Series series,
                                    Episode episode,
                                    int rssIndex,
                                    int downloadClientId)
        {
            var release = new TorrentInfo
            {
                Title = item.Title + " #ANIRSS" + rssIndex,
                DownloadUrl = item.DownloadUrl,
                MagnetUrl = item.MagnetUrl,
                InfoHash = item.InfoHash,
                Size = item.Size,
                PublishDate = item.PublishDate,
                Guid = item.Guid,
                DownloadProtocol = DownloadProtocol.Torrent
            };

            var parsedEpisodeInfo = new ParsedEpisodeInfo
            {
                ReleaseTitle = release.Title,
                SeriesTitle = series.Title,
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumbers = new[] { episode.EpisodeNumber },
                Quality = new QualityModel(Quality.Unknown),
                Languages = new List<Language>(),
                FullSeason = false
            };

            var remoteEpisode = new RemoteEpisode
            {
                Release = release,
                ParsedEpisodeInfo = parsedEpisodeInfo,
                Series = series,
                Episodes = new List<Episode> { episode },
                Languages = new List<Language>(),
                DownloadAllowed = true,
                CustomFormats = new List<CustomFormat>(),
                CustomFormatScore = 0,
                SeriesMatchType = SeriesMatchType.Unknown
            };

            downloadService.DownloadReport(remoteEpisode, downloadClientId).GetAwaiter().GetResult();
        }
    }
}
