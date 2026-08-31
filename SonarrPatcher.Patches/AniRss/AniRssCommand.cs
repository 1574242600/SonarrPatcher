using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace SonarrPatcher.Patches.AniRss
{
    /// <summary>
    /// Command executed by the scheduler (or manually via /api/v3/command).
    /// When <see cref="Subscribe"/> is provided it is used instead of the
    /// <c>ANIRSS_SUBSCRIBE_FILE</c> file and is persisted back to that file.
    /// </summary>
    public class AniRssCommand : Command
    {
        public List<AniRssSubscribeItem> Subscribe { get; set; }
    }
}
