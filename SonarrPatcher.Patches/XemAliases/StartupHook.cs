using System;
using SonarrPatcher.Common;
using SonarrPatcher.Patches;

internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            XemAliases.Initialize();
        }
        catch (Exception ex)
        {
            new Logger("XemAliasesPatch").Error("StartupHook failed: " + ex);
        }
    }

    public static void InitializeForLoader()
    {
        try
        {
            XemAliases.InitializeForLoader();
        }
        catch (Exception ex)
        {
            new Logger("XemAliasesPatch").Error("StartupHook failed (loader): " + ex);
        }
    }
}
