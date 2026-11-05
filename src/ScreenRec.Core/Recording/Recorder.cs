using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;

namespace ScreenRec.Core.Recording;

public sealed class RecorderOptions
{
    public double FramesPerSecond { get; init; } = 2;
    public int MaxWidth { get; init; } = 1920;
    public int JpegQuality { get; init; } = 70;
    public int DisplayIndex { get; init; }
    public bool CaptureTypedText { get; init; }
    /// <summary>A frame is kept as a still when it differs from the last kept one by this much…</summary>
    public double KeepThreshold { get; init; } = 0.004;
    /// <summary>…or this long has passed since the last kept one.</summary>
    public TimeSpan KeepAtLeastEvery { get; init; } = TimeSpan.FromSeconds(10);
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Expert { get; init; } = "";
}

public sealed record RecorderStatus(TimeSpan Elapsed, int Frames, int StillFrames, int Events, bool Paused, string? Problem);

/// <summary>Records one session: the screen at a steady rate into an MJPEG AVI, the frames that matter as stills, and every
/// click, key, scroll and app switch as events. Thread-safe Pause/Resume/Note/Stop.</summary>
public sealed class Recorder : IAsyncDisposable
{
    private readonly IScreenCapture _capture;
    private readonly IInputMonitor _input;
    private readonly RecorderOptions _options;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private RecordingSession? _session;
    private StreamWriter? _events, _frames;
    private MjpegAviWriter? _avi;
    private DisplayInfo? _display;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private byte[]? _lastKeptSignature;
    private double _lastKeptAt = double.NegativeInfinity;
    private int _frameCount, _stillCount, _eventCount, _inputSinceKept;
    private ForegroundInfo _foreground = new(null, null);
    private bool _paused;
    private string? _problem;
    private readonly List<string> _warnings = new();

    public Recorder(IScreenCapture capture, IInputMonitor input, RecorderOptions options, string root)
    {
        _capture = capture;
        _input = input;
        _options = options;
        _root = root;
    }

    public RecordingSession? Session => _session;
    public bool IsRecording => _loop is { IsCompleted: false };
    public event Action<RecorderStatus>? StatusChanged;

    public RecorderStatus Status
    {
        get
        {
            lock (_gate) return new RecorderStatus(_clock.Elapsed, _frameCount, _stillCount, _eventCount, _paused, _problem);
        }
    }

