using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// Command executed by the scheduler (or manually via /api/v3/command).
    /// When <see cref="Subscribe"/> is provided it is persisted to the
    /// <c>ANIRSS_SUBSCRIBE_FILE</c> file, and the resulting list is the one the pass
    /// runs on.
    /// </summary>
    public class AniRssCommand : Command
    {
        /// <summary>
        /// Subscriptions carried by the command: entries to merge into the subscribe
        /// file by default, or the file itself with <see cref="Update"/> turned off.
        /// </summary>
        public List<AniRssSubscribeItem> Subscribe { get; set; }

        /// <summary>
        /// True (default): <see cref="Subscribe"/> is merged into the subscribe file -
        /// an entry replaces the file's entry for the same series and season, the
        /// entries the command does not carry are kept, and the ones the file does not
        /// have are appended. This is the mode for a caller that holds the
        /// subscriptions it changed rather than the whole file.
        /// False: <see cref="Subscribe"/> replaces the file, dropping everything the
        /// command does not carry.
        /// </summary>
        public bool Update { get; set; } = true;
    }
}
