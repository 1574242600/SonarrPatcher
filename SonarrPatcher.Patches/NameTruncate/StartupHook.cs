using SonarrPatcher.Common;
using SonarrPatcher.Patches.NameTruncate;

internal class StartupHook
{
    public static void Initialize()
    {
        SonarrDependencyLoader.EnsureLoaded("0Harmony.dll", "Sonarr.Common.dll");
        new NameTruncate().Run();
    }
}
