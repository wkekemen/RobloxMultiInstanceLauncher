using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using RobloxMultiLauncher.Services;

namespace RobloxMultiLauncher.Views;

/// <summary>
/// Embedded browser login. Uses a throw-away WebView2 profile so each account starts logged out, captures the
/// .ROBLOSECURITY cookie once Roblox sets it, then deletes the temporary browser profile.
/// </summary>
public partial class LoginWindow : Window
{
    private string? _dataDir;
    private DispatcherTimer? _timer;
    private bool _started, _checking;

    public string? Cookie { get; private set; }

    public LoginWindow() => InitializeComponent();

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_started) return;
        _started = true;
        try
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "RML_login_" + Guid.NewGuid().ToString("N"));
            var env = await CoreWebView2Environment.CreateAsync(null, _dataDir);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.Navigate("https://www.roblox.com/login");

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (_, _) => await PollAsync();
            _timer.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("WebView2 initialisation failed", ex);
            MessageBox.Show(this,
                "The embedded browser could not start. Install the Microsoft Edge WebView2 Runtime (https://developer.microsoft.com/microsoft-edge/webview2/) and try again.\n\n" + ex.Message,
                "Sign-in unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
    }

    private async Task PollAsync()
    {
        if (_checking || Web.CoreWebView2 == null) return;
        _checking = true;
        try
        {
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            var c = cookies.FirstOrDefault(x => x.Name == ".ROBLOSECURITY");
            if (c != null && !string.IsNullOrEmpty(c.Value))
            {
                Cookie = c.Value;
                _timer?.Stop();
                DialogResult = true;
            }
        }
        catch (Exception ex) { Logger.Warn("Cookie check failed: " + ex.Message); }
        finally { _checking = false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer?.Stop();
        try { Web.Dispose(); } catch { }
        var dir = _dataDir;
        if (dir == null) return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(1500);
                try { Directory.Delete(dir, true); return; } catch { }
            }
        });
    }
}
