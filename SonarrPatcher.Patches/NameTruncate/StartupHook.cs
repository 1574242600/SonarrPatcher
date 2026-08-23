using System;
using SonarrPatcher.Common;
using SonarrPatcher.Patches;

internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            NameTruncate.Initialize();
        }
        catch (Exception ex)
        {
            new Logger("NameTruncatePatch").Error("StartupHook failed: " + ex);
        }
    }

    public static void InitializeForLoader()
    {
        try
        {
            NameTruncate.InitializeForLoader();
        }
        catch (Exception ex)
        {
            new Logger("NameTruncatePatch").Error("StartupHook failed (loader): " + ex);
        }
    }
}
