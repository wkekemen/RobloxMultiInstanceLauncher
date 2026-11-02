namespace RobloxMultiLauncher.Services;

public static class AppPaths
{
    public static readonly string DataDir = Create();

    private static string Create()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxMultiLauncher");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
