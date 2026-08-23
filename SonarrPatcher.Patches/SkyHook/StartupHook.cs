using System;
using SonarrPatcher.Common;
using SonarrPatcher.Patches;

internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            SkyHook.Initialize();
        }
        catch (Exception ex)
        {
            new Logger("SkyHookPatch").Error("StartupHook failed: " + ex);
        }
    }

    public static void InitializeForLoader()
    {
        try
        {
            SkyHook.InitializeForLoader();
        }
        catch (Exception ex)
        {
            new Logger("SkyHookPatch").Error("StartupHook failed (loader): " + ex);
        }
    }
}
