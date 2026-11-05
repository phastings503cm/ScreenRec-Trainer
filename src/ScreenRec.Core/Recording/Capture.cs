using SkiaSharp;

namespace ScreenRec.Core.Recording;

/// <summary>A display that can be recorded. Bounds are in the OS's input coordinate space (points on macOS, pixels on
/// Windows), which is what mouse events are reported in.</summary>
public sealed record DisplayInfo(int Index, string Name, double X, double Y, double Width, double Height, bool IsMain);

public sealed record CapturedFrame(SKBitmap Bitmap) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

public interface IScreenCapture : IDisposable
{
    string Name { get; }
    /// <summary>The widest a frame is kept (bigger displays are scaled down; 0 = native).</summary>
    int MaxWidth { get; set; }
    IReadOnlyList<DisplayInfo> Displays();
    /// <summary>Grab one frame of the display (with the mouse pointer when the platform allows).</summary>
    CapturedFrame Capture(DisplayInfo display);
}

/// <summary>What is in front: the app and its window title.</summary>
public sealed record ForegroundInfo(string? App, string? Window);

public interface IInputMonitor : IDisposable
{
    string Name { get; }
    /// <summary>False when the OS refused (macOS Input Monitoring / Accessibility not granted).</summary>
    bool Start(Action<RawInput> onInput);
    void Stop();
    ForegroundInfo Foreground();
    /// <summary>Why input couldn't be watched, for the person to fix.</summary>
    string? Problem { get; }
}

public enum RawInputKind { MouseDown, MouseUp, Scroll, Key }

/// <summary>An input in the OS's global coordinates (see <see cref="DisplayInfo"/>).</summary>
public readonly record struct RawInput(RawInputKind Kind, double X, double Y, string? Button, string? Text, int Delta, DateTimeOffset At);

public static class PlatformRecording
{
    public static IScreenCapture CreateCapture()
    {
        if (OperatingSystem.IsWindows()) return new Platform.WindowsScreenCapture();
        if (OperatingSystem.IsMacOS()) return new Platform.MacScreenCapture();
        throw new PlatformNotSupportedException("Screen recording is supported on Windows and macOS.");
    }

    public static IInputMonitor CreateInputMonitor()
    {
        if (OperatingSystem.IsWindows()) return new Platform.WindowsInputMonitor();
        if (OperatingSystem.IsMacOS()) return new Platform.MacInputMonitor();
        return new NullInputMonitor();
    }
}

public sealed class NullInputMonitor : IInputMonitor
{
    public string Name => "none";
    public bool Start(Action<RawInput> onInput) => false;
    public void Stop() { }
    public ForegroundInfo Foreground() => new(null, null);
    public string? Problem => "Input can't be watched on this platform; only the screen is recorded.";
    public void Dispose() { }
}

/// <summary>Draws a moving test pattern (tests, and the CLI's --synthetic).</summary>
public sealed class SyntheticScreenCapture(int width = 1280, int height = 800) : IScreenCapture
{
    private int _n;
    public string Name => "synthetic";
    public int MaxWidth { get; set; }
    public IReadOnlyList<DisplayInfo> Displays() => [new DisplayInfo(0, "Synthetic", 0, 0, width, height, true)];

    public CapturedFrame Capture(DisplayInfo display)
    {
        var bmp = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(240, 240, 245));
        using var paint = new SKPaint { Color = new SKColor(40, 90, 200), IsAntialias = true };
        var step = _n++ / 4;
        canvas.DrawRect(20 + step * 37 % (width - 220), 60, 200, 120, paint);
        using var font = new SKFont(SKTypeface.Default, 28);
        using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText($"Synthetic screen — step {step}", 20, 40, font, text);
        return new CapturedFrame(bmp);
    }

    public void Dispose() { }
}
