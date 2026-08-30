using SonarrPatcher.Common;
using SonarrPatcher.Patches.NameTruncate;

internal class StartupHook
{
    public static void Initialize()
    {
        // The StartupHook runs before Sonarr's main entry point, so Sonarr.Core
        // may not be loaded yet; load it explicitly so the type can be resolved.
        SonarrDependencyLoader.EnsureLoaded("0Harmony.dll", "Sonarr.Common.dll", "Sonarr.Core.dll");
        new NameTruncate().Run();
    }
}
