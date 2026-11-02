using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RobloxMultiLauncher.Interop;
using RobloxMultiLauncher.ViewModels;

namespace RobloxMultiLauncher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += ApplyTitleBarTheme;
        SourceInitialized += InitializeMacroHotkeys;
        _vm.ApplyAppearanceOnStartup();
        ApplyWindowIcon();
        _vm.AppearanceChanged += (_, _) =>
        {
            ApplyTitleBarTheme(this, EventArgs.Empty);
            ApplyWindowIcon();
        };
    }

    private void ApplyWindowIcon()
    {
        if (!_vm.HasCustomLogo)
        {
            Icon = null;
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 64;
        image.UriSource = new Uri(_vm.LogoPath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        Icon = image;
    }

    private void ApplyTitleBarTheme(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        SetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        SetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ToColorRef(GetResourceColor("PanelBrush")));
        SetWindowAttribute(hwnd, NativeMethods.DWMWA_TEXT_COLOR, ToColorRef(GetResourceColor("TextBrush")));
        SetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ToColorRef(GetResourceColor("LineBrush")));
    }

    private void InitializeMacroHotkeys(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(hwnd);
        _windowSource?.AddHook(WindowMessageHook);
        _vm.InitializeMacroHotkeys(hwnd);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312)
        {
            _vm.HandleMacroHotkey(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void MacroHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string action }) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        _vm.CaptureMacroHotkey(action, key, Keyboard.Modifiers);
        e.Handled = true;
    }

    private static void SetWindowAttribute(IntPtr hwnd, int attribute, uint value)
    {
        NativeMethods.DwmSetWindowAttribute(hwnd, attribute, ref value, Marshal.SizeOf<uint>());
    }

    private Color GetResourceColor(string key) =>
        (Application.Current.Resources[key] as SolidColorBrush)?.Color ?? Colors.Black;

    private static uint ToColorRef(Color color) =>
        (uint)(color.R | (color.G << 8) | (color.B << 16));

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _windowSource?.RemoveHook(WindowMessageHook);
        _vm.Dispose();
    }
}
