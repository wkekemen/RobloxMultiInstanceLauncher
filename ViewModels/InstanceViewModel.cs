namespace RobloxMultiLauncher.ViewModels;

public sealed class InstanceViewModel : ObservableObject
{
    private string _heading = "", _details = "";
    private IntPtr _startupSizeAttemptedHandle;

    public InstanceViewModel(int pid) => Pid = pid;

    public int Pid { get; }
    public IntPtr Hwnd { get; private set; }
    public bool HasWindow => Hwnd != IntPtr.Zero;
    public TimeSpan Uptime { get; private set; }

    public bool MarkStartupSizeAttempted(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _startupSizeAttemptedHandle) return false;
        _startupSizeAttemptedHandle = hwnd;
        return true;
    }

    public string Heading { get => _heading; private set => Set(ref _heading, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }

    public void Update(IntPtr hwnd, string title, string? owner, long memoryBytes, TimeSpan uptime, bool minimized)
    {
        if (Hwnd != hwnd)
        {
            Hwnd = hwnd;
            OnPropertyChanged(nameof(Hwnd));
            OnPropertyChanged(nameof(HasWindow));
        }
        Uptime = uptime;
        Heading = $"{owner ?? "Not started by this launcher"}  ·  PID {Pid}";
        string state = !HasWindow ? "Background (no window)" : minimized ? "Minimized" : "Running";
        string up = $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
        Details = $"{state}  ·  {memoryBytes / 1024 / 1024} MB  ·  up {up}" + (title.Length > 0 ? $"  ·  \"{title}\"" : "");
    }
}
