public static class StartupHook
{
    public static bool Invoked;
    public static bool InvokedForLoader;

    public static void Initialize()
    {
        Invoked = true;
    }

    public static void InitializeForLoader()
    {
        InvokedForLoader = true;
    }
}
