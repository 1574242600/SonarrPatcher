using HarmonyLib;
using SonarrPatcher.Common;

namespace SonarrPatcher.Patches
{
    /// <summary>
    /// Base class for all SonarrPatcher patches. Provides the patch name (also
    /// used as the logger prefix), a per-patch logger and the standard
    /// ShouldPatch/Apply lifecycle mirroring Sonarr's own RuntimePatchBase.
    /// </summary>
    internal abstract class Patch
    {
        protected Patch(string name)
        {
            Name = name;
            Log = new Logger(name);
        }

        public string Name { get; }

        protected ILogger Log { get; }

        public virtual bool ShouldPatch() => true;

        protected abstract void Apply(Harmony harmony);

        public void Run(Harmony harmony)
        {
            if (ShouldPatch())
            {
                Apply(harmony);
            }
        }
    }
}
