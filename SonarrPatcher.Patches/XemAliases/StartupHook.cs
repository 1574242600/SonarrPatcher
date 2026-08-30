using SonarrPatcher.Common;
using SonarrPatcher.Patches.XemAliases;

internal class StartupHook
{
    public static void Initialize()
    {
        SonarrDependencyLoader.EnsureLoaded("0Harmony.dll", "Sonarr.Common.dll");
        new XemAliases().Run();
    }
}
