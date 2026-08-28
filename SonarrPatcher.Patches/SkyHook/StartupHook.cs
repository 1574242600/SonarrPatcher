using SonarrPatcher.Common;
using SonarrPatcher.Patches.SkyHook;

internal class StartupHook
{
    public static void Initialize()
    {
        SonarrDependencyLoader.EnsureLoaded("0Harmony.dll", "Sonarr.Common.dll");
        new SkyHook().Run();
    }

    public static void InitializeForLoader()
    {
        new SkyHook().Run();
    }
}
