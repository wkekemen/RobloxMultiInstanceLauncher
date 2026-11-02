namespace RobloxMultiLauncher.Models;

public enum MacroEventType
{
    KeyDown,
    KeyUp,
    LeftMouseDown,
    LeftMouseUp,
    RightMouseDown,
    RightMouseUp,
    MiddleMouseDown,
    MiddleMouseUp,
    XButton1Down,
    XButton1Up,
    XButton2Down,
    XButton2Up
}

public sealed class MacroEvent
{
    public long OffsetMilliseconds { get; set; }
    public MacroEventType Type { get; set; }
    public int VirtualKey { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class MacroRecording
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My macro";
    public long DurationMilliseconds { get; set; } = -1;
    public List<MacroEvent> Events { get; set; } = new();
}

public sealed class MacroLibraryItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int EventCount { get; init; }
    public string DurationText { get; init; } = "";
}
