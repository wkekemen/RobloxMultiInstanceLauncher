using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RobloxMultiLauncher.Services;

namespace RobloxMultiLauncher.Views;

public sealed class ModsView : UserControl
{
    private readonly TextBlock _install = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) };
    private readonly CheckBox _graySky = new() { Content = "Gray sky" };
    private readonly TextBox _fontPath = new();
    private string? _versionDir;

    public ModsView()
    {
        Content = BuildUi();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _versionDir = RobloxModService.FindLatestVersionDir();
        _install.Text = _versionDir is null ? "Roblox installation not found." : "Target: " + _versionDir;
        if (_versionDir is null) return;

        _graySky.IsChecked = RobloxModService.GetGraySky(_versionDir);
    }

    private void Run(Action<string> action, string done)
    {
        if (_versionDir is null) { _status.Text = "Roblox installation not found."; return; }
        try { action(_versionDir); _status.Text = done + " Restart Roblox for it to take effect."; }
        catch (Exception ex) { _status.Text = "Failed: " + ex.Message; }
    }

    private static TextBlock Label(string text, bool muted = false, double size = 12, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
        return t;
    }

    private static Border Card(string title, string? hint, params UIElement[] children)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(title, weight: FontWeights.SemiBold, size: 15));
        if (hint is not null) panel.Children.Add(Label(hint, muted: true));
        foreach (var c in children) panel.Children.Add(c);

        var border = new Border { Child = panel, Margin = new Thickness(0, 0, 0, 12), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        if (Application.Current.TryFindResource("Card") is Style style) border.Style = style;
        return border;
    }

    private static Button Btn(string text, RoutedEventHandler click, bool accent = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        if (accent && Application.Current.TryFindResource("AccentButton") is Style style) b.Style = style;
        b.Click += click;
        return b;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    private UIElement BuildUi()
    {
        _graySky.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _install.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var browse = Btn("Browse...", (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Fonts (*.ttf;*.otf)|*.ttf;*.otf" };
            if (dlg.ShowDialog() == true) _fontPath.Text = dlg.FileName;
        });
        browse.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        var fontRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        fontRow.Children.Add(browse);
        fontRow.Children.Add(_fontPath);

        var panel = new StackPanel();
        panel.Children.Add(Label("Roblox Mods", size: 22, weight: FontWeights.SemiBold));
        panel.Children.Add(Label("Client tweaks applied to the newest installed Roblox version.", muted: true));
        panel.Children.Add(new Border { Height = 12 });

        panel.Children.Add(Card("Installation", null, _install, Row(Btn("Re-detect", (_, _) => Refresh()))));

        panel.Children.Add(Card("Sky", null, _graySky, Row(Btn("Apply", (_, _) =>
            Run(d => RobloxModService.SetGraySky(d, _graySky.IsChecked == true),
                _graySky.IsChecked == true ? "Gray sky enabled." : "Gray sky disabled."), true))));

        panel.Children.Add(Card("Custom font", "Replaces every in-game font with the .ttf/.otf you pick.", fontRow,
            Row(Btn("Apply font", (_, _) =>
                    Run(d => RobloxModService.ApplyFont(d, _fontPath.Text.Trim()), "Custom font applied."), true),
                Btn("Restore default font", (_, _) => Run(RobloxModService.RestoreFont, "Default font restored.")))));

        panel.Children.Add(_status);
        panel.Children.Add(Label("Roblox updates create a new version folder, so re-apply after each update.", muted: true));

        return new ScrollViewer { Content = panel, Margin = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
