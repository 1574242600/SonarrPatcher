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
    /// in their static constructors — <see cref="Log"/> is created automatically from
    /// it, so a derived patch never assigns the logger itself; <see cref="Run"/> owns
    /// the Harmony instance so derived patches never construct one themselves, and logs
    /// (instead of rethrowing) any failure so Sonarr can still boot if a patch fails.
    /// <para>
    /// Contract: this source is linked into every patch assembly (see the csproj globs)
    /// and each assembly contains exactly one derived patch, so the static
    /// <see cref="Name"/>/<see cref="Log"/> are per-assembly, not shared across patches.
    /// Keep each patch project at a single derived class.
    /// </para>
    /// </summary>
    public abstract class Patch
    {
        private static string _name;
        private static ILogger _log;

        /// <summary>
        /// Patch name, also used as the logger prefix. Set by each derived patch in
        /// its static constructor; assigning it also (re)creates <see cref="Log"/>
        /// bound to the new value.
        /// </summary>
        public static string Name
        {
            get => _name;
            protected set
            {
                _name = value;
                _log = new Logger(value);
            }
        }

        /// <summary>
        /// Harmony id used to build the <see cref="Harmony"/> instance for this patch,
        /// generated from <see cref="Name"/>.
        /// </summary>
        public static string PatchId => "tv.sonarr." + Name.ToLowerInvariant();

        /// <summary>
        /// Per-patch logger prefixed with <see cref="Name"/>, created automatically
        /// when <see cref="Name"/> is assigned by the derived patch's static constructor.
        /// Assumes the derived patch type has been initialized (its static constructor
        /// has run) before being read.
        /// </summary>
        internal static ILogger Log => _log;

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
