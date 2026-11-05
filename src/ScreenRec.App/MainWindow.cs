using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ScreenRec.Core;
using ScreenRec.Core.Ai;
using ScreenRec.Core.Export;
using ScreenRec.Core.Guardian;
using ScreenRec.Core.Recording;
using ScreenRec.Core.Storage;

namespace ScreenRec.App;

/// <summary>The whole app in one window: Record · Recordings · Expert · Guardian · Settings.</summary>
public sealed partial class MainWindow : Window
{
    private readonly Workspace _ws;
    private readonly TabControl _tabs = new();
    private readonly TextBlock _accountBadge = new() { VerticalAlignment = VerticalAlignment.Center };

    public MainWindow()
    {
        Title = "ScreenRec Trainer";
        Width = 1100;
        Height = 760;
        MinWidth = 820;
        MinHeight = 560;
        _ws = new Workspace();

        var header = new DockPanel { Margin = new Thickness(16, 12, 16, 4) };
        var title = new StackPanel { Orientation = Orientation.Vertical };
        title.Children.Add(new TextBlock { Text = "ScreenRec Trainer", Classes = { "h1" } });
        title.Children.Add(new TextBlock { Text = "Record how an expert works → AI turns it into training, a Guardian skill and a Guardian automation.", Classes = { "muted" } });
        DockPanel.SetDock(_accountBadge, Dock.Right);
        header.Children.Add(_accountBadge);
        header.Children.Add(title);

        _tabs.Margin = new Thickness(8, 0, 8, 8);
        _tabs.Items.Add(new TabItem { Header = "Record", Content = BuildRecordTab() });
        _tabs.Items.Add(new TabItem { Header = "Recordings", Content = BuildSessionsTab() });
        _tabs.Items.Add(new TabItem { Header = "Expert", Content = BuildExpertTab() });
        _tabs.Items.Add(new TabItem { Header = "Guardian", Content = BuildGuardianTab() });
        _tabs.Items.Add(new TabItem { Header = "Settings", Content = BuildSettingsTab() });
        _tabs.SelectionChanged += (_, e) =>
        {
            // Inner lists' SelectionChanged bubbles up here too: only react to the tabs themselves.
            if (!ReferenceEquals(e.Source, _tabs)) return;
            if (_tabs.SelectedIndex == 1 && _work is null) RefreshSessions();
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_tabs);
        Content = root;

        RefreshAccount();
        Closing += async (_, e) =>
        {
            if (_recorder?.IsRecording == true)
            {
                e.Cancel = true;
                await StopRecordingAsync();
                Close();
            }
        };
        if (_ws.IsSignedIn) _ = RefreshGuardianAsync();
    }

    // MARK: Shared helpers

    internal static TextBlock Label(string text, string? cls = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (cls is not null) t.Classes.Add(cls);
        return t;
    }

    internal static StackPanel Stack(double spacing = 8, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    internal static StackPanel Row(double spacing = 8, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    internal static Control Field(string label, Control input, string? hint = null)
    {
        var s = new StackPanel { Spacing = 4 };
        s.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        s.Children.Add(input);
        if (hint is not null) s.Children.Add(Label(hint, "muted"));
        return s;
    }

    internal static Border Card(Control content) => new() { Classes = { "card" }, Child = content, Margin = new Thickness(0, 0, 0, 12) };

    internal static ScrollViewer Page(Control content) => new() { Content = new Border { Padding = new Thickness(12), Child = content } };

    private static void Open(string pathOrUrl)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", [pathOrUrl])?.Dispose();
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true })?.Dispose();
            else Process.Start("xdg-open", [pathOrUrl])?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static void Ui(Action action) => Dispatcher.UIThread.Post(action);

    private void RefreshAccount()
    {
        _accountBadge.Text = _ws.GuardianAccount is { } a
            ? $"Guardian: {a.Label}{(a.OrgName is { } o ? $" · {o}" : "")}"
            : "Guardian: not signed in";
        _accountBadge.Opacity = _ws.IsSignedIn ? 1 : 0.6;
        RefreshGuardianPanel();
        UpdatePublishEnabled();
    }

    // MARK: Self-test (renders every tab to PNG; exits non-zero on an exception)

    public void RunSelfTest(string? folder)
    {
        folder ??= Path.Combine(Path.GetTempPath(), "screenrec-selftest");
        Directory.CreateDirectory(folder);
        Opened += async (_, _) =>
        {
            var code = 0;
            try
            {
                for (var i = 0; i < _tabs.ItemCount; i++)
                {
                    _tabs.SelectedIndex = i;
                    await Task.Delay(400);
                    var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96, 96));
                    bitmap.Render(this);
                    var name = ((TabItem)_tabs.Items[i]!).Header?.ToString()?.ToLowerInvariant() ?? i.ToString();
                    bitmap.Save(Path.Combine(folder, $"{i}-{name}.png"));
                }
                if (_sessions.Count > 0)
                {
                    _tabs.SelectedIndex = 1;
                    _sessionList.SelectedIndex = 0;
                    await Task.Delay(400);
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
                    bitmap.Render(this);
                    bitmap.Save(Path.Combine(folder, "1-recordings-selected.png"));
                }
                Console.WriteLine($"self-test: rendered {_tabs.ItemCount} tabs to {folder}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("self-test failed: " + e);
                code = 1;
            }
            Environment.Exit(code);
        };
    }
}
