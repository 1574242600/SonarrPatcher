public static class StartupHook
{
    public static bool Invoked;

    public static void Initialize()
    {
        Invoked = true;
    }
}
