using SonarrPatcher.Common;
using SonarrPatcher.Patches.CustomParseRules;

internal class StartupHook
{
    public static void Initialize()
    {
        SonarrDependencyLoader.EnsureLoaded("0Harmony.dll", "Sonarr.Common.dll");
        new CustomParseRules().Run();
    }

    public static void InitializeForLoader()
    {
        new CustomParseRules().Run();
    }
}
