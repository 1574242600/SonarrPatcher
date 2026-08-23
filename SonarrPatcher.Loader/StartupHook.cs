using System;
using SonarrPatcher.Common;
using SonarrPatcher;

internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            Loader.LoadAll();
        }
        catch (Exception ex)
        {
            new Logger("SonarrPatcher.Loader").Error("Loader failed: " + ex);
        }
    }
}
