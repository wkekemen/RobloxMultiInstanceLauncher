using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RobloxMultiLauncher.Models;

namespace RobloxMultiLauncher.Services;

public sealed class MacroService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const uint LlkhfInjected = 0x10;
    private const uint LlmhfInjected = 0x01;
    private const int MaxEvents = 250_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string MacroDirectory => Path.Combine(AppPaths.DataDir, "Macros");

    private readonly object _sync = new();
    private readonly List<MacroEvent> _events = new();
    private readonly HashSet<uint> _pressedKeys = new();
    private readonly HashSet<uint> _suppressedKeys = new();
    private (uint Modifiers, uint VirtualKey)[] _ignoredHotkeys = Array.Empty<(uint, uint)>();
    private readonly HashSet<uint> _recordedKeysDown = new();
    private readonly Dictionary<MacroEventType, (int X, int Y)> _recordedMouseButtons = new();
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly Stopwatch _recordingClock = new();
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private bool _isRecording;
    private bool _eventLimitReached;
    private bool _disposed;

    public MacroService()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void SetIgnoredHotkeys(IEnumerable<(uint Modifiers, uint VirtualKey)> hotkeys)
    {
        lock (_sync) _ignoredHotkeys = hotkeys.ToArray();
    }

    public bool IsRecording
    {
        get { lock (_sync) return _isRecording; }
    }

    public bool EventLimitReached
    {
        get { lock (_sync) return _eventLimitReached; }
    }

    public int EventCount
    {
        get { lock (_sync) return _events.Count; }
    }

    public long ElapsedMilliseconds
    {
        get { lock (_sync) return _recordingClock.ElapsedMilliseconds; }
    }

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_isRecording) throw new InvalidOperationException("A recording is already active.");
            _events.Clear();
            _pressedKeys.Clear();
            _suppressedKeys.Clear();
            _recordedKeysDown.Clear();
            _recordedMouseButtons.Clear();
            _eventLimitReached = false;
            _recordingClock.Restart();
            _isRecording = true;
        }

        try
        {
            IntPtr module = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
            if (_keyboardHook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the global keyboard hook.");

            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the global mouse hook.");
        }
        catch
        {
            StopRecording();
            throw;
        }
    }

    public void PauseRecording()
    {
        lock (_sync)
        {
            if (!_isRecording) return;
            CloseOpenInputsLocked();
            _isRecording = false;
            _recordingClock.Stop();
        }
    }

    public void ResumeRecording()
    {
        lock (_sync)
        {
            if (_isRecording || _eventLimitReached) return;
            _isRecording = true;
            _recordingClock.Start();
        }
    }

    public MacroRecording StopRecording(string name = "My macro")
    {
        lock (_sync)
        {
            CloseOpenInputsLocked();
            _isRecording = false;
            _recordingClock.Stop();
        }

        Unhook();
        return Snapshot(name);
    }

    public MacroRecording Snapshot(string name)
    {
        lock (_sync)
        {
            return new MacroRecording
            {
                Name = name,
                DurationMilliseconds = _recordingClock.ElapsedMilliseconds,
                Events = _events.Select(item => new MacroEvent
                {
                    OffsetMilliseconds = item.OffsetMilliseconds,
                    Type = item.Type,
                    VirtualKey = item.VirtualKey,
                    X = item.X,
                    Y = item.Y
                }).ToList()
            };
        }
    }

    public MacroRecording Save(MacroRecording recording)
    {
        Validate(recording);
        Directory.CreateDirectory(MacroDirectory);
        if (string.IsNullOrWhiteSpace(recording.Id) || recording.Id.Length != 32 ||
            !Guid.TryParseExact(recording.Id, "N", out _))
            recording.Id = Guid.NewGuid().ToString("N");

        string path = Path.Combine(MacroDirectory, recording.Id + ".json");
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(recording, Json));
        File.Move(temporaryPath, path, overwrite: true);
        return recording;
    }

    public IReadOnlyList<MacroLibraryItem> GetSavedMacros()
    {
        if (!Directory.Exists(MacroDirectory)) return Array.Empty<MacroLibraryItem>();

        var macros = new List<MacroLibraryItem>();
        foreach (string path in Directory.EnumerateFiles(MacroDirectory, "*.json"))
        {
            try
            {
                MacroRecording recording = ReadRecording(path);
                macros.Add(new MacroLibraryItem
                {
                    Id = recording.Id,
                    Name = recording.Name,
                    EventCount = recording.Events.Count,
                    DurationText = FormatDuration(recording.DurationMilliseconds)
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       JsonException or ArgumentException)
            {
                Logger.Warn($"Skipping invalid saved macro '{Path.GetFileName(path)}': {ex.Message}");
            }
        }
        return macros.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public MacroRecording Load(string id)
    {
        if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("The selected recording has an invalid ID.");
        return ReadRecording(Path.Combine(MacroDirectory, id + ".json"));
    }

    public static string FormatDuration(long milliseconds)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(milliseconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";
    }

    public static void Send(MacroEvent item)
    {
        INPUT[] inputs;
        switch (item.Type)
        {
            case MacroEventType.KeyDown:
            case MacroEventType.KeyUp:
                inputs = new[]
                {
                    new INPUT
                    {
                        Type = InputKeyboard,
                        Data = new INPUTUNION
                        {
                            Keyboard = new KEYBDINPUT
                            {
                                VirtualKey = (ushort)item.VirtualKey,
                                Flags = item.Type == MacroEventType.KeyUp ? KeyeventfKeyup : 0
                            }
                        }
                    }
                };
                break;
            default:
                uint buttonFlag = item.Type switch
                {
                    MacroEventType.LeftMouseDown => MouseeventfLeftdown,
                    MacroEventType.LeftMouseUp => MouseeventfLeftup,
                    MacroEventType.RightMouseDown => MouseeventfRightdown,
                    MacroEventType.RightMouseUp => MouseeventfRightup,
                    MacroEventType.MiddleMouseDown => MouseeventfMiddledown,
                    MacroEventType.MiddleMouseUp => MouseeventfMiddleup,
                    MacroEventType.XButton1Down or MacroEventType.XButton2Down => MouseeventfXdown,
                    MacroEventType.XButton1Up or MacroEventType.XButton2Up => MouseeventfXup,
                    _ => throw new InvalidDataException("The recording contains an unsupported input event.")
                };
                uint buttonData = item.Type is MacroEventType.XButton1Down or MacroEventType.XButton1Up
                    ? Xbutton1
                    : item.Type is MacroEventType.XButton2Down or MacroEventType.XButton2Up
                        ? Xbutton2
                        : 0;
                inputs = new[]
                {
                    CreateMouseInput(item.X, item.Y, MouseeventfMove | MouseeventfAbsolute | MouseeventfVirtualdesk),
                    CreateMouseInput(item.X, item.Y, buttonFlag, buttonData)
                };
                break;
        }

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not replay an input event.");
    }

    private static INPUT CreateMouseInput(int x, int y, uint flags, uint mouseData = 0)
    {
        int left = GetSystemMetrics(SmXvirtualscreen);
        int top = GetSystemMetrics(SmYvirtualscreen);
        int width = Math.Max(1, GetSystemMetrics(SmCxvirtualscreen) - 1);
        int height = Math.Max(1, GetSystemMetrics(SmCyvirtualscreen) - 1);
        int absoluteX = (int)Math.Clamp(((long)x - left) * 65535 / width, 0, 65535);
        int absoluteY = (int)Math.Clamp(((long)y - top) * 65535 / height, 0, 65535);
        return new INPUT
        {
            Type = InputMouse,
            Data = new INPUTUNION
            {
                Mouse = new MOUSEINPUT { X = absoluteX, Y = absoluteY, MouseData = mouseData, Flags = flags }
            }
        };
    }

    private static MacroRecording ReadRecording(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The saved recording could not be found.", path);
        MacroRecording? recording = JsonSerializer.Deserialize<MacroRecording>(File.ReadAllText(path));
        if (recording == null) throw new InvalidDataException($"The recording file '{Path.GetFileName(path)}' is empty or invalid.");
        Validate(recording);
        return recording;
    }

    private static void Validate(MacroRecording recording)
    {
        if (string.IsNullOrWhiteSpace(recording.Name))
            throw new InvalidDataException("A recording must have a name.");
        if (recording.Events == null || recording.Events.Count > MaxEvents)
            throw new InvalidDataException($"A recording must contain no more than {MaxEvents:N0} events.");
        long previousTime = -1;
        foreach (MacroEvent item in recording.Events)
        {
            if (item == null ||
                item.OffsetMilliseconds < previousTime ||
                item.OffsetMilliseconds < 0 ||
                !Enum.IsDefined(item.Type))
                throw new InvalidDataException("The recording contains an invalid event sequence.");
            if ((item.Type == MacroEventType.KeyDown || item.Type == MacroEventType.KeyUp) &&
                item.VirtualKey is < 1 or > 254)
                throw new InvalidDataException("The recording contains an invalid key.");
            previousTime = item.OffsetMilliseconds;
        }
        if (recording.DurationMilliseconds == -1)
            recording.DurationMilliseconds = Math.Max(0, previousTime);
        else if (recording.DurationMilliseconds < 0)
            throw new InvalidDataException("A recording cannot have a negative duration.");
        if (recording.Events.Count > 0 && recording.DurationMilliseconds < previousTime)
            throw new InvalidDataException("The recording duration cannot be shorter than its last event.");
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcAction)
        {
            try
            {
                var input = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((input.Flags & LlkhfInjected) == 0)
                {
                    int message = wParam.ToInt32();
                    MacroEventType? type = message switch
                    {
                        0x0100 or 0x0104 => MacroEventType.KeyDown,
                        0x0101 or 0x0105 => MacroEventType.KeyUp,
                        _ => null
                    };
                    if (type is MacroEventType eventType)
                        CaptureKey(input.VirtualKey, eventType == MacroEventType.KeyDown);
                }
            }
            catch (Exception ex) { Logger.Error("Could not record a keyboard event", ex); }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcAction)
        {
            try
            {
                var input = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((input.Flags & LlmhfInjected) == 0)
                {
                    MacroEventType? type = wParam.ToInt32() switch
                    {
                        0x0201 => MacroEventType.LeftMouseDown,
                        0x0202 => MacroEventType.LeftMouseUp,
                        0x0204 => MacroEventType.RightMouseDown,
                        0x0205 => MacroEventType.RightMouseUp,
                        0x0207 => MacroEventType.MiddleMouseDown,
                        0x0208 => MacroEventType.MiddleMouseUp,
                        0x020B when (input.MouseData >> 16) == Xbutton1 => MacroEventType.XButton1Down,
                        0x020C when (input.MouseData >> 16) == Xbutton1 => MacroEventType.XButton1Up,
                        0x020B when (input.MouseData >> 16) == Xbutton2 => MacroEventType.XButton2Down,
                        0x020C when (input.MouseData >> 16) == Xbutton2 => MacroEventType.XButton2Up,
                        _ => null
                    };
                    if (type is MacroEventType eventType)
                        CaptureMouse(eventType, input.Point.X, input.Point.Y);
                }
            }
            catch (Exception ex) { Logger.Error("Could not record a mouse event", ex); }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void CaptureMouse(MacroEventType type, int x, int y)
    {
        lock (_sync)
        {
            MacroEventType? matchingDown = type switch
            {
                MacroEventType.LeftMouseUp => MacroEventType.LeftMouseDown,
                MacroEventType.RightMouseUp => MacroEventType.RightMouseDown,
                MacroEventType.MiddleMouseUp => MacroEventType.MiddleMouseDown,
                MacroEventType.XButton1Up => MacroEventType.XButton1Down,
                MacroEventType.XButton2Up => MacroEventType.XButton2Down,
                _ => null
            };
            if (matchingDown is MacroEventType down && !_recordedMouseButtons.ContainsKey(down)) return;
            AddEventLocked(new MacroEvent { Type = type, X = x, Y = y });
        }
    }

    private void CaptureKey(uint virtualKey, bool isDown)
    {
        lock (_sync)
        {
            if (isDown)
            {
                _pressedKeys.Add(virtualKey);
                foreach (var hotkey in _ignoredHotkeys)
                {
                    if (hotkey.VirtualKey != virtualKey || !HasModifiersDown(hotkey.Modifiers)) continue;
                    SuppressKey(virtualKey);
                    if ((hotkey.Modifiers & 0x0001) != 0) SuppressPressed(0x12, 0xA4, 0xA5);
                    if ((hotkey.Modifiers & 0x0002) != 0) SuppressPressed(0x11, 0xA2, 0xA3);
                    if ((hotkey.Modifiers & 0x0004) != 0) SuppressPressed(0x10, 0xA0, 0xA1);
                    if ((hotkey.Modifiers & 0x0008) != 0) SuppressPressed(0x5B, 0x5C);
                    break;
                }

                if (!_suppressedKeys.Contains(virtualKey))
                {
                    if (_isRecording)
                    {
                        AddEventLocked(new MacroEvent { Type = MacroEventType.KeyDown, VirtualKey = (int)virtualKey });
                    }
                }
                return;
            }

            _pressedKeys.Remove(virtualKey);
            if (_suppressedKeys.Remove(virtualKey))
            {
                _recordedKeysDown.Remove(virtualKey);
                return;
            }
            if (_isRecording && _recordedKeysDown.Contains(virtualKey))
            {
                AddEventLocked(new MacroEvent { Type = MacroEventType.KeyUp, VirtualKey = (int)virtualKey });
            }
        }
    }

    private bool HasModifiersDown(uint modifiers)
    {
        bool alt = _pressedKeys.Contains(0x12) || _pressedKeys.Contains(0xA4) || _pressedKeys.Contains(0xA5);
        bool control = _pressedKeys.Contains(0x11) || _pressedKeys.Contains(0xA2) || _pressedKeys.Contains(0xA3);
        bool shift = _pressedKeys.Contains(0x10) || _pressedKeys.Contains(0xA0) || _pressedKeys.Contains(0xA1);
        bool windows = _pressedKeys.Contains(0x5B) || _pressedKeys.Contains(0x5C);
        return alt == ((modifiers & 0x0001) != 0) &&
            control == ((modifiers & 0x0002) != 0) &&
            shift == ((modifiers & 0x0004) != 0) &&
            windows == ((modifiers & 0x0008) != 0);
    }

    private void SuppressPressed(params uint[] virtualKeys)
    {
        foreach (uint virtualKey in virtualKeys)
        {
            if (_pressedKeys.Contains(virtualKey)) SuppressKey(virtualKey);
        }
    }

    private void SuppressKey(uint virtualKey)
    {
        _suppressedKeys.Add(virtualKey);
        if (!_recordedKeysDown.Remove(virtualKey)) return;
        int index = _events.FindLastIndex(item =>
            item.Type == MacroEventType.KeyDown && item.VirtualKey == virtualKey);
        if (index >= 0) _events.RemoveAt(index);
    }

    private void AddEventLocked(MacroEvent item)
    {
        if (!_isRecording) return;
        int additionalRelease = item.Type switch
        {
            MacroEventType.KeyDown when !_recordedKeysDown.Contains((uint)item.VirtualKey) => 1,
            MacroEventType.LeftMouseDown when !_recordedMouseButtons.ContainsKey(MacroEventType.LeftMouseDown) => 1,
            MacroEventType.RightMouseDown when !_recordedMouseButtons.ContainsKey(MacroEventType.RightMouseDown) => 1,
            MacroEventType.MiddleMouseDown when !_recordedMouseButtons.ContainsKey(MacroEventType.MiddleMouseDown) => 1,
            MacroEventType.XButton1Down when !_recordedMouseButtons.ContainsKey(MacroEventType.XButton1Down) => 1,
            MacroEventType.XButton2Down when !_recordedMouseButtons.ContainsKey(MacroEventType.XButton2Down) => 1,
            _ => 0
        };
        int requiredCloseEvents = _recordedKeysDown.Count + _recordedMouseButtons.Count + additionalRelease;
        if (_events.Count + 1 + requiredCloseEvents > MaxEvents)
        {
            _eventLimitReached = true;
            _isRecording = false;
            _recordingClock.Stop();
            return;
        }
        item.OffsetMilliseconds = _recordingClock.ElapsedMilliseconds;
        _events.Add(item);
        switch (item.Type)
        {
            case MacroEventType.KeyDown:
                _recordedKeysDown.Add((uint)item.VirtualKey);
                break;
            case MacroEventType.KeyUp:
                _recordedKeysDown.Remove((uint)item.VirtualKey);
                break;
            case MacroEventType.LeftMouseDown:
            case MacroEventType.RightMouseDown:
            case MacroEventType.MiddleMouseDown:
            case MacroEventType.XButton1Down:
            case MacroEventType.XButton2Down:
                _recordedMouseButtons[item.Type] = (item.X, item.Y);
                break;
            case MacroEventType.LeftMouseUp:
                _recordedMouseButtons.Remove(MacroEventType.LeftMouseDown);
                break;
            case MacroEventType.RightMouseUp:
                _recordedMouseButtons.Remove(MacroEventType.RightMouseDown);
                break;
            case MacroEventType.MiddleMouseUp:
                _recordedMouseButtons.Remove(MacroEventType.MiddleMouseDown);
                break;
            case MacroEventType.XButton1Up:
                _recordedMouseButtons.Remove(MacroEventType.XButton1Down);
                break;
            case MacroEventType.XButton2Up:
                _recordedMouseButtons.Remove(MacroEventType.XButton2Down);
                break;
        }
    }

    private void CloseOpenInputsLocked()
    {
        foreach (uint virtualKey in _recordedKeysDown.ToArray())
            AddClosingEventLocked(new MacroEvent { Type = MacroEventType.KeyUp, VirtualKey = (int)virtualKey });
        foreach (var (type, point) in _recordedMouseButtons.ToArray())
        {
            MacroEventType releaseType = type switch
            {
                MacroEventType.LeftMouseDown => MacroEventType.LeftMouseUp,
                MacroEventType.RightMouseDown => MacroEventType.RightMouseUp,
                MacroEventType.MiddleMouseDown => MacroEventType.MiddleMouseUp,
                MacroEventType.XButton1Down => MacroEventType.XButton1Up,
                _ => MacroEventType.XButton2Up
            };
            AddClosingEventLocked(new MacroEvent { Type = releaseType, X = point.X, Y = point.Y });
        }
    }

    private void AddClosingEventLocked(MacroEvent item)
    {
        item.OffsetMilliseconds = _recordingClock.ElapsedMilliseconds;
        _events.Add(item);
        switch (item.Type)
        {
            case MacroEventType.KeyUp:
                _recordedKeysDown.Remove((uint)item.VirtualKey);
                break;
            case MacroEventType.LeftMouseUp:
                _recordedMouseButtons.Remove(MacroEventType.LeftMouseDown);
                break;
            case MacroEventType.RightMouseUp:
                _recordedMouseButtons.Remove(MacroEventType.RightMouseDown);
                break;
            case MacroEventType.MiddleMouseUp:
                _recordedMouseButtons.Remove(MacroEventType.MiddleMouseDown);
                break;
            case MacroEventType.XButton1Up:
                _recordedMouseButtons.Remove(MacroEventType.XButton1Down);
                break;
            case MacroEventType.XButton2Up:
                _recordedMouseButtons.Remove(MacroEventType.XButton2Down);
                break;
        }
    }

    private void Unhook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopRecording();
        _disposed = true;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfXdown = 0x0080;
    private const uint MouseeventfXup = 0x0100;
    private const uint MouseeventfVirtualdesk = 0x4000;
    private const uint MouseeventfAbsolute = 0x8000;
    private const uint Xbutton1 = 0x0001;
    private const uint Xbutton2 = 0x0002;
    private const uint KeyeventfKeyup = 0x0002;
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int inputSize);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
