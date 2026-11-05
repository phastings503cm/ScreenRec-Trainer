using System.Text;
using ScreenRec.Core.Ai;
using ScreenRec.Core.Recording;
using ScreenRec.Core.Storage;

namespace ScreenRec.Core.Tests;

public class RecordingTests
{
    [Fact]
    public void Avi_has_valid_riff_structure_and_index()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a.avi");
        var frame = new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 }; // odd length → padded
        using (var w = new MjpegAviWriter(path, 320, 200, 2))
        {
            w.WriteFrame(frame);
            w.WriteFrame(frame);
            w.WriteFrame(frame);
        }
        var b = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(b, 0, 4));
        Assert.Equal("AVI ", Encoding.ASCII.GetString(b, 8, 4));
        Assert.Equal(b.Length - 8, BitConverter.ToInt32(b, 4));
        var avih = IndexOf(b, "avih");
        Assert.Equal(500_000, BitConverter.ToInt32(b, avih + 8)); // µs per frame at 2 fps
        Assert.Equal(3, BitConverter.ToInt32(b, avih + 8 + 16)); // total frames
        var idx = IndexOf(b, "idx1");
        Assert.Equal(3 * 16, BitConverter.ToInt32(b, idx + 4));
        // The first index entry points at the first 00dc chunk (offset from 'movi').
        var movi = IndexOf(b, "movi");
        var firstOffset = BitConverter.ToInt32(b, idx + 8 + 8);
        Assert.Equal("00dc", Encoding.ASCII.GetString(b, movi + firstOffset, 4));
    }

    private static int IndexOf(byte[] b, string tag)
    {
        var t = Encoding.ASCII.GetBytes(tag);
        for (var i = 0; i < b.Length - 4; i++)
            if (b[i] == t[0] && b[i + 1] == t[1] && b[i + 2] == t[2] && b[i + 3] == t[3]) return i;
        return -1;
    }

    [Fact]
    public async Task Recorder_records_synthetic_screen_events_and_notes()
    {
        using var dir = new TempDir();
        using var capture = new SyntheticScreenCapture(400, 300);
        var input = new ScriptedInput();
        await using var recorder = new Recorder(capture, input, new RecorderOptions { FramesPerSecond = 10, Title = "T", Expert = "Maria" }, dir.Path);
        var session = recorder.Start();
        await Task.Delay(300);
        input.Fire(new RawInput(RawInputKind.MouseDown, 100, 150, "left", null, 0, DateTimeOffset.Now));
        input.Fire(new RawInput(RawInputKind.Key, 0, 0, "A", "a", 0, DateTimeOffset.Now));
        recorder.AddNote("check totals");
        recorder.Pause();
        input.Fire(new RawInput(RawInputKind.MouseDown, 1, 1, "left", null, 0, DateTimeOffset.Now)); // ignored while paused
        recorder.Resume();
        for (var i = 0; i < 50 && recorder.Status.StillFrames < 2; i++) await Task.Delay(100);
        session = await recorder.StopAsync();

        Assert.Equal(SessionStatus.Recorded, session.Manifest.Status);
        Assert.True(session.Manifest.FrameCount >= 2);
        Assert.True(File.Exists(session.VideoPath));
        var events = session.ReadEvents();
        var click = Assert.Single(events, e => e.Kind == InputKind.MouseDown);
        Assert.Equal(0.25, click.X);
        Assert.Equal(0.5, click.Y);
        Assert.Equal("•", events.Single(e => e.Kind == InputKind.Key).Text); // typed text masked by default
        Assert.Contains(events, e => e.Kind == InputKind.Note && e.Text == "check totals");
        Assert.Contains(events, e => e.Kind == InputKind.Pause);
        Assert.Equal("Maria", RecordingSession.Open(session.Folder)!.Manifest.Expert);
    }

    private sealed class ScriptedInput : IInputMonitor
    {
        private Action<RawInput>? _on;
        public string Name => "scripted";
        public string? Problem => null;
        public bool Start(Action<RawInput> onInput) { _on = onInput; return true; }
        public void Stop() => _on = null;
        public ForegroundInfo Foreground() => new("TestApp", "Window");
        public void Fire(RawInput input) => _on?.Invoke(input);
        public void Dispose() { }
    }

    [Fact]
    public void Segmenter_builds_meaningful_steps()
    {
        using var dir = new TempDir();
        var s = SampleSessions.Create(dir.Path);
        var steps = Segmenter.Segment(s.ReadEvents(), s.ReadFrames());
        Assert.Equal(["click", "type", "note", "shortcut", "switch-app", "scroll"], steps.Select(x => x.Kind));
        Assert.Equal("ab", steps[1].Text);
        Assert.Contains("Enter", steps[1].Keys);
        Assert.All(steps, x => Assert.NotNull(x.Before));
        Assert.Equal(Enumerable.Range(1, steps.Count), steps.Select(x => x.Index));
        Assert.Equal("QuickBooks", steps[0].App);
        Assert.Equal("Excel", steps[^1].App);
        Assert.True(steps[0].After!.T >= steps[0].End + 0.6);
    }

    [Fact]
    public void Segmenter_double_click_drag_and_thinning()
    {
        var ev = new List<InputEvent>
        {
            new() { T = 1, Kind = InputKind.MouseDown, X = .1, Y = .1, Button = "left" },
            new() { T = 1.05, Kind = InputKind.MouseUp, X = .1, Y = .1, Button = "left" },
            new() { T = 1.2, Kind = InputKind.MouseDown, X = .1, Y = .1, Button = "left" },
            new() { T = 1.25, Kind = InputKind.MouseUp, X = .1, Y = .1, Button = "left" },
            new() { T = 3, Kind = InputKind.MouseDown, X = .1, Y = .1, Button = "left" },
            new() { T = 3.5, Kind = InputKind.MouseUp, X = .6, Y = .6, Button = "left" },
        };
        var steps = Segmenter.Segment(ev, [new FrameInfo { T = 0, File = "a" }]);
        Assert.Equal(["double-click", "drag"], steps.Select(s => s.Kind));

        var many = Enumerable.Range(0, 200).Select(i => new InputEvent { T = i * 5, Kind = InputKind.MouseDown, X = .5, Y = .5, Button = "left" }).ToList();
        many.Add(new InputEvent { T = 333, Kind = InputKind.Note, Text = "important" });
        var thinned = Segmenter.Segment(many, [new FrameInfo { T = 0, File = "a" }], maxSteps: 20);
        Assert.True(thinned.Count <= 21);
        Assert.Contains(thinned, s => s.Kind == "note");
        Assert.Equal(0, thinned[0].Start);
        Assert.Equal(995, thinned.Where(s => s.Kind == "click").Max(s => s.Start));
    }

    [Fact]
    public void Segmenter_without_input_uses_screen_changes()
    {
        var frames = new List<FrameInfo>
        {
            new() { Index = 0, T = 0, File = "0", Change = 1 },
            new() { Index = 1, T = 1, File = "1", Change = 0.001 },
            new() { Index = 2, T = 2, File = "2", Change = 0.3 },
        };
        var steps = Segmenter.Segment([new InputEvent { Kind = InputKind.Focus, App = "X" }], frames);
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal("observe", s.Kind));
    }

    [Fact]
    public void Model_images_are_scaled_and_marked()
    {
        using var dir = new TempDir();
        var s = SampleSessions.Create(dir.Path);
        var frame = s.ReadFrames()[0];
        var jpeg = ImageTools.PrepareForModel(s.FramePath(frame), 320, [(0.5, 0.5)]);
        using var bmp = SkiaSharp.SKBitmap.Decode(jpeg);
        Assert.Equal(320, bmp.Width);
        Assert.Equal(200, bmp.Height);
        var center = bmp.GetPixel(160 + 14, 100);
        Assert.True(center.Red > 180 && center.Green < 120, $"expected the red ring, got {center}");
    }

    [Fact]
    public void Sessions_list_newest_first_and_survive_a_truncated_line()
    {
        using var dir = new TempDir();
        var a = SampleSessions.Create(dir.Path);
        File.AppendAllText(a.EventsFile, "{\"t\":99,\"kind\":\"Mou");
        Assert.Equal(10, a.ReadEvents().Count);
        Assert.Single(RecordingSession.List(dir.Path));
    }

    [Fact]
    public void File_secret_store_round_trips_encrypted()
    {
        using var dir = new TempDir();
        var store = new FileSecretStore(dir.Path);
        store.Write("guardian/token", "tok-123");
        Assert.Equal("tok-123", new FileSecretStore(dir.Path).Read("guardian/token"));
        Assert.DoesNotContain("tok-123", File.ReadAllText(Path.Combine(dir.Path, "secrets.dat")));
        store.Delete("guardian/token");
        Assert.Null(store.Read("guardian/token"));
    }

    [Fact]
    public void Settings_round_trip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "s.json");
        new AppSettings { Provider = AiProvider.Guardian, SparkBaseUrl = "http://spark:8888/v1" }.Save(path);
        var back = AppSettings.Load(path);
        Assert.Equal(AiProvider.Guardian, back.Provider);
        Assert.Equal("http://spark:8888/v1", back.SparkBaseUrl);
        File.WriteAllText(path, "{broken");
        Assert.Equal(AiProvider.Spark, AppSettings.Load(path).Provider);
    }
}
