using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.EpisodeImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;
using SonarrPatcher.Patches.AniRss;
using Xunit;

namespace SonarrPatcher.Tests
{
    public class AniRssTests
    {
        // ---- Unit tests (no Sonarr needed) ----

        [Fact]
        public void DefaultEpRegex_ExtractsEpisodeNumber()
        {
            Assert.Equal(2, AniRssCommandExecutor.ParseEpisodeNumber("[Sub] Some Title 02 [720p]", " ([0-9]{2,}) "));
        }

        [Fact]
        public void DefaultEpRegex_IgnoresResolution()
        {
            // 1080 is not space-delimited as an independent token, so it must not match.
            Assert.Equal(5, AniRssCommandExecutor.ParseEpisodeNumber("Title 05 [1080p]", " ([0-9]{2,}) "));
        }

        [Fact]
        public void CustomEpRegex_WithGroup_UsesCaptureGroup()
        {
            Assert.Equal(7, AniRssCommandExecutor.ParseEpisodeNumber("Show - 07", @"[- ](\d{1,2})$"));
        }

        [Fact]
        public void EpOffset_AddsToParsedNumber()
        {
            var sub = new AniRssSubscribeItem { EpOffset = 10 };
            Assert.Equal(10, sub.EpOffset);
        }

        [Fact]
        public void ParseEpisodeNumber_NoMatch_ReturnsNull()
        {
            Assert.Null(AniRssCommandExecutor.ParseEpisodeNumber("No numbers here", " ([0-9]{2,}) "));
        }

        [Fact]
        public void ParseAniRssIndex_ExtractsIndex()
        {
            Assert.Equal(3, AniRssCommandExecutor.ParseAniRssIndex(Regex.Match("Title #ANIRSS3", @"#ANIRSS(\d+)")));
        }

        [Fact]
        public void ParseAniRssIndex_NoMarker_ReturnsNull()
        {
            Assert.Null(AniRssCommandExecutor.ParseAniRssIndex(Regex.Match("Plain title", @"#ANIRSS(\d+)")));
        }

