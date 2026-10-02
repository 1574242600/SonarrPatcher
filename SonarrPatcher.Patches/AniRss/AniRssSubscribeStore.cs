using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Extensions;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// Owns the subscribe file (<see cref="AniRssPatch.SubscribeFile"/>) as a file: its
    /// JSON shape, the merge of command-carried subscriptions into it, and the removal of
    /// the subscriptions a pass has nothing left to do for.
    /// <para>
    /// Two entry points, one per direction of the pass: <see cref="LoadFor"/> answers "what
    /// should this pass run on" (persisting the command's payload first when it carries
    /// one) and <see cref="DropFinished"/> answers "write the survivors back". Everything
    /// about reading, writing and merging the file stops here, so the executor only deals
    /// with the list it is handed.
    /// </para>
    /// </summary>
    internal sealed class AniRssSubscribeStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly Logger _logger;

        public AniRssSubscribeStore(Logger logger)
        {
            _logger = logger;
        }

        private static string FilePath => AniRssPatch.SubscribeFile;

        /// <summary>
        /// The subscriptions to run the pass on: the ones the command carries - written to
        /// the file first, as the whole list or merged into it when the command updates -
        /// or the file on disk when the command carries none. Returns null when there is
        /// nothing to process.
        /// </summary>
        public List<AniRssSubscribeItem> LoadFor(AniRssCommand message)
        {
            if (message.Subscribe != null && message.Subscribe.Count > 0)
            {
                return PersistSubscriptions(message);
            }

            if (FilePath.IsNullOrWhiteSpace())
            {
                _logger.Warn("subscribe file path is empty, skipping execution.");
                return null;
            }

            if (!File.Exists(FilePath))
            {
                _logger.Warn("subscribe file not found: {0}", FilePath);
                return null;
            }

            return ReadFile(FilePath);
        }

        /// <summary>
        /// Drops the subscriptions that have nothing left to do from the subscribe file
        /// (see <see cref="AniRssSourcePolicy.IsFinished"/>): an entry that only re-lists a
        /// season already complete from the top feed would be fetched in full on every pass
        /// from then on. A pass that finished nothing writes nothing.
        /// </summary>
        public void DropFinished(List<AniRssSubscribeItem> config, List<AniRssSubscribeItem> finished)
        {
            if (finished.Count == 0)
            {
                return;
            }

            config.RemoveAll(finished.Contains);
            WriteFile(FilePath, config);
        }

        /// <summary>
        /// Writes the subscriptions carried by the command to the subscribe file, and
        /// returns the list to run the pass on: the command's own list, or - when the
        /// command updates - what it merged into the file. An update may be the first
        /// command that ever writes the file, so a file that cannot be read is merged
        /// into as if it were empty.
        /// </summary>
        private List<AniRssSubscribeItem> PersistSubscriptions(AniRssCommand message)
        {
            if (!message.Update)
            {
                WriteFile(FilePath, message.Subscribe);
                return message.Subscribe;
            }

            List<AniRssSubscribeItem> existing;
            try
            {
                existing = ReadFile(FilePath);
            }
            catch (Exception ex)
            {
                _logger.Warn("{0}, merging into an empty subscription list.", ex.Message);
                existing = new List<AniRssSubscribeItem>();
            }

            var merged = MergeSubscriptions(existing, message.Subscribe);
            WriteFile(FilePath, merged);

            return merged;
        }

        /// <summary>
        /// Merges the command's subscriptions into the ones already in the file: an
        /// entry replaces the file's entry for the same series and season, the entries
        /// the command does not carry are kept, and the entries the file does not have
        /// are appended. A subscription is identified by the series and season it
        /// watches - the label is decoration and the feeds are content, so neither is
        /// part of the identity, and a replaced entry keeps its place in the file.
        /// </summary>
        internal static List<AniRssSubscribeItem> MergeSubscriptions(List<AniRssSubscribeItem> existing, List<AniRssSubscribeItem> incoming)
        {
            var merged = new List<AniRssSubscribeItem>(existing);

            foreach (var item in incoming)
            {
                var index = merged.FindIndex(e => e.TvdbId == item.TvdbId && e.Season == item.Season);
                if (index >= 0)
                {
                    merged[index] = item;
                }
                else
                {
                    merged.Add(item);
                }
            }

            return merged;
        }

        private static List<AniRssSubscribeItem> ReadFile(string configPath)
        {
            try
            {
                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<List<AniRssSubscribeItem>>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to read subscribe file " + configPath + ": " + ex.Message, ex);
            }
        }

        private void WriteFile(string configPath, List<AniRssSubscribeItem> config)
        {
            if (configPath.IsNullOrWhiteSpace())
            {
                _logger.Warn("subscribe file path is empty, cannot persist subscribe config.");
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(configPath);
                if (directory.IsNotNullOrWhiteSpace())
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(configPath, json);
                _logger.Info("subscribe config written to {0}", configPath);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to write subscribe config to {0}: {1}", configPath, ex.Message);
            }
        }
    }
}
