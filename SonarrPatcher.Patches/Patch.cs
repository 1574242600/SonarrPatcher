using System;
using HarmonyLib;
using SonarrPatcher.Common;

namespace SonarrPatcher.Patches
{
    /// <summary>
    /// Base class for all SonarrPatcher patches. Exposes the static patch
    /// <see cref="Name"/> (also used as the logger prefix), the Harmony patch id
    /// (<see cref="PatchId"/>, derived from <see cref="Name"/>), a per-patch static
    /// logger (<see cref="Log"/>) and the standard ShouldPatch/Apply lifecycle
    /// mirroring Sonarr's own RuntimePatchBase. Derived patches set <see cref="Name"/>
    /// and <see cref="Log"/> in their static constructors; <see cref="Run"/> owns the
    /// Harmony instance so derived patches never construct one themselves, and logs
    /// (instead of rethrowing) any failure so Sonarr can still boot if a patch fails.
    /// </summary>
    public abstract class Patch
    {
        /// <summary>
        /// Patch name, also used as the logger prefix. Set by each derived patch in
        /// its static constructor.
        /// </summary>
        public static string Name { get; protected set; }

        /// <summary>
        /// Harmony id used to build the <see cref="Harmony"/> instance for this patch,
        /// generated from <see cref="Name"/>.
        /// </summary>
        public static string PatchId => "tv.sonarr." + Name.ToLowerInvariant();

        /// <summary>
        /// Per-patch logger prefixed with <see cref="Name"/>. Set by each derived
        /// patch in its static constructor.
        /// </summary>
        internal static ILogger Log;

        public virtual bool ShouldPatch() => true;

        protected abstract void Apply(Harmony harmony);

        public void Run()
        {
            try
            {
                if (ShouldPatch())
                {
                    Apply(new Harmony(PatchId));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to apply patch: " + ex);
            }
        }
    }
}
