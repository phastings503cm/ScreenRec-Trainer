using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenRec.Core.Recording;
using SkiaSharp;
using static ScreenRec.Core.Platform.MacNative;

namespace ScreenRec.Core.Platform;

/// <summary>macOS screen capture with CoreGraphics (CGDisplayCreateImage), drawn straight into a Skia bitmap at the size the
/// recorder wants. The pointer isn't in CoreGraphics' image, so it is drawn on afterwards. When CoreGraphics gives nothing
/// (no Screen Recording permission, or a macOS that dropped the call) it falls back to /usr/sbin/screencapture.</summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacScreenCapture : IScreenCapture
{
    /// <summary>The widest a frame is kept; bigger displays are scaled down while drawing (0 = native pixels).</summary>
    public int MaxWidth { get; set; } = 1920;

    public string Name => "CoreGraphics";

    public static bool HasPermission() => CGPreflightScreenCaptureAccess();

    /// <summary>Shows the system prompt (once) and returns whether access is already granted.</summary>
    public static bool RequestPermission() => CGRequestScreenCaptureAccess();

    private uint[] DisplayIds()
    {
        var ids = new uint[16];
        uint count;
        fixed (uint* p = ids)
        {
            if (CGGetActiveDisplayList(16, p, out count) != 0 || count == 0) return [CGMainDisplayID()];
        }
        var main = CGMainDisplayID();
        return ids[..(int)count].OrderBy(id => id == main ? 0 : 1).ToArray();
    }

    public IReadOnlyList<DisplayInfo> Displays()
    {
        var main = CGMainDisplayID();
        return DisplayIds().Select((id, i) =>
        {
            var b = CGDisplayBounds(id);
            return new DisplayInfo(i, id == main ? "Main display" : $"Display {i + 1}", b.Origin.X, b.Origin.Y, b.Size.Width, b.Size.Height, id == main);
        }).ToList();
    }

    public CapturedFrame Capture(DisplayInfo display)
    {
        var ids = DisplayIds();
        var id = ids[Math.Clamp(display.Index, 0, ids.Length - 1)];
        var image = CGDisplayCreateImage(id);
        SKBitmap bitmap;
        if (image == 0)
        {
            bitmap = CaptureWithTool(display.Index) ?? throw new InvalidOperationException(
                "macOS didn't give a picture of the screen. Allow this app in System Settings › Privacy & Security › Screen & System Audio Recording, then quit and reopen it.");
        }
        else
        {
            try { bitmap = Draw(image); }
            finally { CGImageRelease(image); }
        }
        DrawPointer(bitmap, display);
        return new CapturedFrame(bitmap);
    }

    private SKBitmap Draw(nint image)
    {
        var w = (int)CGImageGetWidth(image);
        var h = (int)CGImageGetHeight(image);
        if (MaxWidth > 0 && w > MaxWidth)
        {
            h = (int)Math.Round(h * (MaxWidth / (double)w));
            w = MaxWidth;
        }
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var space = CGColorSpaceCreateDeviceRGB();
        // kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little = BGRA in memory, Skia's Bgra8888.
        var context = CGBitmapContextCreate((void*)bitmap.GetPixels(), (nuint)w, (nuint)h, 8, (nuint)bitmap.RowBytes, space, 2 | (2 << 12));
        CGColorSpaceRelease(space);
        if (context == 0)
        {
            bitmap.Dispose();
            throw new InvalidOperationException("CoreGraphics couldn't make a drawing context for the screen image.");
        }
        try
        {
            CGContextSetInterpolationQuality(context, 3);
            CGContextDrawImage(context, new CGRect { Size = new CGSize { Width = w, Height = h } }, image);
        }
        finally
        {
            CGContextRelease(context);
        }
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    private SKBitmap? CaptureWithTool(int displayIndex)
    {
        var file = Path.Combine(Path.GetTempPath(), $"screenrec-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-x", "-C", "-D", (displayIndex + 1).ToString(), "-t", "png", file }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            if (!File.Exists(file)) return null;
            using var full = SKBitmap.Decode(file);
            if (full is null) return null;
            if (MaxWidth <= 0 || full.Width <= MaxWidth) return full.Copy();
            var h = (int)Math.Round(full.Height * (MaxWidth / (double)full.Width));
            return full.Resize(new SKImageInfo(MaxWidth, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private static void DrawPointer(SKBitmap bitmap, DisplayInfo display)
    {
        var mouse = MouseLocation();
        var x = (mouse.X - display.X) / display.Width * bitmap.Width;
        var y = (mouse.Y - display.Y) / display.Height * bitmap.Height;
        if (x < 0 || y < 0 || x > bitmap.Width || y > bitmap.Height) return;
        PointerArt.Draw(bitmap, (float)x, (float)y, (float)(bitmap.Width / display.Width));
    }

    public void Dispose() { }
}

/// <summary>A plain arrow pointer for captures that don't include one.</summary>
public static class PointerArt
{
    public static void Draw(SKBitmap bitmap, float x, float y, float scale)
    {
        using var canvas = new SKCanvas(bitmap);
        var s = Math.Max(1f, scale);
        using var path = new SKPath();
        path.MoveTo(x, y);
        path.LineTo(x, y + 17 * s);
        path.LineTo(x + 4.5f * s, y + 13 * s);
        path.LineTo(x + 7.5f * s, y + 19.5f * s);
        path.LineTo(x + 10 * s, y + 18.5f * s);
        path.LineTo(x + 7 * s, y + 12 * s);
        path.LineTo(x + 12.5f * s, y + 12 * s);
        path.Close();
        using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f * s };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, stroke);
    }
}

/// <summary>Global mouse and keyboard on macOS through a listen-only CGEventTap on its own run-loop thread. Needs Input
/// Monitoring (System Settings › Privacy &amp; Security); without it the tap can't be made and only the screen is recorded.</summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacInputMonitor : IInputMonitor
{
    private static MacInputMonitor? _current;
    private Action<RawInput>? _onInput;
    private Thread? _thread;
    private nint _tap, _source, _loop;
    private readonly ManualResetEventSlim _started = new();
    private bool _ok;

    public string Name => "CGEventTap";
    public string? Problem { get; private set; }

    public static bool HasPermission() => CGPreflightListenEventAccess();
    public static bool RequestPermission() => CGRequestListenEventAccess();

    public bool Start(Action<RawInput> onInput)
    {
        Stop();
        _onInput = onInput;
        _current = this;
        _started.Reset();
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "ScreenRec event tap" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
        return _ok;
    }

    private void RunLoop()
    {
        ulong mask = 0;
        foreach (var t in new[] { kCGEventLeftMouseDown, kCGEventLeftMouseUp, kCGEventRightMouseDown, kCGEventRightMouseUp,
                     kCGEventOtherMouseDown, kCGEventOtherMouseUp, kCGEventKeyDown, kCGEventScrollWheel })
            mask |= 1UL << t;
        _tap = CGEventTapCreate(kCGSessionEventTap, kCGHeadInsertEventTap, kCGEventTapOptionListenOnly, mask, &Callback, 0);
        if (_tap == 0)
        {
            Problem = "macOS didn't allow watching the mouse and keyboard. Allow this app in System Settings › Privacy & Security › Input Monitoring (and Accessibility), then reopen it. The screen is still recorded.";
            _ok = false;
            _started.Set();
            return;
        }
        _source = CFMachPortCreateRunLoopSource(0, _tap, 0);
        _loop = CFRunLoopGetCurrent();
        CFRunLoopAddSource(_loop, _source, CommonModes);
        CGEventTapEnable(_tap, true);
        _ok = true;
        Problem = null;
        _started.Set();
        CFRunLoopRun();
        CFMachPortInvalidate(_tap);
        CFRelease(_source);
        CFRelease(_tap);
        _tap = _source = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static nint Callback(nint proxy, int type, nint ev, nint user)
    {
        try
        {
            var me = _current;
            if (me is null) return ev;
            if (type is kCGEventTapDisabledByTimeout or kCGEventTapDisabledByUserInput)
            {
                if (me._tap != 0) CGEventTapEnable(me._tap, true);
                return ev;
            }
            var at = DateTimeOffset.Now;
            var p = CGEventGetLocation(ev);
            RawInput? input = type switch
            {
                kCGEventLeftMouseDown => new RawInput(RawInputKind.MouseDown, p.X, p.Y, "left", null, 0, at),
                kCGEventLeftMouseUp => new RawInput(RawInputKind.MouseUp, p.X, p.Y, "left", null, 0, at),
                kCGEventRightMouseDown => new RawInput(RawInputKind.MouseDown, p.X, p.Y, "right", null, 0, at),
                kCGEventRightMouseUp => new RawInput(RawInputKind.MouseUp, p.X, p.Y, "right", null, 0, at),
                kCGEventOtherMouseDown => new RawInput(RawInputKind.MouseDown, p.X, p.Y, "middle", null, 0, at),
                kCGEventOtherMouseUp => new RawInput(RawInputKind.MouseUp, p.X, p.Y, "middle", null, 0, at),
                kCGEventScrollWheel => new RawInput(RawInputKind.Scroll, p.X, p.Y, null, null, (int)CGEventGetIntegerValueField(ev, kCGScrollWheelEventDeltaAxis1), at),
                kCGEventKeyDown => Key(ev, p, at),
                _ => null,
            };
            if (input is { } i) me._onInput?.Invoke(i);
        }
        catch
        {
            // Never let an exception cross back into CoreGraphics.
        }
        return ev;
    }

    private static RawInput Key(nint ev, CGPoint p, DateTimeOffset at)
    {
        var code = (int)CGEventGetIntegerValueField(ev, kCGKeyboardEventKeycode);
        var flags = CGEventGetFlags(ev);
        string? text = null;
        char* buffer = stackalloc char[8];
        CGEventKeyboardGetUnicodeString(ev, 8, out var length, buffer);
        if (length > 0) text = new string(buffer, 0, (int)length);
        var name = KeyNames.Mac(code) ?? (text is { Length: > 0 } && !char.IsControl(text[0]) ? text.ToUpperInvariant() : $"Key{code}");
        var mods = new List<string>();
        if ((flags & FlagControl) != 0) mods.Add("Ctrl");
        if ((flags & FlagAlt) != 0) mods.Add("Option");
        if ((flags & FlagShift) != 0 && KeyNames.Mac(code) is not null) mods.Add("Shift");
        if ((flags & FlagCommand) != 0) mods.Add("Cmd");
        var combo = mods.Count > 0 && (mods.Any(m => m != "Shift") || KeyNames.Mac(code) is not null) ? string.Join("+", mods) + "+" + name : name;
        var printable = text is { Length: > 0 } && !char.IsControl(text[0]) && (flags & (FlagCommand | FlagControl)) == 0 ? text : null;
        return new RawInput(RawInputKind.Key, p.X, p.Y, combo, printable, 0, at);
    }

    public ForegroundInfo Foreground()
    {
        try
        {
            var (name, pid) = FrontmostApp();
            return new ForegroundInfo(name, WindowTitleFor(pid));
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException)
        {
            return new ForegroundInfo(null, null);
        }
    }

    public void Stop()
    {
        if (_loop != 0)
        {
            CFRunLoopStop(_loop);
            _loop = 0;
        }
        _thread?.Join(2000);
        _thread = null;
        if (_current == this) _current = null;
    }

    public void Dispose() => Stop();
}

public static class KeyNames
{
    /// <summary>macOS virtual key codes that aren't characters.</summary>
    public static string? Mac(int code) => code switch
    {
        36 => "Enter", 76 => "Enter", 48 => "Tab", 49 => "Space", 51 => "Backspace", 53 => "Esc", 117 => "Delete",
        123 => "Left", 124 => "Right", 125 => "Down", 126 => "Up", 115 => "Home", 119 => "End", 116 => "PageUp", 121 => "PageDown",
        122 => "F1", 120 => "F2", 99 => "F3", 118 => "F4", 96 => "F5", 97 => "F6", 98 => "F7", 100 => "F8", 101 => "F9",
        109 => "F10", 103 => "F11", 111 => "F12",
        _ => null,
    };

    /// <summary>Windows virtual-key codes that aren't characters.</summary>
    public static string? Windows(int vk) => vk switch
    {
        0x0D => "Enter", 0x09 => "Tab", 0x20 => "Space", 0x08 => "Backspace", 0x1B => "Esc", 0x2E => "Delete", 0x2D => "Insert",
        0x25 => "Left", 0x27 => "Right", 0x28 => "Down", 0x26 => "Up", 0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
        _ => null,
    };
}
