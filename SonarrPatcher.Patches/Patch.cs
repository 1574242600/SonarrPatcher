using HarmonyLib;
using SonarrPatcher.Common;

namespace SonarrPatcher.Patches
{
    /// <summary>
    /// Base class for all SonarrPatcher patches. Provides the patch name (also
    /// used as the logger prefix), the Harmony patch id (<see cref="PatchId"/>),
    /// a per-patch logger and the standard ShouldPatch/Apply lifecycle mirroring
    /// Sonarr's own RuntimePatchBase. <see cref="Run"/> owns the Harmony instance
    /// so derived patches never construct one themselves.
    /// </summary>
    internal abstract class Patch
    {
        protected Patch(string name)
        {
            Name = name;
            Log = new Logger(name);
        }

        public string Name { get; }

        /// <summary>
        /// Harmony id used to build the <see cref="Harmony"/> instance for this patch.
        /// </summary>
        public abstract string PatchId { get; }

        protected ILogger Log { get; }

        public virtual bool ShouldPatch() => true;

        protected abstract void Apply(Harmony harmony);

        public void Run()
        {
            if (ShouldPatch())
            {
                Apply(new Harmony(PatchId));
            }
        }
    }
}
