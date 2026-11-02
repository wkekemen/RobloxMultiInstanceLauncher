namespace RobloxMultiLauncher.Services;

public static class RobloxLocator
{
    public const string ExeName = "RobloxPlayerBeta.exe";

    public static string? AutoDetect()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions"),
        };
        foreach (var root in roots)
        {
            var exe = NewestIn(root);
            if (exe != null) return exe;
        }
        return null;
    }

    /// <summary>Accepts blank (auto-detect), the exe itself, a version-* folder, or a Versions folder.</summary>
    public static string? Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return AutoDetect();
        var p = configured.Trim().Trim('"');
        if (File.Exists(p)) return p;
        if (Directory.Exists(p))
        {
            var direct = Path.Combine(p, ExeName);
            if (File.Exists(direct)) return direct;
            return NewestIn(p);
        }
        return null;
    }

    public static string? VersionOf(string? exePath) =>
        string.IsNullOrEmpty(exePath) ? null : Path.GetFileName(Path.GetDirectoryName(exePath));

    private static string? NewestIn(string versionsDir)
    {
        try
        {
            if (!Directory.Exists(versionsDir)) return null;
            return Directory.GetDirectories(versionsDir, "version-*")
                .Select(d => Path.Combine(d, ExeName))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not scan {versionsDir}: {ex.Message}");
            return null;
        }
    }
}
