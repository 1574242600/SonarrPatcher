using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.EpisodeImport.Manual;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

namespace SonarrPatcher.Tests
{
    /// <summary>
    /// Minimal stubs for the Sonarr services the AniRss import binder talks to. Only the
    /// members the binder calls are implemented; everything else throws, which keeps the
    /// stubs honest about what the patch actually uses.
    /// </summary>
    internal sealed class FakeHistoryService : IHistoryService
    {
        public List<EpisodeHistory> Grabbed = new List<EpisodeHistory>();
        public List<string> DownloadIds = new List<string>();

        public List<EpisodeHistory> FindByDownloadId(string downloadId)
        {
            DownloadIds.Add(downloadId);
            return Grabbed;
        }

        public PagingSpec<EpisodeHistory> Paged(PagingSpec<EpisodeHistory> pagingSpec, int[] languages, int[] qualities) => throw new NotImplementedException();
        public EpisodeHistory MostRecentForEpisode(int episodeId) => throw new NotImplementedException();
        public List<EpisodeHistory> FindByEpisodeId(int episodeId) => throw new NotImplementedException();
        public EpisodeHistory MostRecentForDownloadId(string downloadId) => throw new NotImplementedException();
        public EpisodeHistory Get(int historyId) => throw new NotImplementedException();
        public List<EpisodeHistory> GetBySeries(int seriesId, EpisodeHistoryEventType? eventType) => throw new NotImplementedException();
        public List<EpisodeHistory> GetBySeason(int seriesId, int seasonNumber, EpisodeHistoryEventType? eventType) => throw new NotImplementedException();
        public List<EpisodeHistory> Find(string downloadId, EpisodeHistoryEventType eventType) => throw new NotImplementedException();
        public string FindDownloadId(EpisodeImportedEvent trackedDownload) => throw new NotImplementedException();
        public List<EpisodeHistory> Since(DateTime date, EpisodeHistoryEventType? eventType) => throw new NotImplementedException();
    }

    /// <summary>Returns a canned list of manual import items and records how it was asked.</summary>
    internal sealed class FakeManualImportService : IManualImportService
    {
        public List<ManualImportItem> Items = new List<ManualImportItem>();
        public List<string> Calls = new List<string>();

        public List<ManualImportItem> GetMediaFiles(string path, string downloadId, int? seriesId, bool filterExistingFiles)
        {
            Calls.Add(path + " | " + downloadId + " | " + seriesId + " | " + filterExistingFiles);
            return Items;
        }

        public List<ManualImportItem> GetMediaFiles(int seriesId, int? seasonNumber) => throw new NotImplementedException();
        public ManualImportItem ReprocessItem(string path, string downloadId, int seriesId, int? seasonNumber, List<int> episodeIds, string releaseGroup, QualityModel quality, List<Language> languages, int indexerFlags, ReleaseType releaseType) => throw new NotImplementedException();
    }

    /// <summary>Collects the commands the binder pushes instead of running them.</summary>
    internal sealed class FakeCommandQueue : IManageCommandQueue
    {
        public List<Command> Pushed = new List<Command>();

        public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
            where TCommand : Command
        {
            Pushed.Add(command);
            return new CommandModel { Name = command.Name };
        }

        public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotImplementedException();
        public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
        public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
        public List<CommandModel> All() => throw new NotImplementedException();
        public CommandModel Get(int id) => throw new NotImplementedException();
        public List<CommandModel> GetStarted() => throw new NotImplementedException();
        public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
        public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
        public void Start(CommandModel command) => throw new NotImplementedException();
        public void Complete(CommandModel command, string message) => throw new NotImplementedException();
        public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
        public void Requeue() => throw new NotImplementedException();
        public void Cancel(int id) => throw new NotImplementedException();
        public void CleanCommands() => throw new NotImplementedException();
    }
}
