namespace RobloxMultiLauncher.Models;

public sealed class AppSettings
{
    /// <summary>Empty = auto-detect. May be RobloxPlayerBeta.exe, a version-* folder, or a Versions folder.</summary>
    public string RobloxPath { get; set; } = "";
    public bool AttemptMultiInstance { get; set; } = true;
    public int LaunchTimeoutSeconds { get; set; } = 60;
    public string PlaceInput { get; set; } = "";
    public string JobId { get; set; } = "";
    public string BackgroundColor { get; set; } = "#0F1117";
    public string PanelColor { get; set; } = "#151822";
    public string CardColor { get; set; } = "#1B1F2C";
    public string AccentColor { get; set; } = "#5B8CFF";
    public string CustomLogoPath { get; set; } = "";
    public string MonitorDeviceName { get; set; } = "";
    public int SessionsPerRow { get; set; } = 2;
    public bool AutoArrangeWindows { get; set; } = true;
    public int MacroRepeatCount { get; set; } = 1;
    public int LiveActivityColumns { get; set; } = 2;
    public string MacroRecordHotkey { get; set; } = "Ctrl+Alt+F5";
    public string MacroPlayHotkey { get; set; } = "Ctrl+Alt+F6";
    public string MacroPauseHotkey { get; set; } = "Ctrl+Alt+F7";
    public string MacroStopHotkey { get; set; } = "Ctrl+Alt+F8";

    // Recorded only after two launcher-started clients were observed running at the same time.
    public string? VerifiedRobloxVersion { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
}
