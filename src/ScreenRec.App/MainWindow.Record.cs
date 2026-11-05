using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using ScreenRec.Core.Platform;
using ScreenRec.Core.Recording;
using ScreenRec.Core.Storage;

namespace ScreenRec.App;

public sealed partial class MainWindow
{
    private Recorder? _recorder;
    private IScreenCapture? _capture;
    private IInputMonitor? _input;
    private readonly TextBox _recExpert = new() { PlaceholderText = "Who is being recorded (e.g. Maria Lopez)" };
    private readonly TextBox _recTitle = new() { PlaceholderText = "Task name (e.g. Month-end vendor reconciliation)" };
    private readonly TextBox _recAbout = new() { PlaceholderText = "In a sentence or two: what are you about to do, and why?", AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 64 };
    private readonly ComboBox _recDisplay = new() { MinWidth = 260 };
    private readonly CheckBox _recHide = new() { Content = "Minimize this window while recording", IsChecked = true };
    private readonly Button _recStart = new() { Content = "● Start recording", FontSize = 16, Padding = new Thickness(18, 10) };
    private readonly Button _recPause = new() { Content = "Pause", IsEnabled = false, Padding = new Thickness(14, 10) };
    private readonly Button _recStop = new() { Content = "■ Stop", IsEnabled = false, Padding = new Thickness(14, 10) };
    private readonly TextBox _recNote = new() { PlaceholderText = "Add a note while recording (\"now I check the totals match the bank statement\")", MinWidth = 420 };
    private readonly Button _recAddNote = new() { Content = "Add note", IsEnabled = false };
    private readonly TextBlock _recStatus = Label("Not recording.");
    private readonly TextBlock _recProblem = Label("", "error");
    private readonly StackPanel _permissions = new() { Spacing = 8 };
    private readonly DispatcherTimer _recTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private Control BuildRecordTab()
    {
        _recExpert.Text = _ws.Settings.ExpertName;
        _recStart.Click += async (_, _) => await StartRecordingAsync();
        _recStop.Click += async (_, _) => await StopRecordingAsync();
        _recPause.Click += (_, _) =>
        {
            if (_recorder is null) return;
            if (_recorder.Status.Paused) { _recorder.Resume(); _recPause.Content = "Pause"; }
            else { _recorder.Pause(); _recPause.Content = "Resume"; }
        };
        _recAddNote.Click += (_, _) => AddNote();
        _recNote.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) AddNote(); };
        _recTimer.Tick += (_, _) => ShowRecorderStatus();

        try
        {
            _capture = PlatformRecording.CreateCapture();
            var displays = _capture.Displays();
            _recDisplay.ItemsSource = displays.Select(d => $"{d.Name} — {d.Width:0}×{d.Height:0}").ToList();
            _recDisplay.SelectedIndex = Math.Clamp(_ws.Settings.DisplayIndex, 0, Math.Max(0, displays.Count - 1));
        }
        catch (Exception e) when (e is PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            _recProblem.Text = e.Message;
            _recStart.IsEnabled = false;
        }
        BuildPermissions();

        var form = Stack(12,
            Label("Record a task", "h2"),
            Label("Do the task the way you normally do it. Clicks, keys, app switches and the screen are recorded on this computer only — nothing leaves it until you process or publish. Talk the AI through the tricky parts with notes.", "muted"),
            Field("Expert", _recExpert),
            Field("Task", _recTitle),
            Field("What you're doing", _recAbout, "The single most useful hint for the AI. Mention the goal and anything you check."),
            Field("Screen", _recDisplay),
            _recHide);

        var controls = Stack(12,
            Row(10, _recStart, _recPause, _recStop),
            _recStatus,
            _recProblem,
            Row(8, _recNote, _recAddNote));

        return Page(Stack(0, Card(form), Card(controls), Card(_permissions)));
    }

    private void BuildPermissions()
    {
        _permissions.Children.Clear();
        _permissions.Children.Add(Label("Permissions", "h2"));
        if (OperatingSystem.IsMacOS())
        {
            var screen = MacScreenCapture.HasPermission();
            var input = MacInputMonitor.HasPermission();
            _permissions.Children.Add(Label($"Screen Recording: {(screen ? "allowed ✓" : "not allowed — frames will be blank or missing")}"));
            _permissions.Children.Add(Label($"Input Monitoring (clicks and keys): {(input ? "allowed ✓" : "not allowed — only the screen is recorded")}"));
            var ask = new Button { Content = "Ask macOS for access" };
            ask.Click += (_, _) =>
            {
                if (!OperatingSystem.IsMacOS()) return;
                MacScreenCapture.RequestPermission();
                MacInputMonitor.RequestPermission();
                BuildPermissions();
            };
            var settings = new Button { Content = "Open Privacy & Security" };
            settings.Click += (_, _) => Open("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");
            _permissions.Children.Add(Row(8, ask, settings));
            _permissions.Children.Add(Label("After allowing, quit and reopen the app. If it runs from Terminal, allow Terminal instead.", "muted"));
        }
        else if (OperatingSystem.IsWindows())
        {
            _permissions.Children.Add(Label("Windows needs no extra permission. Windows running as administrator can't be watched unless this app also runs as administrator.", "muted"));
        }
    }

    private async Task StartRecordingAsync()
    {
        if (_capture is null) return;
        _recProblem.Text = "";
        _ws.Settings.ExpertName = _recExpert.Text?.Trim() ?? "";
        _ws.Settings.DisplayIndex = Math.Max(0, _recDisplay.SelectedIndex);
        _ws.Settings.Save();
        _capture.MaxWidth = _ws.Settings.FrameMaxWidth;
        _input = PlatformRecording.CreateInputMonitor();
        _recorder = new Recorder(_capture, _input, new RecorderOptions
        {
            FramesPerSecond = _ws.Settings.FramesPerSecond,
            MaxWidth = _ws.Settings.FrameMaxWidth,
            JpegQuality = _ws.Settings.JpegQuality,
            DisplayIndex = _ws.Settings.DisplayIndex,
            CaptureTypedText = _ws.Settings.CaptureTypedText,
            Title = _recTitle.Text ?? "",
            Description = _recAbout.Text ?? "",
            Expert = _ws.Settings.ExpertName,
        }, AppPaths.Sessions);
        try
        {
            await Task.Run(() => _recorder.Start());
        }
        catch (Exception e)
        {
            _recProblem.Text = e.Message;
            _recorder = null;
            return;
        }
        _recStart.IsEnabled = false;
        _recStop.IsEnabled = _recPause.IsEnabled = _recAddNote.IsEnabled = true;
        _recPause.Content = "Pause";
        _recTimer.Start();
        ShowRecorderStatus();
        if (_recHide.IsChecked == true) WindowState = WindowState.Minimized;
    }

    private void AddNote()
    {
        if (_recorder is null || string.IsNullOrWhiteSpace(_recNote.Text)) return;
        _recorder.AddNote(_recNote.Text);
        _recNote.Text = "";
    }

    private void ShowRecorderStatus()
    {
        if (_recorder is null) return;
        var s = _recorder.Status;
        _recStatus.Text = $"{(s.Paused ? "Paused" : "● Recording")} — {s.Elapsed:hh\\:mm\\:ss} · {s.StillFrames} key frames · {s.Events} events";
        if (s.Problem is { Length: > 0 } p) _recProblem.Text = p;
    }

    private async Task StopRecordingAsync()
    {
        if (_recorder is null) return;
        _recTimer.Stop();
        _recStop.IsEnabled = _recPause.IsEnabled = _recAddNote.IsEnabled = false;
        _recStatus.Text = "Saving…";
        RecordingSession session;
        try
        {
            session = await _recorder.StopAsync();
        }
        catch (Exception e)
        {
            _recProblem.Text = e.Message;
            _recStart.IsEnabled = true;
            return;
        }
        finally
        {
            _input?.Dispose();
            _input = null;
        }
        await _recorder.DisposeAsync();
        _recorder = null;
        _recStart.IsEnabled = true;
        var m = session.Manifest;
        _recStatus.Text = $"Saved \"{m.Title}\": {m.DurationSeconds:0} s, {m.FrameCount} key frames, {m.EventCount} events. Find it under Recordings to process it.";
        if (m.Warnings.Count > 0) _recProblem.Text = string.Join("\n", m.Warnings);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        RefreshSessions(select: session.Id);
        _tabs.SelectedIndex = 1;
    }
}
