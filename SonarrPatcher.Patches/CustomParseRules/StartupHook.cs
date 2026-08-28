using System;
using SonarrPatcher.Common;
using SonarrPatcher.Patches;

internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            CustomParseRules.Initialize();
        }
        catch (Exception ex)
        {
            new Logger("CustomParseRules").Error("StartupHook failed: " + ex);
        }
    }

    public static void InitializeForLoader()
    {
        try
        {
            CustomParseRules.InitializeForLoader();
        }
        catch (Exception ex)
        {
            new Logger("CustomParseRules").Error("StartupHook failed (loader): " + ex);
        }
    }
}
