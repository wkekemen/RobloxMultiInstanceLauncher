namespace RobloxMultiLauncher.Services;

/// <summary>Tiny file + event logger. Never pass cookies or auth tickets to it.</summary>
public static class Logger
{
    private static readonly object Gate = new();
    public static string LogPath { get; } = Path.Combine(AppPaths.DataDir, "launcher.log");
    public static event Action<string>? LineWritten;

    static Logger()
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
        }
        catch { /* ignore */ }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        try { lock (Gate) File.AppendAllText(LogPath, line + Environment.NewLine); } catch { /* ignore */ }
        LineWritten?.Invoke(line);
    }
}