    public RecordingSession Start()
    {
        if (IsRecording) throw new InvalidOperationException("Already recording.");
        if (_options.MaxWidth > 0) _capture.MaxWidth = _options.MaxWidth;
        var displays = _capture.Displays();
        if (displays.Count == 0) throw new InvalidOperationException("No display to record.");
        _display = displays[Math.Clamp(_options.DisplayIndex, 0, displays.Count - 1)];

        var started = DateTimeOffset.Now;
        _session = RecordingSession.Create(_root, new SessionManifest
        {
            StartedAt = started,
            Title = string.IsNullOrWhiteSpace(_options.Title) ? $"Recording {started.ToLocalTime():yyyy-MM-dd HH:mm}" : _options.Title.Trim(),
            Description = _options.Description.Trim(),
            Expert = _options.Expert.Trim(),
            Platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : Environment.OSVersion.Platform.ToString().ToLowerInvariant(),
            Machine = Environment.MachineName,
            FramesPerSecond = _options.FramesPerSecond,
            Status = SessionStatus.Recording,
        });
        _events = new StreamWriter(new FileStream(_session.EventsFile, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _frames = new StreamWriter(new FileStream(_session.FramesFile, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

        if (!_input.Start(OnInput))
        {
            _problem = _input.Problem ?? "Mouse and keyboard aren't being watched; only the screen is recorded.";
            _warnings.Add(_problem);
        }
        _foreground = SafeForeground();
        WriteEvent(new InputEvent { T = 0, Kind = InputKind.Focus, App = _foreground.App, Window = _foreground.Window });

        _cts = new CancellationTokenSource();
        _clock.Restart();
        _loop = Task.Factory.StartNew(() => CaptureLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        return _session;
    }

    private ForegroundInfo SafeForeground()
    {
        try { return _input.Foreground(); }
        catch (Exception) { return new ForegroundInfo(null, null); }
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private void OnInput(RawInput raw)
    {
        var display = _display;
        if (display is null) return;
        lock (_gate)
        {
            if (_paused || _events is null) return;
        }
        double? nx = null, ny = null;
        if (raw.Kind != RawInputKind.Key)
        {
            nx = Math.Round((raw.X - display.X) / display.Width, 4);
            ny = Math.Round((raw.Y - display.Y) / display.Height, 4);
            if (nx < 0 || ny < 0 || nx > 1 || ny > 1) { nx = null; ny = null; } // on another display
        }
        var e = raw.Kind switch
        {
            RawInputKind.MouseDown => new InputEvent { Kind = InputKind.MouseDown, X = nx, Y = ny, Button = raw.Button },
            RawInputKind.MouseUp => new InputEvent { Kind = InputKind.MouseUp, X = nx, Y = ny, Button = raw.Button },
            RawInputKind.Scroll => new InputEvent { Kind = InputKind.Scroll, X = nx, Y = ny, Delta = raw.Delta },
            _ => new InputEvent { Kind = InputKind.Key, Button = raw.Button, Text = _options.CaptureTypedText ? raw.Text : raw.Text is null ? null : "•" },
        };
        WriteEvent(e with { T = Math.Round(Now, 3) });
        Interlocked.Increment(ref _inputSinceKept);
    }

    private void WriteEvent(InputEvent e)
    {
        lock (_gate)
        {
            if (_events is null) return;
            _events.WriteLine(JsonSerializer.Serialize(e, RecordingSession.Line));
            _eventCount++;
        }
    }

    private async Task CaptureLoop(CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(1 / Math.Clamp(_options.FramesPerSecond, 0.2, 30));
        var next = _clock.Elapsed;
        var lastStatus = TimeSpan.Zero;
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            bool paused;
            lock (_gate) paused = _paused;
            if (!paused)
            {
                try
                {
                    CaptureOne();
                    failures = 0;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failures++;
                    lock (_gate) _problem = error.Message;
                    if (failures == 1) lock (_gate) _warnings.Add(error.Message);
                    if (failures > 20 && _frameCount == 0) throw;
                }
            }
            if (_clock.Elapsed - lastStatus > TimeSpan.FromMilliseconds(500))
            {
                lastStatus = _clock.Elapsed;
                StatusChanged?.Invoke(Status);
            }
            next += interval;
            var wait = next - _clock.Elapsed;
            if (wait < TimeSpan.Zero) { next = _clock.Elapsed; wait = TimeSpan.Zero; }
            try { await Task.Delay(wait, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void CaptureOne()
    {
        var session = _session!;
        var t = Math.Round(Now, 3);
        var fg = SafeForeground();
        if (fg.App != _foreground.App || fg.Window != _foreground.Window)
        {
            _foreground = fg;
            WriteEvent(new InputEvent { T = t, Kind = InputKind.Focus, App = fg.App, Window = fg.Window });
        }

        using var frame = _capture.Capture(_display!);
        var bitmap = frame.Bitmap;
        var jpeg = ImageTools.Jpeg(bitmap, _options.JpegQuality);

        lock (_gate)
        {
            if (_avi is null)
            {
                session.Manifest.DisplayWidth = bitmap.Width;
                session.Manifest.DisplayHeight = bitmap.Height;
                session.Manifest.VideoFile = "recording.avi";
                _avi = new MjpegAviWriter(Path.Combine(session.Folder, "recording.avi"), bitmap.Width, bitmap.Height, _options.FramesPerSecond);
            }
            if (bitmap.Width == session.Manifest.DisplayWidth && bitmap.Height == session.Manifest.DisplayHeight && !_avi.IsFull)
                _avi.WriteFrame(jpeg);
            _frameCount++;
        }

        var signature = ImageTools.Signature(bitmap);
        var change = ImageTools.Difference(_lastKeptSignature, signature);
        var inputs = Interlocked.Exchange(ref _inputSinceKept, 0);
        var keep = change >= _options.KeepThreshold || inputs > 0 || t - _lastKeptAt >= _options.KeepAtLeastEvery.TotalSeconds;
        if (!keep) return;

        var index = _stillCount;
        var name = $"{index:D6}.jpg";
        File.WriteAllBytes(Path.Combine(session.FramesFolder, name), jpeg);
        var info = new FrameInfo
        {
            Index = index, T = t, File = name, Width = bitmap.Width, Height = bitmap.Height,
            App = fg.App, Window = fg.Window, Change = Math.Round(Math.Min(1, change), 4),
        };
        lock (_gate)
        {
            _frames!.WriteLine(JsonSerializer.Serialize(info, RecordingSession.Line));
            _stillCount++;
        }
        _lastKeptSignature = signature;
        _lastKeptAt = t;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused || !IsRecording) return;
            _paused = true;
            _clock.Stop();
        }
        WriteEvent(new InputEvent { T = Math.Round(Now, 3), Kind = InputKind.Pause });
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            _clock.Start();
        }
        WriteEvent(new InputEvent { T = Math.Round(Now, 3), Kind = InputKind.Resume });
        Interlocked.Increment(ref _inputSinceKept); // keep the first frame after a pause
    }

    /// <summary>A spoken-style remark the expert adds ("this total must match the bank statement").</summary>
    public void AddNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        WriteEvent(new InputEvent { T = Math.Round(Now, 3), Kind = InputKind.Note, Text = text.Trim() });
        Interlocked.Increment(ref _inputSinceKept);
    }

    public async Task<RecordingSession> StopAsync()
    {
        var session = _session ?? throw new InvalidOperationException("Not recording.");
        _cts?.Cancel();
        Exception? failure = null;
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { failure = e; }
        _input.Stop();
        _clock.Stop();
        lock (_gate)
        {
            _avi?.Dispose();
            _avi = null;
            _events?.Dispose();
            _frames?.Dispose();
            _events = _frames = null;
            var m = session.Manifest;
            m.EndedAt = DateTimeOffset.Now;
            m.DurationSeconds = Math.Round(_clock.Elapsed.TotalSeconds, 1);
            m.FrameCount = _stillCount;
            m.EventCount = _eventCount;
            if (m.DurationSeconds > 10 && _problem is null && session.ReadEvents().All(e => e.Kind is InputKind.Focus or InputKind.Note or InputKind.Pause or InputKind.Resume))
                _warnings.Add(OperatingSystem.IsMacOS()
                    ? "No clicks or key presses were seen. If you did click, allow this app in System Settings › Privacy & Security › Input Monitoring and reopen it. The AI will work from screen changes alone."
                    : "No clicks or key presses were seen. The AI will work from screen changes alone.");
            m.Warnings = _warnings.Distinct().ToList();
            m.Status = failure is null || _stillCount > 0 ? SessionStatus.Recorded : SessionStatus.Failed;
            m.LastError = failure?.Message;
            if (_frameCount == 0 && m.VideoFile is null) m.LastError ??= "No frames were captured.";
            session.Save();
        }
        StatusChanged?.Invoke(Status);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRecording) await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