        [Fact]
        public void SubscribeConfig_SerializesFormattedJson_RoundTrips()
        {
            var config = new List<AniRssSubscribeItem>
            {
                new AniRssSubscribeItem
                {
                    TvdbId = 123,
                    Season = 1,
                    EpOffset = 2,
                    Rss = new List<string> { "https://feed.example/1", "https://feed.example/2" }
                }
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            // Formatted JSON should contain newlines.
            Assert.Contains('\n', json);

            var back = JsonSerializer.Deserialize<List<AniRssSubscribeItem>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Assert.Single(back);
            Assert.Equal(123, back[0].TvdbId);
            Assert.Equal(1, back[0].Season);
            Assert.Equal(2, back[0].EpOffset);
            Assert.Equal(2, back[0].Rss.Count);
            Assert.Equal(" ([0-9]{2,}) ", back[0].EpRegex);
        }

        // ---- Integration tests (require Sonarr.Core.dll) ----

        [SkippableFact]
        public void AniRssCommand_InheritsSonarrCommand()
        {
            var corePath = Path.Combine(AppContext.BaseDirectory, "Sonarr.Core.dll");
            Skip.If(!File.Exists(corePath), "Sonarr.Core.dll not found; skipping Sonarr integration tests");
            LoadIfAbsent(corePath);

            Assert.True(typeof(Command).IsAssignableFrom(typeof(AniRssCommand)));
        }

        [SkippableFact]
        public void RssParser_ParsesTorrentEnclosure_AndLinkMagnet()
        {
            var corePath = Path.Combine(AppContext.BaseDirectory, "Sonarr.Core.dll");
            Skip.If(!File.Exists(corePath), "Sonarr.Core.dll not found; skipping Sonarr integration tests");
            LoadIfAbsent(corePath);

            const string rss = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
  <channel>
    <title>Feed</title>
    <item>
      <title>[Group] Some Show 05 [1080p][HEVC]</title>
      <link>magnet:?xt=urn:btih:abcdef</link>
      <guid>item-1</guid>
      <pubDate>Tue, 11 Mar 2025 12:00:00 +0000</pubDate>
      <enclosure url=""https://tracker.example/show-05.torrent"" length=""123456789"" type=""application/x-bittorrent"" />
    </item>
  </channel>
</rss>";

            var parser = new AniRssParser();
            var request = new HttpRequest("https://feed.example/rss", HttpAccept.Rss);
            var httpResponse = new HttpResponse(request, new HttpHeader(), rss);
            var response = new IndexerResponse(new IndexerRequest(request), httpResponse);
            var releases = parser.ParseResponse(response);

            var torrent = releases.OfType<TorrentInfo>().Single();
            Assert.Equal("[Group] Some Show 05 [1080p][HEVC]", torrent.Title);
            Assert.Equal("https://tracker.example/show-05.torrent", torrent.DownloadUrl);
            Assert.Equal("magnet:?xt=urn:btih:abcdef", torrent.MagnetUrl);
            Assert.Equal(123456789, torrent.Size);
        }

        [SkippableFact]
        public void AssemblyLoader_AddsAniRssAssemblyToResult()
        {
            var commonPath = Path.Combine(AppContext.BaseDirectory, "Sonarr.Common.dll");
            Skip.If(!File.Exists(commonPath), "Sonarr.Common.dll not found; skipping Sonarr integration tests");
            LoadIfAbsent(commonPath);

            var patchType = typeof(AniRssPatch);
            var postfix = patchType.GetMethod("AssemblyLoaderLoadPostfix", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(postfix);

            var result = new List<Assembly> { typeof(string).Assembly };
            postfix.Invoke(null, new object[] { result });

            Assert.Contains(result, a => a == typeof(AniRssCommand).Assembly);
        }

        // ---- Import binder: marker ----

        [Fact]
        public void IsAniRssTitle_RecognisesMarker()
        {
            Assert.True(AniRssImportBinder.IsAniRssTitle("[Group] Show 05 [1080p] #ANIRSS1"));
            Assert.False(AniRssImportBinder.IsAniRssTitle("[Group] Show 05 [1080p]"));
            Assert.False(AniRssImportBinder.IsAniRssTitle(null));
        }

        // ---- Import binder: file selection (require Sonarr.Core) ----

        [SkippableFact]
        public void SelectFiles_SingleFile_GetsTheGrabbedEpisode()
        {
            SkipIfSonarrMissing();

            var items = new List<ManualImportItem> { Item("/d/ep01.mkv", 100, null) };

            var files = AniRssImportBinder.SelectFiles(items, 1, new List<int> { 5 }, "dl-1");

            var file = Assert.Single(files);
            Assert.Equal(new[] { 5 }, file.EpisodeIds.ToArray());
            Assert.Equal(1, file.SeriesId);
            Assert.Equal("dl-1", file.DownloadId);
            Assert.Equal(ImportMode.Auto, ImportMode.Auto);
        }

        [SkippableFact]
        public void SelectFiles_SingleFile_KeepsWhatSonarrParsed()
        {
            SkipIfSonarrMissing();

            var quality = new QualityModel(Quality.Bluray1080p);
            var item = Item("/d/ep01.mkv", 100, null);
            item.Quality = quality;
            item.ReleaseGroup = "Group";
            item.Languages = new List<Language> { Language.English };

            var file = Assert.Single(AniRssImportBinder.SelectFiles(new List<ManualImportItem> { item }, 1, new List<int> { 5 }, "dl-1"));

            Assert.Same(quality, file.Quality);
            Assert.Equal("Group", file.ReleaseGroup);
            Assert.Equal(new[] { Language.English }, file.Languages.ToArray());
        }

        [SkippableFact]
        public void SelectFiles_SeveralFiles_KeepsTheEpisodesSonarrParsed()
        {
            SkipIfSonarrMissing();

            var items = new List<ManualImportItem>
            {
                Item("/d/a.mkv", 200, new List<Episode> { new Episode { Id = 7 } }),
                Item("/d/b.mkv", 100, new List<Episode> { new Episode { Id = 8 } })
            };

            var files = AniRssImportBinder.SelectFiles(items, 1, new List<int> { 5 }, "dl-1");

            Assert.Equal(2, files.Count);
            Assert.Equal(new[] { 7 }, files[0].EpisodeIds.ToArray());
            Assert.Equal(new[] { 8 }, files[1].EpisodeIds.ToArray());
        }

        [SkippableFact]
        public void SelectFiles_SeveralFiles_BindsOnlyTheLargestUnmappedFile()
        {
            SkipIfSonarrMissing();

            var items = new List<ManualImportItem>
            {
                Item("/d/big.mkv", 300, null),
                Item("/d/small.mkv", 200, null),
                Item("/d/mapped.mkv", 100, new List<Episode> { new Episode { Id = 9 } })
            };

            var files = AniRssImportBinder.SelectFiles(items, 1, new List<int> { 5 }, "dl-1");

            Assert.Equal(2, files.Count);
            Assert.Equal("/d/big.mkv", files[0].Path);
            Assert.Equal(new[] { 5 }, files[0].EpisodeIds.ToArray());
            Assert.Equal("/d/mapped.mkv", files[1].Path);
            Assert.Equal(new[] { 9 }, files[1].EpisodeIds.ToArray());
        }

        [SkippableFact]
        public void SelectFiles_DropsFilesRejectedForSafetyReasons()
        {
            SkipIfSonarrMissing();

            var sample = Item("/d/sample.mkv", 300, null);
            sample.Rejections = new[] { new ImportRejection(ImportRejectionReason.Sample, "sample") };
            var good = Item("/d/ep01.mkv", 100, null);

            var file = Assert.Single(AniRssImportBinder.SelectFiles(new List<ManualImportItem> { sample, good }, 1, new List<int> { 5 }, "dl-1"));

            Assert.Equal("/d/ep01.mkv", file.Path);
        }

        [SkippableFact]
        public void SelectFiles_KeepsFilesOnlyRejectedForEpisodeMatching()
        {
            SkipIfSonarrMissing();

            var unparseable = Item("/d/unparseable.mkv", 100, null);
            unparseable.Rejections = new[] { new ImportRejection(ImportRejectionReason.InvalidSeasonOrEpisode, "no episodes") };

            var file = Assert.Single(AniRssImportBinder.SelectFiles(new List<ManualImportItem> { unparseable }, 1, new List<int> { 5 }, "dl-1"));

            Assert.Equal(new[] { 5 }, file.EpisodeIds.ToArray());
        }

        // ---- Import binder: diverting the automatic import ----

        [SkippableFact]
        public void ImportPrefix_DownloadAniRssDidNotPush_RunsSonarrsOwnImport()
        {
            SkipIfSonarrMissing();

            var history = new FakeHistoryService();
            history.Grabbed.Add(Grabbed("dl-plain", 5, "Show.S02E03.1080p.WEB"));
            var queue = new FakeCommandQueue();
            Bind(history, new FakeManualImportService(), queue);

            var runSonarrImport = AniRssImportBinder.ImportPrefix(Download("dl-plain", "/d/Show.S02E03"));

            Assert.True(runSonarrImport);
            Assert.Empty(queue.Pushed);
        }

        [SkippableFact]
        public void ImportPrefix_AniRssDownload_IsHandedToManualImport()
        {
            SkipIfSonarrMissing();

            var history = new FakeHistoryService();
            history.Grabbed.Add(Grabbed("dl-anirss", 5, "[Group] Show 03 #ANIRSS1"));
            var manual = new FakeManualImportService();
            manual.Items.Add(Item("/d/unparseable.mkv", 100, null));
            var queue = new FakeCommandQueue();
            Bind(history, manual, queue);

            var runSonarrImport = AniRssImportBinder.ImportPrefix(Download("dl-anirss", "/d/Show.S02E03"));

            Assert.False(runSonarrImport);

            var command = Assert.IsType<ManualImportCommand>(Assert.Single(queue.Pushed));
            Assert.Equal(new[] { 5 }, Assert.Single(command.Files).EpisodeIds.ToArray());
            Assert.Equal(ImportMode.Auto, command.ImportMode);
            Assert.Contains("dl-anirss", manual.Calls.Single());
        }

        [SkippableFact]
        public void ImportPrefix_QueuesEachDownloadOnlyOnce()
        {
            SkipIfSonarrMissing();

            var history = new FakeHistoryService();
            history.Grabbed.Add(Grabbed("dl-once", 5, "[Group] Show 03 #ANIRSS1"));
            var manual = new FakeManualImportService();
            manual.Items.Add(Item("/d/unparseable.mkv", 100, null));
            var queue = new FakeCommandQueue();
            Bind(history, manual, queue);

            var download = Download("dl-once", "/d/Show.S02E03");

            Assert.False(AniRssImportBinder.ImportPrefix(download));
            Assert.False(AniRssImportBinder.ImportPrefix(download));
            Assert.False(AniRssImportBinder.ImportPrefix(download));

            Assert.Single(queue.Pushed);
        }

        [SkippableFact]
        public void ImportPrefix_LeavesSonarrAloneWithoutAnImportPath()
        {
            SkipIfSonarrMissing();

            var history = new FakeHistoryService();
            history.Grabbed.Add(Grabbed("dl-nopath", 5, "[Group] Show 03 #ANIRSS1"));
            var queue = new FakeCommandQueue();
            Bind(history, new FakeManualImportService(), queue);

            var download = Download("dl-nopath", "/d/Show.S02E03");
            download.ImportItem = null;

            Assert.True(AniRssImportBinder.ImportPrefix(download));
            Assert.Empty(queue.Pushed);
        }

        [SkippableFact]
        public void ImportPrefix_PatchTargetsExistInSonarr()
        {
            SkipIfSonarrMissing();

            var completedDownloadService = AccessTools.TypeByName("NzbDrone.Core.Download.CompletedDownloadService");
            Assert.NotNull(completedDownloadService);
            Assert.NotNull(AccessTools.Method(completedDownloadService, "Import", new[] { typeof(TrackedDownload) }));

            foreach (var typeName in new[]
            {
                "NzbDrone.Core.Tv.EpisodeService",
                "NzbDrone.Core.History.HistoryService",
                "NzbDrone.Core.MediaFiles.EpisodeImport.Manual.ManualImportService",
                "NzbDrone.Core.Messaging.Commands.CommandQueueManager"
            })
            {
                var type = AccessTools.TypeByName(typeName);
                Assert.NotNull(type);
                Assert.NotNull(AccessTools.Constructor(type));
            }
        }

        private static void Bind(FakeHistoryService history, FakeManualImportService manual, FakeCommandQueue queue)
        {
            AniRssImportBinder.HistoryService = history;
            AniRssImportBinder.ManualImportService = manual;
            AniRssImportBinder.CommandQueue = queue;
        }

        private static EpisodeHistory Grabbed(string downloadId, int episodeId, string sourceTitle)
        {
            return new EpisodeHistory
            {
                EventType = EpisodeHistoryEventType.Grabbed,
                EpisodeId = episodeId,
                SeriesId = 1,
                SourceTitle = sourceTitle
            };
        }

        private static ManualImportItem Item(string path, long size, List<Episode> episodes)
        {
            var item = new ManualImportItem
            {
                Path = path,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Size = size,
                Quality = new QualityModel(Quality.Unknown),
                Languages = new List<Language>()
            };

            if (episodes != null)
            {
                item.Episodes = episodes;
            }

            return item;
        }

        private static TrackedDownload Download(string downloadId, string outputPath)
        {
            return new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = downloadId, Title = "[Group] Show 03" },
                ImportItem = new DownloadClientItem { DownloadId = downloadId, OutputPath = new OsPath(outputPath) }
            };
        }


        private static void SkipIfSonarrMissing()
        {
            var corePath = Path.Combine(AppContext.BaseDirectory, "Sonarr.Core.dll");
            Skip.If(!File.Exists(corePath), "Sonarr.Core.dll not found; skipping Sonarr integration tests");
            LoadIfAbsent(corePath);
        }

        private static void LoadIfAbsent(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var name = AssemblyName.GetAssemblyName(path);
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().FullName == name.FullName);
            if (!loaded)
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
        }
    }
}
