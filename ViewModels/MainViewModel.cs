using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RobloxMultiLauncher.Interop;
using RobloxMultiLauncher.Models;
using RobloxMultiLauncher.Services;
using RobloxMultiLauncher.Views;

namespace RobloxMultiLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ProfileStore _store = new();
    private readonly RobloxAuthService _auth = new();
    private readonly SingletonGuard _guard = new();
    private readonly RobloxLauncherService _launcher;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly SemaphoreSlim _instanceRefreshGate = new(1, 1);
    private readonly Dictionary<int, ProfileViewModel> _tracked = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _macroTimer;
    private readonly AppSettings _settings;
    private readonly MacroService _macroService = new();
    private readonly Stopwatch _playbackClock = new();
    private readonly HashSet<int> _pressedPlaybackKeys = new();
    private readonly Dictionary<MacroEventType, (int X, int Y)> _pressedPlaybackMouseButtons = new();
    private readonly HashSet<int> _pausedPlaybackKeys = new();
    private readonly Dictionary<MacroEventType, (int X, int Y)> _pausedPlaybackMouseButtons = new();
    private readonly HashSet<int> _registeredHotkeyIds = new();
    private MacroRecording? _currentMacro;
    private int _playbackEventIndex;
    private IntPtr _hotkeyWindow;
    private Task _startupCleanupTask = Task.CompletedTask;
    private bool _isDisposed;

    public ObservableCollection<ProfileViewModel> Profiles { get; } = new();
    public ObservableCollection<InstanceViewModel> Instances { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<MonitorOption> AvailableMonitors { get; } = new();
    public ObservableCollection<MacroLibraryItem> SavedMacros { get; } = new();

    // Navigation
    private bool _isDashboard = true, _isActivity, _isMacros, _isSettings, _isDiagnostics;
    public bool IsDashboard { get => _isDashboard; set => Set(ref _isDashboard, value); }
    public bool IsActivity { get => _isActivity; set => Set(ref _isActivity, value); }
    public bool IsMacros { get => _isMacros; set => Set(ref _isMacros, value); }
    private bool _isMods;
    public bool IsMods { get => _isMods; set => Set(ref _isMods, value); }
    public bool IsSettings { get => _isSettings; set => Set(ref _isSettings, value); }
    public bool IsDiagnostics { get => _isDiagnostics; set { if (Set(ref _isDiagnostics, value) && value) _ = RefreshDiagnosticsAsync(); } }
    private string _backgroundColor, _panelColor, _cardColor, _accentColor, _logoPath;
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public string PanelColor { get => _panelColor; set => Set(ref _panelColor, value); }
    public string CardColor { get => _cardColor; set => Set(ref _cardColor, value); }
    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }
    public string LogoPath
    {
        get => _logoPath;
        private set
        {
            if (Set(ref _logoPath, value)) OnPropertyChanged(nameof(HasCustomLogo));
        }
    }
    public bool HasCustomLogo => !string.IsNullOrWhiteSpace(LogoPath) && File.Exists(LogoPath);
    public event EventHandler? AppearanceChanged;

    // Settings-bound
    private string _robloxPath, _placeInput, _jobId;
    private string _selectedMonitorDeviceName = "";
    private bool _attemptMulti, _autoArrange;
    private int _timeout, _sessionsPerRow, _liveActivityColumns;
    public IReadOnlyList<int> LiveActivityColumnOptions { get; } = Enumerable.Range(1, 12).ToArray();
    public string RobloxPath { get => _robloxPath; set => Set(ref _robloxPath, value); }
    public string PlaceInput { get => _placeInput; set => Set(ref _placeInput, value); }
    public string JobId { get => _jobId; set => Set(ref _jobId, value); }
    public string SelectedMonitorDeviceName
    {
        get => _selectedMonitorDeviceName;
        set => Set(ref _selectedMonitorDeviceName, value);
    }
    public bool AttemptMultiInstance { get => _attemptMulti; set => Set(ref _attemptMulti, value); }
    public bool AutoArrangeWindows { get => _autoArrange; set => Set(ref _autoArrange, value); }
    public int LaunchTimeoutSeconds { get => _timeout; set => Set(ref _timeout, value); }
    public int SessionsPerRow
    {
        get => _sessionsPerRow;
        set => Set(ref _sessionsPerRow, Math.Clamp(value, 1, 12));
    }
    public int LiveActivityColumns
    {
        get => _liveActivityColumns;
        set
        {
            int columns = Math.Clamp(value, 1, 12);
            if (!Set(ref _liveActivityColumns, columns)) return;

            _settings.LiveActivityColumns = columns;
            try { _store.SaveSettings(_settings); }
            catch (Exception ex) { ShowError("Could not save live activity layout", ex.Message, ex); }
        }
    }

    // Status
    private string _statusMessage = "Ready.", _singletonStatus = "", _multiStatus = "", _resolvedExe = "", _robloxVersion = "", _outsideInfo = "";
    private bool _hasWarning;
    private string _warning = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public string SingletonStatus { get => _singletonStatus; set => Set(ref _singletonStatus, value); }
    public string MultiStatus { get => _multiStatus; set => Set(ref _multiStatus, value); }
    public string ResolvedExe { get => _resolvedExe; set => Set(ref _resolvedExe, value); }
    public string RobloxVersion { get => _robloxVersion; set => Set(ref _robloxVersion, value); }
    public string OutsideInfo { get => _outsideInfo; set => Set(ref _outsideInfo, value); }
    public string Warning { get => _warning; set { if (Set(ref _warning, value)) HasWarning = value.Length > 0; } }
    public bool HasWarning { get => _hasWarning; private set => Set(ref _hasWarning, value); }

    // Commands
    public ICommand AddProfileCommand { get; }
    public ICommand RemoveProfileCommand { get; }
    public ICommand ReLoginCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand LaunchAllCommand { get; }
    public ICommand CloseAllSessionsCommand { get; }
    public ICommand StopProfileCommand { get; }
    public ICommand FocusCommand { get; }
    public ICommand MinimizeCommand { get; }
    public ICommand CloseInstanceCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand AutoDetectCommand { get; }
    public ICommand RefreshDiagnosticsCommand { get; }
    public ICommand CloseOutsideCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ApplyAppearanceCommand { get; }
    public ICommand SelectLogoCommand { get; }
    public ICommand ClearLogoCommand { get; }
    public ICommand StartMacroRecordingCommand { get; }
    public ICommand PauseMacroCommand { get; }
    public ICommand PlayMacroCommand { get; }
    public ICommand StopMacroCommand { get; }
    public ICommand SaveMacroCommand { get; }
    public ICommand LoadMacroCommand { get; }
    public ICommand RefreshMacrosCommand { get; }

    private bool _isMacroRecording, _isMacroPlaying, _isMacroPaused;
    private string _macroName = "My macro", _macroStateText = "Idle", _macroElapsedText = "00:00.000";
    private string _macroDetailsText = "No recording loaded.", _macroPauseButtonText = "Pause";
    private string _recordHotkeyText, _playHotkeyText, _pauseHotkeyText, _stopHotkeyText;
    private int _macroRepeatCount = 1, _targetPlaybackCount, _completedPlaybackCount;
    private MacroLibraryItem? _selectedSavedMacro;
    public bool IsMacroRecording { get => _isMacroRecording; private set => Set(ref _isMacroRecording, value); }
    public bool IsMacroPlaying { get => _isMacroPlaying; private set => Set(ref _isMacroPlaying, value); }
    public bool IsMacroPaused { get => _isMacroPaused; private set => Set(ref _isMacroPaused, value); }
    public string MacroName { get => _macroName; set => Set(ref _macroName, value); }
    public string MacroStateText { get => _macroStateText; private set => Set(ref _macroStateText, value); }
    public string MacroElapsedText { get => _macroElapsedText; private set => Set(ref _macroElapsedText, value); }
    public string MacroDetailsText { get => _macroDetailsText; private set => Set(ref _macroDetailsText, value); }
    public string MacroPauseButtonText { get => _macroPauseButtonText; private set => Set(ref _macroPauseButtonText, value); }
    public string RecordHotkeyText { get => _recordHotkeyText; private set => Set(ref _recordHotkeyText, value); }
    public int MacroRepeatCount
    {
        get => _macroRepeatCount;
        set => Set(ref _macroRepeatCount, Math.Max(0, value));
    }
    public string PlayHotkeyText { get => _playHotkeyText; private set => Set(ref _playHotkeyText, value); }
    public string PauseHotkeyText { get => _pauseHotkeyText; private set => Set(ref _pauseHotkeyText, value); }
    public string StopHotkeyText { get => _stopHotkeyText; private set => Set(ref _stopHotkeyText, value); }
    public MacroLibraryItem? SelectedSavedMacro
    {
        get => _selectedSavedMacro;
        set
        {
            if (Set(ref _selectedSavedMacro, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public MainViewModel()
    {
        _settings = _store.LoadSettings();
        _recordHotkeyText = _settings.MacroRecordHotkey;
        _playHotkeyText = _settings.MacroPlayHotkey;
        _pauseHotkeyText = _settings.MacroPauseHotkey;
        _stopHotkeyText = _settings.MacroStopHotkey;
        _robloxPath = _settings.RobloxPath;
        _placeInput = _settings.PlaceInput;
        _jobId = _settings.JobId;
        foreach (var monitor in WindowManager.GetMonitors()) AvailableMonitors.Add(monitor);
        _selectedMonitorDeviceName = AvailableMonitors.Any(m =>
            string.Equals(m.DeviceName, _settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ? _settings.MonitorDeviceName
            : AvailableMonitors.FirstOrDefault(m => m.DisplayName.Contains("(Primary)", StringComparison.Ordinal))?.DeviceName
              ?? AvailableMonitors.FirstOrDefault()?.DeviceName
              ?? "";
        _settings.MonitorDeviceName = _selectedMonitorDeviceName;
        _attemptMulti = _settings.AttemptMultiInstance;
        _autoArrange = _settings.AutoArrangeWindows;
        _macroRepeatCount = Math.Max(0, _settings.MacroRepeatCount);
        _timeout = _settings.LaunchTimeoutSeconds;
        _sessionsPerRow = Math.Clamp(_settings.SessionsPerRow, 1, 12);
        _liveActivityColumns = Math.Clamp(_settings.LiveActivityColumns, 1, 12);
        _backgroundColor = _settings.BackgroundColor;
        _panelColor = _settings.PanelColor;
        _cardColor = _settings.CardColor;
        _accentColor = _settings.AccentColor;
        _logoPath = _settings.CustomLogoPath;
        if (!string.IsNullOrWhiteSpace(_logoPath) && !IsValidLogo(_logoPath))
        {
            Logger.Warn("The saved application logo could not be loaded; using the default logo.");
            _logoPath = "";
        }

        _launcher = new RobloxLauncherService(_auth, _guard);
        foreach (var sp in _store.LoadProfiles()) Profiles.Add(new ProfileViewModel(sp));
        Logger.LineWritten += OnLogLine;

        AddProfileCommand = new AsyncRelayCommand(_ => SignInAsync(null));
        ReLoginCommand = new AsyncRelayCommand(p => SignInAsync(p as ProfileViewModel));
        RemoveProfileCommand = new RelayCommand(p => RemoveProfile(p as ProfileViewModel));
        LaunchCommand = new AsyncRelayCommand(async p => { if (p is ProfileViewModel vm) await LaunchProfileAsync(vm); });
        LaunchAllCommand = new AsyncRelayCommand(_ => LaunchAllAsync());
        CloseAllSessionsCommand = new AsyncRelayCommand(_ => CloseAllSessionsAsync());
        StopProfileCommand = new AsyncRelayCommand(async p => { if (p is ProfileViewModel { Pid: int pid }) await CloseProcessAsync(pid); });
        FocusCommand = new RelayCommand(p => { if (p is InstanceViewModel i && !WindowManager.Focus(i.Hwnd)) StatusMessage = "That window can no longer be focused."; });
        MinimizeCommand = new RelayCommand(p => { if (p is InstanceViewModel i && !WindowManager.Minimize(i.Hwnd)) StatusMessage = "That window can no longer be minimised."; });
        CloseInstanceCommand = new AsyncRelayCommand(async p => { if (p is InstanceViewModel i) await CloseProcessAsync(i.Pid); });
        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        BrowseCommand = new RelayCommand(_ => Browse());
        AutoDetectCommand = new RelayCommand(_ => { RobloxPath = ""; SaveSettings(); });
        RefreshDiagnosticsCommand = new AsyncRelayCommand(_ => RefreshDiagnosticsAsync());
        CloseOutsideCommand = new AsyncRelayCommand(_ => CloseOutsideProcessesAsync());
        OpenDataFolderCommand = new RelayCommand(_ => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDir) { UseShellExecute = true }));
        ApplyAppearanceCommand = new RelayCommand(_ => ApplyAndSaveAppearance());
        SelectLogoCommand = new RelayCommand(_ => SelectLogo());
        ClearLogoCommand = new RelayCommand(_ => { LogoPath = ""; SaveAppearanceSettings(); });
        StartMacroRecordingCommand = new RelayCommand(_ => StartMacroRecording(), _ => !IsMacroRecording && !IsMacroPlaying);
        PauseMacroCommand = new RelayCommand(_ => ToggleMacroPause(), _ => IsMacroRecording || IsMacroPlaying);
        PlayMacroCommand = new RelayCommand(_ => PlayMacro(),
            _ => !IsMacroRecording && (!IsMacroPlaying || IsMacroPaused) && _currentMacro is { Events.Count: > 0 });
        StopMacroCommand = new RelayCommand(_ => StopMacro(), _ => IsMacroRecording || IsMacroPlaying);
        SaveMacroCommand = new RelayCommand(_ => SaveMacro(), _ => _currentMacro != null && !IsMacroRecording && !IsMacroPlaying);
        LoadMacroCommand = new RelayCommand(_ => LoadSelectedMacro(), _ => SelectedSavedMacro != null && !IsMacroRecording && !IsMacroPlaying);
        RefreshMacrosCommand = new RelayCommand(_ => RefreshSavedMacros());
        RefreshSavedMacros(showErrors: false);

        if (_settings.AttemptMultiInstance) _guard.TryClaim();
        UpdateSingletonText();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) =>
        {
            if (Application.Current?.MainWindow?.WindowState != WindowState.Minimized)
                await RefreshInstancesAsync();
        };
        _timer.Start();
        _macroTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _macroTimer.Tick += MacroTimerTick;
        _ = RefreshInstancesAsync();
        _startupCleanupTask = CloseOutsideProcessesAsync(confirm: false);
    }

    public void InitializeMacroHotkeys(IntPtr hwnd)
    {
        _hotkeyWindow = hwnd;
        RegisterConfiguredHotkey(1, PlayHotkeyText, "play");
        RegisterConfiguredHotkey(2, PauseHotkeyText, "pause");
        RegisterConfiguredHotkey(3, StopHotkeyText, "stop");
        RegisterConfiguredHotkey(4, RecordHotkeyText, "record");
        UpdateIgnoredMacroHotkeys();
    }

    public void HandleMacroHotkey(int id)
    {
        switch (id)
        {
            case 1: PlayMacro(); break;
            case 2: ToggleMacroPause(); break;
            case 3: StopMacro(); break;
            case 4:
                if (IsMacroRecording) FinishRecording("Recording stopped by global shortcut.");
                else if (!IsMacroPlaying) StartMacroRecording();
                break;
        }
    }

    public void CaptureMacroHotkey(string action, Key key, ModifierKeys modifiers)
    {
        if (IsModifierKey(key))
        {
            StatusMessage = "Press a non-modifier key together with Ctrl, Alt, Shift, or Windows to set a shortcut.";
            return;
        }

        uint modifierFlags = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) modifierFlags |= 0x0001;
        if (modifiers.HasFlag(ModifierKeys.Control)) modifierFlags |= 0x0002;
        if (modifiers.HasFlag(ModifierKeys.Shift)) modifierFlags |= 0x0004;
        if (modifiers.HasFlag(ModifierKeys.Windows)) modifierFlags |= 0x0008;
        if (modifierFlags == 0)
        {
            StatusMessage = "A global shortcut must include Ctrl, Alt, Shift, or Windows.";
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey is <= 0 or > 255)
        {
            StatusMessage = $"'{key}' cannot be used as a global shortcut.";
            return;
        }

        string text = $"{FormatModifiers(modifierFlags)}{key}";
        int id = action switch { "record" => 4, "play" => 1, "pause" => 2, "stop" => 3, _ => 0 };
        if (id == 0) return;
        if (!TryChangeHotkey(id, text, modifierFlags, (uint)virtualKey)) return;

        switch (id)
        {
            case 4: RecordHotkeyText = text; _settings.MacroRecordHotkey = text; break;
            case 1: PlayHotkeyText = text; _settings.MacroPlayHotkey = text; break;
            case 2: PauseHotkeyText = text; _settings.MacroPauseHotkey = text; break;
            case 3: StopHotkeyText = text; _settings.MacroStopHotkey = text; break;
        }
        UpdateIgnoredMacroHotkeys();
        SaveMacroHotkeys();
    }

    private void UpdateIgnoredMacroHotkeys()
    {
        var hotkeys = new List<(uint Modifiers, uint VirtualKey)>();
        string[] texts = { PlayHotkeyText, PauseHotkeyText, StopHotkeyText, RecordHotkeyText };
        for (int index = 0; index < texts.Length; index++)
        {
            if (_registeredHotkeyIds.Contains(index + 1) &&
                TryParseHotkey(texts[index], out uint modifiers, out uint virtualKey))
                hotkeys.Add((modifiers, virtualKey));
        }
        _macroService.SetIgnoredHotkeys(hotkeys);
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string FormatModifiers(uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");
        return string.Join("+", parts) + "+";
    }

    private void RegisterConfiguredHotkey(int id, string text, string action)
    {
        if (!TryParseHotkey(text, out uint modifiers, out uint virtualKey))
        {
            Logger.Warn($"The saved global macro {action} shortcut '{text}' is invalid.");
            StatusMessage = $"Saved {action} shortcut '{text}' is invalid; edit it in Macros.";
            return;
        }
        if (!NativeMethods.RegisterHotKey(_hotkeyWindow, id, modifiers | 0x4000, virtualKey))
        {
            _registeredHotkeyIds.Remove(id);
            int error = Marshal.GetLastWin32Error();
            Logger.Warn($"Could not register the global macro {action} shortcut '{text}' (Windows error {error}).");
            StatusMessage = $"Could not register {action} shortcut '{text}' (Windows error {error}); edit it in Macros.";
            return;
        }
        _registeredHotkeyIds.Add(id);
    }

    private bool TryChangeHotkey(int id, string text, uint modifiers, uint virtualKey)
    {
        if (_hotkeyWindow == IntPtr.Zero)
        {
            StatusMessage = "Global shortcuts are not ready yet.";
            return false;
        }
        if (new[] { PlayHotkeyText, PauseHotkeyText, StopHotkeyText, RecordHotkeyText }
            .Where((_, index) => index + 1 != id)
            .Any(existing => string.Equals(existing, text, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "Each macro action needs a different global shortcut.";
            return false;
        }

        string previous = id switch
        {
            1 => PlayHotkeyText,
            2 => PauseHotkeyText,
            3 => StopHotkeyText,
            _ => RecordHotkeyText
        };
        NativeMethods.UnregisterHotKey(_hotkeyWindow, id);
        _registeredHotkeyIds.Remove(id);
        if (!NativeMethods.RegisterHotKey(_hotkeyWindow, id, modifiers | 0x4000, virtualKey))
        {
            int error = Marshal.GetLastWin32Error();
            bool previousRestored = false;
            if (TryParseHotkey(previous, out uint oldModifiers, out uint oldKey))
            {
                previousRestored = NativeMethods.RegisterHotKey(_hotkeyWindow, id, oldModifiers | 0x4000, oldKey);
                if (previousRestored)
                    _registeredHotkeyIds.Add(id);
            }
            UpdateIgnoredMacroHotkeys();
            StatusMessage = previousRestored
                ? $"Could not register '{text}' (Windows error {error}); the previous shortcut was kept."
                : $"Could not register '{text}' (Windows error {error}), and the previous shortcut could not be restored.";
            Logger.Warn(StatusMessage);
            return false;
        }
        _registeredHotkeyIds.Add(id);
        StatusMessage = $"Global {id switch { 1 => "play", 2 => "pause", 3 => "stop", _ => "record" }} shortcut set to {text}.";
        return true;
    }

    private void SaveMacroHotkeys()
    {
        try { _store.SaveSettings(_settings); }
        catch (Exception ex) { ShowError("Could not save macro shortcuts", ex.Message, ex); }
    }

    private static bool TryParseHotkey(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            uint flag = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 0x0002,
                "ALT" => 0x0001,
                "SHIFT" => 0x0004,
                "WIN" or "WINDOWS" => 0x0008,
                _ => 0
            };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }
        return Enum.TryParse(parts[^1], true, out Key key) &&
            !IsModifierKey(key) &&
            (virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key)) > 0;
    }

    private void StartMacroRecording()
    {
        try
        {
            StopPlayback();
            _currentMacro = null;
            _macroService.StartRecording();
            IsMacroRecording = true;
            IsMacroPlaying = false;
            IsMacroPaused = false;
            MacroStateText = "Recording across Windows";
            MacroDetailsText = "Input is being recorded globally. Pause or stop to finish.";
            MacroElapsedText = "00:00.000";
            _macroTimer.Start();
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            ShowError("Could not start macro recording", ex.Message, ex);
        }
    }

    private void ToggleMacroPause()
    {
        if (IsMacroRecording)
        {
            if (IsMacroPaused)
            {
                _macroService.ResumeRecording();
                IsMacroPaused = false;
                MacroStateText = "Recording across Windows";
                MacroPauseButtonText = "Pause";
            }
            else
            {
                _macroService.PauseRecording();
                IsMacroPaused = true;
                MacroStateText = "Recording paused";
                MacroPauseButtonText = "Resume";
            }
        }
        else if (IsMacroPlaying)
        {
            try
            {
                if (IsMacroPaused)
                {
                    ResumePlaybackInputs();
                    _playbackClock.Start();
                    IsMacroPaused = false;
                    MacroStateText = "Playing";
                    MacroPauseButtonText = "Pause";
                    _macroTimer.Start();
                }
                else
                {
                    _playbackClock.Stop();
                    PausePlaybackInputs();
                    IsMacroPaused = true;
                    MacroStateText = "Playback paused";
                    MacroPauseButtonText = "Resume";
                    _macroTimer.Stop();
                }
            }
            catch (Exception ex)
            {
                StopPlayback();
                ShowError("Could not pause or resume macro playback", ex.Message, ex);
            }
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void PlayMacro()
    {
        if (IsMacroRecording)
        {
            if (IsMacroPaused) ToggleMacroPause();
            return;
        }
        if (IsMacroPlaying)
        {
            if (IsMacroPaused) ToggleMacroPause();
            return;
        }
        if (_currentMacro == null || _currentMacro.Events.Count == 0)
        {
            StatusMessage = "Load or record a macro with at least one event before playing.";
            return;
        }

        _playbackEventIndex = 0;
        _completedPlaybackCount = 0;
        _targetPlaybackCount = MacroRepeatCount;
        _playbackClock.Restart();
        IsMacroPlaying = true;
        IsMacroPaused = false;
        MacroPauseButtonText = "Pause";
        MacroStateText = "Playing";
        MacroDetailsText = GetPlaybackProgressText();
        _macroTimer.Start();
        CommandManager.InvalidateRequerySuggested();
    }

    private void StopMacro()
    {
        if (IsMacroRecording)
        {
            FinishRecording("Recording stopped.");
            return;
        }
        if (IsMacroPlaying) StopPlayback();
    }

    private void FinishRecording(string message)
    {
        _macroTimer.Stop();
        _currentMacro = _macroService.StopRecording(string.IsNullOrWhiteSpace(MacroName) ? "My macro" : MacroName.Trim());
        IsMacroRecording = false;
        IsMacroPlaying = false;
        IsMacroPaused = false;
        MacroPauseButtonText = "Pause";
        MacroStateText = "Idle";
        MacroDetailsText = $"{_currentMacro.Events.Count:N0} events recorded. Save the recording or play it now.";
        MacroElapsedText = MacroService.FormatDuration(_macroService.ElapsedMilliseconds);
        StatusMessage = message;
        CommandManager.InvalidateRequerySuggested();
    }

    private void StopPlayback()
    {
        _macroTimer.Stop();
        _playbackClock.Stop();
        bool wasPlaying = IsMacroPlaying;
        IsMacroPlaying = false;
        IsMacroPaused = false;
        _targetPlaybackCount = 0;
        _pausedPlaybackKeys.Clear();
        _pausedPlaybackMouseButtons.Clear();
        MacroPauseButtonText = "Pause";
        if (wasPlaying)
        {
            ReleasePlaybackInputs();
            MacroStateText = "Idle";
            MacroDetailsText = "Playback stopped.";
            UpdateMacroElapsed();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void MacroTimerTick(object? sender, EventArgs e)
    {
        if (IsMacroRecording)
        {
            if (_macroService.EventLimitReached)
            {
                FinishRecording("Recording stopped at the 250,000-event safety limit.");
                return;
            }
            UpdateMacroElapsed();
            MacroDetailsText = $"{_macroService.EventCount:N0} events recorded.";
            return;
        }
        if (!IsMacroPlaying || IsMacroPaused || _currentMacro == null) return;

        long elapsed = _playbackClock.ElapsedMilliseconds;
        try
        {
            while (_playbackEventIndex < _currentMacro.Events.Count &&
                   _currentMacro.Events[_playbackEventIndex].OffsetMilliseconds <= elapsed)
            {
                MacroEvent item = _currentMacro.Events[_playbackEventIndex++];
                MacroService.Send(item);
                TrackPlaybackInput(item);
            }
            UpdateMacroElapsed();
            if (_playbackEventIndex >= _currentMacro.Events.Count)
            {
                if (elapsed >= _currentMacro.DurationMilliseconds)
                {
                    _completedPlaybackCount++;
                    if (_targetPlaybackCount > 0 && _completedPlaybackCount >= _targetPlaybackCount)
                    {
                        StopPlayback();
                        MacroStateText = "Finished";
                        MacroDetailsText = $"Finished playing “{_currentMacro.Name}” ({_completedPlaybackCount:N0} time(s)).";
                        StatusMessage = "Macro playback finished.";
                    }
                    else
                    {
                        ReleasePlaybackInputs();
                        _playbackEventIndex = 0;
                        _playbackClock.Restart();
                        MacroDetailsText = GetPlaybackProgressText();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StopPlayback();
            ShowError("Macro playback stopped", ex.Message, ex);
        }
    }

    private string GetPlaybackProgressText() =>
        _targetPlaybackCount == 0
            ? $"Playing “{_currentMacro?.Name}” — loop {_completedPlaybackCount + 1:N0} (infinite)."
            : $"Playing “{_currentMacro?.Name}” — loop {_completedPlaybackCount + 1:N0} of {_targetPlaybackCount:N0}.";

    private void TrackPlaybackInput(MacroEvent item)
    {
        switch (item.Type)
        {
            case MacroEventType.KeyDown: _pressedPlaybackKeys.Add(item.VirtualKey); break;
            case MacroEventType.KeyUp: _pressedPlaybackKeys.Remove(item.VirtualKey); break;
            case MacroEventType.LeftMouseDown: _pressedPlaybackMouseButtons[MacroEventType.LeftMouseDown] = (item.X, item.Y); break;
            case MacroEventType.LeftMouseUp: _pressedPlaybackMouseButtons.Remove(MacroEventType.LeftMouseDown); break;
            case MacroEventType.RightMouseDown: _pressedPlaybackMouseButtons[MacroEventType.RightMouseDown] = (item.X, item.Y); break;
            case MacroEventType.RightMouseUp: _pressedPlaybackMouseButtons.Remove(MacroEventType.RightMouseDown); break;
            case MacroEventType.MiddleMouseDown: _pressedPlaybackMouseButtons[MacroEventType.MiddleMouseDown] = (item.X, item.Y); break;
            case MacroEventType.MiddleMouseUp: _pressedPlaybackMouseButtons.Remove(MacroEventType.MiddleMouseDown); break;
            case MacroEventType.XButton1Down: _pressedPlaybackMouseButtons[MacroEventType.XButton1Down] = (item.X, item.Y); break;
            case MacroEventType.XButton1Up: _pressedPlaybackMouseButtons.Remove(MacroEventType.XButton1Down); break;
            case MacroEventType.XButton2Down: _pressedPlaybackMouseButtons[MacroEventType.XButton2Down] = (item.X, item.Y); break;
            case MacroEventType.XButton2Up: _pressedPlaybackMouseButtons.Remove(MacroEventType.XButton2Down); break;
        }
    }

    private void ReleasePlaybackInputs()
    {
        foreach (int virtualKey in _pressedPlaybackKeys.ToArray())
        {
            try { MacroService.Send(new MacroEvent { Type = MacroEventType.KeyUp, VirtualKey = virtualKey }); }
            catch (Exception ex) { Logger.Error($"Could not release replayed key {virtualKey}", ex); }
        }
        _pressedPlaybackKeys.Clear();

        foreach (var (type, point) in _pressedPlaybackMouseButtons.ToArray())
        {
            MacroEventType releaseType = type switch
            {
                MacroEventType.LeftMouseDown => MacroEventType.LeftMouseUp,
                MacroEventType.RightMouseDown => MacroEventType.RightMouseUp,
                MacroEventType.MiddleMouseDown => MacroEventType.MiddleMouseUp,
                MacroEventType.XButton1Down => MacroEventType.XButton1Up,
                MacroEventType.XButton2Down => MacroEventType.XButton2Up,
                _ => MacroEventType.MiddleMouseUp
            };
            try { MacroService.Send(new MacroEvent { Type = releaseType, X = point.X, Y = point.Y }); }
            catch (Exception ex) { Logger.Error($"Could not release replayed mouse button {type}", ex); }
        }
        _pressedPlaybackMouseButtons.Clear();
    }

    private void PausePlaybackInputs()
    {
        _pausedPlaybackKeys.UnionWith(_pressedPlaybackKeys);
        foreach (var (type, point) in _pressedPlaybackMouseButtons)
            _pausedPlaybackMouseButtons[type] = point;
        ReleasePlaybackInputs();
    }

    private void ResumePlaybackInputs()
    {
        foreach (int virtualKey in _pausedPlaybackKeys)
        {
            MacroService.Send(new MacroEvent { Type = MacroEventType.KeyDown, VirtualKey = virtualKey });
            _pressedPlaybackKeys.Add(virtualKey);
        }
        foreach (var (type, point) in _pausedPlaybackMouseButtons.ToArray())
        {
            MacroService.Send(new MacroEvent { Type = type, X = point.X, Y = point.Y });
            TrackPlaybackInput(new MacroEvent { Type = type, X = point.X, Y = point.Y });
        }
        _pausedPlaybackKeys.Clear();
        _pausedPlaybackMouseButtons.Clear();
    }

    private void UpdateMacroElapsed()
    {
        long elapsed = IsMacroRecording ? _macroService.ElapsedMilliseconds : _playbackClock.ElapsedMilliseconds;
        MacroElapsedText = MacroService.FormatDuration(elapsed);
    }

    private void SaveMacro()
    {
        if (_currentMacro == null) return;
        string name = MacroName.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "Enter a name before saving the recording.";
            return;
        }
        try
        {
            _currentMacro.Name = name;
            _currentMacro = _macroService.Save(_currentMacro);
            RefreshSavedMacros();
            MacroDetailsText = $"Saved “{name}” ({_currentMacro.Events.Count:N0} events).";
            StatusMessage = $"Saved macro “{name}”.";
        }
        catch (Exception ex) { ShowError("Could not save macro", ex.Message, ex); }
        CommandManager.InvalidateRequerySuggested();
    }

    private void LoadSelectedMacro()
    {
        if (SelectedSavedMacro == null) return;
        try
        {
            _currentMacro = _macroService.Load(SelectedSavedMacro.Id);
            MacroName = _currentMacro.Name;
            MacroDetailsText = $"Loaded “{_currentMacro.Name}” ({_currentMacro.Events.Count:N0} events).";
            MacroElapsedText = "00:00.000";
            MacroStateText = "Ready";
            StatusMessage = $"Loaded macro “{_currentMacro.Name}”.";
        }
        catch (Exception ex) { ShowError("Could not load macro", ex.Message, ex); }
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshSavedMacros(bool showErrors = true)
    {
        try
        {
            SavedMacros.Clear();
            foreach (MacroLibraryItem item in _macroService.GetSavedMacros())
                SavedMacros.Add(item);
            SelectedSavedMacro = null;
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            if (showErrors) ShowError("Could not load saved macros", ex.Message, ex);
            else Logger.Error("Could not list saved macros", ex);
        }
    }

    // ---------- Accounts ----------

    private async Task SignInAsync(ProfileViewModel? target)
    {
        var win = new LoginWindow { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        var cookie = win.Cookie;
        if (string.IsNullOrEmpty(cookie)) return;

        try
        {
            StatusMessage = "Verifying sign-in…";
            var (id, name) = await _auth.GetAuthenticatedUserAsync(cookie, CancellationToken.None);
            var protectedCookie = SecretProtector.Protect(cookie);
            var existing = Profiles.FirstOrDefault(p => p.Model.UserId == id);
            if (target != null)
            {
                if (existing != null && !ReferenceEquals(existing, target))
                {
                    ShowError("Account already added",
                        $"The signed-in account “{name}” is already present as another profile. The selected profile was not changed.", null);
                    return;
                }

                target.Model.DisplayName = name;
                target.Model.UserId = id;
                target.Model.ProtectedCookie = protectedCookie;
                target.NeedsLogin = false;
                target.StatusText = "Ready";
                target.LastError = "";
                target.RefreshIdentity();
            }
            else if (existing == null)
            {
                Profiles.Add(new ProfileViewModel(new StoredProfile { DisplayName = name, UserId = id, ProtectedCookie = protectedCookie }));
            }
            else
            {
                existing.Model.DisplayName = name;
                existing.Model.ProtectedCookie = protectedCookie;
                existing.NeedsLogin = false;
                existing.StatusText = "Ready";
                existing.LastError = "";
                existing.RefreshIdentity();
            }
            _store.SaveProfiles(Profiles.Select(p => p.Model));
            StatusMessage = $"Signed in as {name}.";
        }
        catch (Exception ex)
        {
            ShowError("Could not verify the Roblox sign-in", ex.Message, ex);
        }
    }

    private void RemoveProfile(ProfileViewModel? p)
    {
        if (p == null) return;
        if (MessageBox.Show(Application.Current.MainWindow, $"Remove \"{p.DisplayName}\" and delete its stored session?\n(A running Roblox window is not closed.)",
                "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Profiles.Remove(p);
        _store.SaveProfiles(Profiles.Select(x => x.Model));
        StatusMessage = $"Removed {p.DisplayName}.";
    }

    // ---------- Launching ----------

    private async Task LaunchAllAsync()
    {
        foreach (var p in Profiles.ToList())
        {
            if (!p.CanLaunch) continue;
            if (!await LaunchProfileAsync(p)) break; // a failure usually applies to every later launch too
        }
    }

    private async Task<bool> LaunchProfileAsync(ProfileViewModel p)
    {
        if (p.IsBusy || p.IsRunning) return false;
        try
        {
            await _startupCleanupTask;
        }
        catch (Exception ex)
        {
            ShowError("Startup process cleanup failed", ex.Message, ex);
            return false;
        }

        string accountPlaceInput = p.PlaceInput.Trim();
        string launchPlaceInput = accountPlaceInput.Length > 0 ? accountPlaceInput : PlaceInput;
        long? placeId = ParsePlaceId(launchPlaceInput);
        if (placeId == null)
        {
            ShowError("Game not set", accountPlaceInput.Length > 0
                ? $"The game link for {p.DisplayName} is not a valid Roblox game link or Place ID."
                : "Enter a Place ID or a roblox.com/games/… link on the dashboard or in this account's game field first.", null);
            return false;
        }
        string? launchJobId = accountPlaceInput.Length > 0
            ? ParseJobIdFromLink(accountPlaceInput)
            : JobId;

        await _launchGate.WaitAsync();
        if (p.IsBusy || p.IsRunning)
        {
            _launchGate.Release();
            return false;
        }
        p.IsBusy = true;
        p.LastError = "";
        try
        {
            var cookie = SecretProtector.Unprotect(p.Model.ProtectedCookie);
            if (cookie == null)
            {
                p.NeedsLogin = true;
                p.StatusText = "Needs sign-in";
                return false;
            }

            _settings.PlaceInput = PlaceInput;
            _settings.JobId = JobId;
            _settings.MonitorDeviceName = SelectedMonitorDeviceName;
            _settings.RobloxPath = RobloxPath;
            _settings.AttemptMultiInstance = AttemptMultiInstance;
            _settings.LaunchTimeoutSeconds = LaunchTimeoutSeconds;
            _settings.SessionsPerRow = SessionsPerRow;
            _settings.AutoArrangeWindows = AutoArrangeWindows;
            _store.SaveSettings(_settings);
            _store.SaveProfiles(Profiles.Select(profile => profile.Model));

            var progress = new Progress<string>(m =>
            {
                p.StatusText = m;
                StatusMessage = $"{p.DisplayName}: {m}";
            });

            Logger.Info($"Launching '{p.DisplayName}' into place {placeId}");
            var result = await _launcher.LaunchAsync(_settings, cookie, placeId.Value, launchJobId, _tracked.Keys.ToList(), progress, CancellationToken.None);

            if (result.Success && result.Pid is int pid)
            {
                _tracked[pid] = p;
                p.Pid = pid;
                p.IsRunning = true;
                p.StatusText = result.ClosedOtherInstance ? "Running (another client was closed!)" : "Running";
                if (result.ClosedOtherInstance)
                {
                    Warning = result.Message;
                    ShowError("Multi-instance did not work", result.Message, null);
                }
                await RefreshInstancesAsync(waitForCurrent: true);
                if (AutoArrangeWindows) ArrangeTrackedWindows();
                return !result.ClosedOtherInstance;
            }

            p.StatusText = "Launch failed";
            p.LastError = result.Message;
            ShowError($"Launch failed for {p.DisplayName}", result.Message, null);
            return false;
        }
        catch (AuthExpiredException)
        {
            p.NeedsLogin = true;
            p.StatusText = "Session expired – sign in again";
            return false;
        }
        catch (Exception ex)
        {
            p.StatusText = "Launch failed";
            p.LastError = ex.Message;
            ShowError($"Launch failed for {p.DisplayName}", ex.Message, ex);
            return false;
        }
        finally
        {
            p.IsBusy = false;
            _launchGate.Release();
        }
    }

    private static long? ParsePlaceId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        if (long.TryParse(input, out var direct) && direct > 0) return direct;
        var m = Regex.Match(input, @"roblox\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?games/(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(input, @"placeId=(\d+)", RegexOptions.IgnoreCase);
        return m.Success && long.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    private static string? ParseJobIdFromLink(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in query)
        {
            var parts = item.Split('=', 2);
            if (parts.Length == 2 &&
                (parts[0].Equals("gameInstanceId", StringComparison.OrdinalIgnoreCase) ||
                 parts[0].Equals("gameId", StringComparison.OrdinalIgnoreCase)))
            {
                var value = Uri.UnescapeDataString(parts[1]);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        return null;
    }

    private void ArrangeTrackedWindows()
    {
        var windows = _tracked.Keys
            .OrderBy(pid => pid)
            .Select(WindowManager.FindMainWindow)
            .Where(hwnd => hwnd != IntPtr.Zero)
            .ToArray();
        int failures = WindowManager.ArrangeWindows(windows, SelectedMonitorDeviceName, SessionsPerRow);
        if (failures > 0)
            StatusMessage = $"Some Roblox windows could not be arranged ({failures} failed). See Diagnostics for details.";
        else if (windows.Length > 0)
            StatusMessage = $"Arranged {windows.Length} launcher-started Roblox session(s) on the selected monitor.";
    }

    // ---------- Windows / processes ----------

    private async Task CloseProcessAsync(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            Logger.Info($"Closing PID {pid} gracefully…");
            proc.CloseMainWindow();
            bool exited = await Task.Run(() => proc.WaitForExit(8000));
            if (!exited)
            {
                var ans = MessageBox.Show(Application.Current.MainWindow,
                    "Roblox did not close on request. Force-terminate it? Unsaved game progress may be lost.",
                    "Force close", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ans == MessageBoxResult.Yes) proc.Kill(true);
            }
        }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex) { ShowError("Could not close the window", ex.Message, ex); }
        await RefreshInstancesAsync(waitForCurrent: true);
    }

    private async Task CloseAllSessionsAsync()
    {
        StatusMessage = "Finding Roblox processes…";
        int[] pids = (await Task.Run(RobloxProcesses.GetPids)).ToArray();
        if (pids.Length == 0)
        {
            StatusMessage = "No Roblox processes were found.";
            return;
        }

        StatusMessage = $"Force-closing {pids.Length} Roblox process(es)…";
        bool[] terminated = await Task.WhenAll(pids.Select(TryTerminateProcessAsync));
        int closedCount = terminated.Count(closed => closed);

        await RefreshInstancesAsync(waitForCurrent: true);
        StatusMessage = closedCount == pids.Length
            ? $"Force-closed all {closedCount} Roblox process(es)."
            : $"Force-closed {closedCount} of {pids.Length} Roblox process(es); some could not be closed.";
    }

    private static Task<bool> TryTerminateProcessAsync(int pid) => Task.Run(() =>
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!string.Equals(proc.ProcessName, RobloxProcesses.ProcessName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (proc.HasExited) return true;
            proc.Kill(true);
            bool exited = proc.WaitForExit(3000);
            if (!exited) Logger.Warn($"Roblox process {pid} was still running after termination was requested.");
            return exited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Exception ex)
        {
            Logger.Warn($"Could not force-close PID {pid}: {ex.Message}");
            return false;
        }
    });

    private async Task RefreshInstancesAsync(bool waitForCurrent = false)
    {
        if (_isDisposed) return;
        if (waitForCurrent) await _instanceRefreshGate.WaitAsync();
        else if (!await _instanceRefreshGate.WaitAsync(0)) return;
        try
        {
            if (_isDisposed) return;
            var trackedOwners = _tracked.ToDictionary(pair => pair.Key, pair => pair.Value.DisplayName);
            var knownWindows = Instances.ToDictionary(instance => instance.Pid, instance => instance.Hwnd);
            var snapshots = await Task.Run(() => CaptureInstances(trackedOwners, knownWindows));
            if (_isDisposed) return;

            var seen = new HashSet<int>();
            var instancesByPid = Instances.ToDictionary(instance => instance.Pid);
            foreach (var snapshot in snapshots)
            {
                seen.Add(snapshot.Pid);
                if (!instancesByPid.TryGetValue(snapshot.Pid, out var vm))
                {
                    vm = new InstanceViewModel(snapshot.Pid);
                    Instances.Add(vm);
                }
                if (snapshot.Hwnd != IntPtr.Zero && trackedOwners.ContainsKey(snapshot.Pid) &&
                    vm.MarkStartupSizeAttempted(snapshot.Hwnd))
                    WindowManager.SetCompactSize(snapshot.Hwnd);
                vm.Update(snapshot.Hwnd, snapshot.Title, snapshot.OwnerName, snapshot.MemoryBytes,
                    snapshot.Uptime, snapshot.Minimized);
            }

            for (int i = Instances.Count - 1; i >= 0; i--)
                if (!seen.Contains(Instances[i].Pid)) Instances.RemoveAt(i);

            foreach (var pid in _tracked.Keys.Where(pid => !seen.Contains(pid)).ToArray())
            {
                var profile = _tracked[pid];
                _tracked.Remove(pid);
                profile.IsRunning = false;
                profile.Pid = null;
                profile.StatusText = "Stopped";
                Logger.Info($"{profile.DisplayName}'s client (PID {pid}) exited.");
            }
            UpdateMultiStatus();
        }
        catch (Exception ex) { Logger.Error("Instance refresh failed", ex); }
        finally { _instanceRefreshGate.Release(); }
    }

    private static List<InstanceSnapshot> CaptureInstances(
        IReadOnlyDictionary<int, string> trackedOwners, IReadOnlyDictionary<int, IntPtr> knownWindows)
    {
        var snapshots = new List<InstanceSnapshot>();
        foreach (var proc in Process.GetProcessesByName(RobloxProcesses.ProcessName))
        {
            using (proc)
            {
                try
                {
                    int pid = proc.Id;
                    var hwnd = knownWindows.TryGetValue(pid, out var knownWindow) && WindowManager.IsOwnedBy(knownWindow, pid)
                        ? knownWindow
                        : WindowManager.FindMainWindow(pid);
                    string title = hwnd != IntPtr.Zero ? WindowManager.GetTitle(hwnd) : "";
                    TimeSpan uptime = DateTime.Now - proc.StartTime;
                    long memoryBytes = proc.WorkingSet64;
                    trackedOwners.TryGetValue(pid, out var ownerName);
                    snapshots.Add(new InstanceSnapshot(pid, hwnd, title, ownerName, memoryBytes, uptime,
                        hwnd != IntPtr.Zero && WindowManager.IsMinimized(hwnd)));
                }
                catch (ArgumentException)
                {
                    // The client exited while its process data was being read.
                }
                catch (InvalidOperationException)
                {
                    // The client exited while its process data was being read.
                }
            }
        }
        return snapshots;
    }

    private sealed record InstanceSnapshot(
        int Pid, IntPtr Hwnd, string Title, string? OwnerName, long MemoryBytes, TimeSpan Uptime, bool Minimized);

    private void UpdateMultiStatus()
    {
        int alive = _tracked.Keys
            .Select(pid => Instances.FirstOrDefault(i => i.Pid == pid))
            .Count(i => i != null && i.HasWindow && i.Uptime >= TimeSpan.FromSeconds(20));

        if (alive >= 2)
        {
            if (_settings.VerifiedRobloxVersion != RobloxVersion || _settings.VerifiedAtUtc == null)
            {
                _settings.VerifiedRobloxVersion = RobloxVersion;
                _settings.VerifiedAtUtc = DateTime.UtcNow;
                try { _store.SaveSettings(_settings); } catch { }
            }
            MultiStatus = $"VERIFIED right now: {alive} launcher-started clients running at the same time ({RobloxVersion}).";
        }
        else if (_settings.VerifiedAtUtc != null && _settings.VerifiedRobloxVersion == RobloxVersion && RobloxVersion.Length > 0)
            MultiStatus = $"Worked on this Roblox version on {_settings.VerifiedAtUtc.Value.ToLocalTime():d}; not confirmed right now. Launch two accounts to re-check.";
        else
            MultiStatus = "NOT VERIFIED on this PC. Launch two accounts to find out whether this Roblox build allows it.";
    }

    // ---------- Settings / diagnostics ----------

    private void Browse()
    {
        var dlg = new OpenFileDialog { Title = "Select RobloxPlayerBeta.exe", Filter = "RobloxPlayerBeta.exe|RobloxPlayerBeta.exe|Executables|*.exe" };
        if (dlg.ShowDialog() == true) RobloxPath = dlg.FileName;
    }

    // Copies every UI-bound setting into the settings object (does not write the file).
    private void CollectSettings()
    {
        _settings.RobloxPath = RobloxPath.Trim();
        _settings.AttemptMultiInstance = AttemptMultiInstance;
        _settings.LaunchTimeoutSeconds = Math.Clamp(LaunchTimeoutSeconds, 15, 300);
        _settings.SessionsPerRow = SessionsPerRow;
        _settings.LiveActivityColumns = LiveActivityColumns;
        _settings.AutoArrangeWindows = AutoArrangeWindows;
        _settings.MacroRepeatCount = MacroRepeatCount;
        _settings.PlaceInput = PlaceInput;
        _settings.JobId = JobId;
        _settings.MonitorDeviceName = SelectedMonitorDeviceName;
        _settings.CustomLogoPath = LogoPath;
    }

    private void SaveSettings()
    {
        bool appearanceValid = TryApplyAppearance();
        CollectSettings();
        LaunchTimeoutSeconds = _settings.LaunchTimeoutSeconds;
        try { _store.SaveSettings(_settings); }
        catch (Exception ex) { ShowError("Could not save settings", ex.Message, ex); return; }

        if (!AttemptMultiInstance) _guard.Release();
        else if (!_guard.IsClaimed && RobloxProcesses.GetPids().All(_tracked.ContainsKey)) _guard.TryClaim();
        UpdateSingletonText();
        StatusMessage = appearanceValid
            ? "Settings saved."
            : "Settings saved, but appearance colors were not applied; correct the color values and apply them again.";
        _ = RefreshDiagnosticsAsync();
    }

    public void ApplyAppearanceOnStartup()
    {
        if (!TryApplyAppearance())
            Logger.Warn("Saved appearance colors are invalid; using the default application palette.");
    }

    private void ApplyAndSaveAppearance()
    {
        if (TryApplyAppearance()) SaveAppearanceSettings();
    }

    private bool TryApplyAppearance()
    {
        if (!TryParseColor(BackgroundColor, out var background) ||
            !TryParseColor(PanelColor, out var panel) ||
            !TryParseColor(CardColor, out var card) ||
            !TryParseColor(AccentColor, out var accent))
        {
            StatusMessage = "Use valid colors in #RRGGBB or #AARRGGBB format.";
            return false;
        }

        SetBrushColor("BgBrush", background);
        SetBrushColor("PanelBrush", panel);
        SetBrushColor("CardBrush", card);
        SetBrushColor("AccentBrush", accent);
        _settings.BackgroundColor = BackgroundColor;
        _settings.PanelColor = PanelColor;
        _settings.CardColor = CardColor;
        _settings.AccentColor = AccentColor;
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        if (string.IsNullOrEmpty(value) || value.Length is not (7 or 9) || !value.StartsWith('#'))
        {
            color = default;
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(value) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException) { }
        catch (NotSupportedException) { }
        catch (ArgumentException) { }
        color = default;
        return false;
    }

    private static void SetBrushColor(string resourceKey, Color color)
    {
        Application.Current.Resources[resourceKey] = new SolidColorBrush(color);
    }

    private void SelectLogo()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an application logo",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        if (!IsValidLogo(dialog.FileName))
        {
            ShowError("Could not load logo", "Choose a valid PNG, JPG, or BMP image.", null);
            return;
        }
        LogoPath = dialog.FileName;
        _settings.CustomLogoPath = LogoPath;
        SaveAppearanceSettings();
    }

    private static bool IsValidLogo(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or
                                   NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void SaveAppearanceSettings()
    {
        _settings.CustomLogoPath = LogoPath;
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            _store.SaveSettings(_settings);
            StatusMessage = "Appearance saved.";
        }
        catch (Exception ex) { ShowError("Could not save appearance", ex.Message, ex); }
    }

    private void UpdateSingletonText() =>
        SingletonStatus = !AttemptMultiInstance ? "Multi-instance attempt: OFF"
            : _guard.IsClaimed ? "Single-instance objects: claimed by this launcher"
            : "Single-instance objects: NOT claimed (a Roblox process owns them)";

    private async Task RefreshDiagnosticsAsync()
    {
        string configuredPath = RobloxPath;
        var (exe, cmds) = await Task.Run(() =>
        {
            var resolved = RobloxLocator.Resolve(configuredPath);
            return (resolved, RobloxProcesses.GetCommandLines());
        });
        ResolvedExe = exe ?? "Not found – set the path in Settings";
        RobloxVersion = RobloxLocator.VersionOf(exe) ?? "";

        var outside = cmds.Where(k => !_tracked.ContainsKey(k.Key)).ToList();
        if (outside.Count == 0)
        {
            OutsideInfo = "No Roblox processes outside this launcher were found.";
            if (AttemptMultiInstance && !_guard.IsClaimed) _guard.TryClaim();
        }
        else
        {
            bool tray = outside.Any(k => RobloxProcesses.IsTrayProcess(k.Value));
            OutsideInfo = $"{outside.Count} Roblox process(es) not started by this launcher (PID {string.Join(", ", outside.Select(k => k.Key))})."
                + (tray ? " One is Roblox's background/tray process, which already holds the single-instance objects and blocks multi-instance until it is closed." : "");
        }
        UpdateSingletonText();
        UpdateMultiStatus();
    }

    private Task CloseOutsideProcessesAsync() => CloseOutsideProcessesAsync(confirm: true);

    private async Task CloseOutsideProcessesAsync(bool confirm)
    {
        var trackedPids = _tracked.Keys.ToHashSet();
        var pids = await Task.Run(() => RobloxProcesses.GetPids()
            .Where(pid => !trackedPids.Contains(pid)).ToArray());
        if (pids.Length == 0)
        {
            if (confirm) StatusMessage = "Nothing to close.";
            await RefreshDiagnosticsAsync();
            return;
        }
        if (confirm && MessageBox.Show(Application.Current.MainWindow,
                    $"Close {pids.Length} Roblox process(es) that were not started by this launcher?\nAny game running in them will be disconnected.\n\nRoblox may restart its background process at the next Windows sign-in; turn off its start-with-Windows option (or Task Manager → Startup) to stop that.",
                    "Close Roblox processes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        StatusMessage = $"Closing {pids.Length} Roblox process(es) not started by this launcher…";
        var failedPids = await Task.Run(() =>
        {
            var failed = new HashSet<int>();
            foreach (var pid in pids)
            {
                try { using var process = Process.GetProcessById(pid); process.Kill(true); Logger.Info($"Closed outside Roblox process {pid}."); }
                catch (ArgumentException) { }
                catch (Exception ex) { failed.Add(pid); Logger.Warn($"Could not close PID {pid}: {ex.Message}"); }
            }
            return failed;
        });
        await Task.Delay(1000);
        var remainingPids = await Task.Run(() => RobloxProcesses.GetPids().Intersect(pids).ToArray());
        foreach (var pid in remainingPids)
        {
            if (failedPids.Add(pid))
                Logger.Warn($"Roblox process {pid} was still running after termination was requested.");
        }
        if (AttemptMultiInstance) _guard.TryClaim();
        await RefreshDiagnosticsAsync();
        await RefreshInstancesAsync(waitForCurrent: true);
        StatusMessage = failedPids.Count == 0
            ? $"Closed {pids.Length} Roblox process(es) not started by this launcher."
            : $"Could not close {failedPids.Count} of {pids.Length} outside Roblox process(es). See Diagnostics for details.";
    }

    // ---------- Helpers ----------

    private void ShowError(string title, string message, Exception? ex)
    {
        if (ex != null) Logger.Error(title, ex); else Logger.Warn($"{title}: {message}");
        StatusMessage = $"{title}: {message}";
        MessageBox.Show(Application.Current.MainWindow, message + "\n\nSee the Diagnostics tab for troubleshooting.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnLogLine(string line) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LogLines.Insert(0, line);
            while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
        });

    // Closing the window saves every setting; colours are only kept when all four are valid.
    private void SaveAllSettingsOnExit()
    {
        if (TryParseColor(BackgroundColor, out _) && TryParseColor(PanelColor, out _) &&
            TryParseColor(CardColor, out _) && TryParseColor(AccentColor, out _))
        {
            _settings.BackgroundColor = BackgroundColor;
            _settings.PanelColor = PanelColor;
            _settings.CardColor = CardColor;
            _settings.AccentColor = AccentColor;
        }
        CollectSettings();
        try { _store.SaveSettings(_settings); }
        catch (Exception ex) { Logger.Error("Could not save settings while closing", ex); }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _timer.Stop();
        _macroTimer.Stop();
        if (IsMacroPlaying) StopPlayback();
        if (_hotkeyWindow != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hotkeyWindow, 1);
            NativeMethods.UnregisterHotKey(_hotkeyWindow, 2);
            NativeMethods.UnregisterHotKey(_hotkeyWindow, 3);
            NativeMethods.UnregisterHotKey(_hotkeyWindow, 4);
            _hotkeyWindow = IntPtr.Zero;
        }
        _macroService.Dispose();
        Logger.LineWritten -= OnLogLine;
        SaveAllSettingsOnExit();
        try { _store.SaveProfiles(Profiles.Select(profile => profile.Model)); }
        catch (Exception ex) { Logger.Error("Could not save account game links while closing", ex); }
        _guard.Dispose();
    }
}
