using System.Windows.Media;
using RobloxMultiLauncher.Models;

namespace RobloxMultiLauncher.ViewModels;

public sealed class ProfileViewModel : ObservableObject
{
    private static Brush B(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
    private static readonly Brush Good = B("#3DDC97"), Accent = B("#5B8CFF"), Bad = B("#FF6B6B"), Muted = B("#8B93A7");

    public StoredProfile Model { get; }

    private string _status;
    private bool _running, _busy, _needsLogin;
    private int? _pid;
    private string _lastError = "";

    public ProfileViewModel(StoredProfile model)
    {
        Model = model;
        _needsLogin = string.IsNullOrEmpty(model.ProtectedCookie);
        _status = _needsLogin ? "Needs sign-in" : "Ready";
    }

    public string DisplayName => Model.DisplayName;
    public string UserIdText => $"User ID {Model.UserId}";
    public string PlaceInput
    {
        get => Model.PlaceInput;
        set
        {
            if (string.Equals(Model.PlaceInput, value, StringComparison.Ordinal)) return;
            Model.PlaceInput = value;
            OnPropertyChanged();
        }
    }

    public string StatusText { get => _status; set { if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusBrush)); } }
    public string LastError { get => _lastError; set => Set(ref _lastError, value); }
    public int? Pid { get => _pid; set => Set(ref _pid, value); }

    public bool IsRunning
    {
        get => _running;
        set { if (Set(ref _running, value)) { OnPropertyChanged(nameof(CanLaunch)); OnPropertyChanged(nameof(StatusBrush)); } }
    }

    public bool IsBusy
    {
        get => _busy;
        set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(CanLaunch)); OnPropertyChanged(nameof(StatusBrush)); } }
    }

    public bool NeedsLogin
    {
        get => _needsLogin;
        set { if (Set(ref _needsLogin, value)) { OnPropertyChanged(nameof(CanLaunch)); OnPropertyChanged(nameof(StatusBrush)); } }
    }

    public bool CanLaunch => !IsBusy && !IsRunning && !NeedsLogin;

    public Brush StatusBrush =>
        IsRunning ? Good :
        IsBusy ? Accent :
        NeedsLogin || StatusText.Contains("fail", StringComparison.OrdinalIgnoreCase) ? Bad : Muted;

    public void RefreshIdentity()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(UserIdText));
    }
}
