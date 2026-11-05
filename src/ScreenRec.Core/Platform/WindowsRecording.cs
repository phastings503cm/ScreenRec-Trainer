using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ScreenRec.Core.Recording;
using SkiaSharp;

namespace ScreenRec.Core.Platform;

[SupportedOSPlatform("windows")]
internal static unsafe partial class WinNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO { public int cbSize, flags; public nint hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO { public int fIcon, xHotspot, yHotspot; public nint hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }

    public delegate nint HookProc(int code, nint wParam, nint lParam);
    public delegate bool MonitorEnumProc(nint monitor, nint hdc, RECT* rect, nint data);

    [LibraryImport("user32.dll")] public static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint hwnd, nint hdc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetProcessDpiAwarenessContext(nint value);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorInfo(ref CURSORINFO info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetIconInfo(nint icon, out ICONINFO info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DrawIconEx(nint hdc, int x, int y, nint icon, int w, int h, uint step, nint brush, uint flags);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")] public static partial int GetWindowText(nint hwnd, char* text, int max);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW")] public static partial nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32.dll")] public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] public static partial int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostThreadMessage(uint thread, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll")] public static partial short GetKeyState(int vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetKeyboardState(byte* state);
    [LibraryImport("user32.dll", EntryPoint = "ToUnicode")] public static partial int ToUnicode(uint vk, uint scan, byte* state, char* buffer, int size, uint flags);
    [LibraryImport("kernel32.dll")] public static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)] public static partial nint GetModuleHandle(string? name);

    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GdiFlush();

    public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    public const int CURSOR_SHOWING = 1;
    public const uint DI_NORMAL = 3;
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204,
        WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_QUIT = 0x12;
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    private static int _dpiSet;
    public static void EnsureDpiAware()
    {
        if (Interlocked.Exchange(ref _dpiSet, 1) == 0)
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch (EntryPointNotFoundException) { }
        }
    }
}

/// <summary>Windows screen capture with GDI BitBlt (works on every Windows 10/11 desktop, no extra runtime), cursor drawn in,
/// coordinates in physical pixels (the process is made per-monitor DPI aware so they match the hooks').</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsScreenCapture : IScreenCapture
{
    public int MaxWidth { get; set; } = 1920;
    public string Name => "GDI";

    public WindowsScreenCapture() => WinNative.EnsureDpiAware();

    public IReadOnlyList<DisplayInfo> Displays()
    {
        var list = new List<DisplayInfo>();
        WinNative.EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new WinNative.MONITORINFOEX { cbSize = sizeof(WinNative.MONITORINFOEX) };
            if (WinNative.GetMonitorInfo(monitor, ref info))
            {
                var r = info.rcMonitor;
                var primary = (info.dwFlags & 1) != 0;
                list.Add(new DisplayInfo(0, new string(info.szDevice).TrimEnd('\0'), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, primary));
            }
            return true;
        }, 0);
        return list.OrderBy(d => d.IsMain ? 0 : 1).Select((d, i) => d with { Index = i, Name = d.IsMain ? "Main display" : $"Display {i + 1}" }).ToList();
    }

    public CapturedFrame Capture(DisplayInfo display)
    {
        int x = (int)display.X, y = (int)display.Y, w = (int)display.Width, h = (int)display.Height;
        var screen = WinNative.GetDC(0);
        var mem = WinNative.CreateCompatibleDC(screen);
        var header = new WinNative.BITMAPINFOHEADER { biSize = sizeof(WinNative.BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = WinNative.CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
        var old = WinNative.SelectObject(mem, dib);
        try
        {
            if (!WinNative.BitBlt(mem, 0, 0, w, h, screen, x, y, WinNative.SRCCOPY | WinNative.CAPTUREBLT))
                throw new InvalidOperationException("Windows refused to copy the screen (is the desktop locked or a secure screen showing?).");
            DrawCursor(mem, x, y);
            WinNative.GdiFlush();
            var full = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
            Buffer.MemoryCopy((void*)bits, (void*)full.GetPixels(), (long)full.RowBytes * h, (long)w * 4 * h);
            full.NotifyPixelsChanged();
            if (MaxWidth <= 0 || w <= MaxWidth) return new CapturedFrame(full);
            var scaled = full.Resize(new SKImageInfo(MaxWidth, (int)Math.Round(h * (MaxWidth / (double)w)), SKColorType.Bgra8888, SKAlphaType.Opaque),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            full.Dispose();
            return new CapturedFrame(scaled);
        }
        finally
        {
            WinNative.SelectObject(mem, old);
            WinNative.DeleteObject(dib);
            WinNative.DeleteDC(mem);
            WinNative.ReleaseDC(0, screen);
        }
    }

    private static void DrawCursor(nint hdc, int originX, int originY)
    {
        var info = new WinNative.CURSORINFO { cbSize = sizeof(WinNative.CURSORINFO) };
        if (!WinNative.GetCursorInfo(ref info) || (info.flags & WinNative.CURSOR_SHOWING) == 0 || info.hCursor == 0) return;
        int hx = 0, hy = 0;
        if (WinNative.GetIconInfo(info.hCursor, out var icon))
        {
            hx = icon.xHotspot; hy = icon.yHotspot;
            if (icon.hbmMask != 0) WinNative.DeleteObject(icon.hbmMask);
            if (icon.hbmColor != 0) WinNative.DeleteObject(icon.hbmColor);
        }
        WinNative.DrawIconEx(hdc, info.ptScreenPos.X - originX - hx, info.ptScreenPos.Y - originY - hy, info.hCursor, 0, 0, 0, 0, WinNative.DI_NORMAL);
    }

    public void Dispose() { }
}

/// <summary>Global low-level mouse and keyboard hooks on a dedicated message-loop thread. They only observe (every event is
/// passed on). No admin rights are needed; input into elevated windows isn't seen unless this app is elevated too.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsInputMonitor : IInputMonitor
{
    private Thread? _thread;
    private uint _threadId;
    private nint _mouseHook, _keyHook;
    private WinNative.HookProc? _mouseProc, _keyProc; // kept alive while hooked
    private Action<RawInput>? _onInput;
    private readonly ManualResetEventSlim _started = new();
    private bool _ok;

    public string Name => "Low-level hooks";
    public string? Problem { get; private set; }

    public WindowsInputMonitor() => WinNative.EnsureDpiAware();

    public bool Start(Action<RawInput> onInput)
    {
        Stop();
        _onInput = onInput;
        _started.Reset();
        _thread = new Thread(Loop) { IsBackground = true, Name = "ScreenRec input hooks" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
        return _ok;
    }

    private void Loop()
    {
        _threadId = WinNative.GetCurrentThreadId();
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
        var module = WinNative.GetModuleHandle(null);
        _mouseHook = WinNative.SetWindowsHookEx(WinNative.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = WinNative.SetWindowsHookEx(WinNative.WH_KEYBOARD_LL, _keyProc, module, 0);
        _ok = _mouseHook != 0 || _keyHook != 0;
        Problem = _ok ? null : $"Windows didn't allow watching the mouse and keyboard (error {Marshal.GetLastPInvokeError()}). The screen is still recorded.";
        _started.Set();
        if (!_ok) return;
        while (WinNative.GetMessage(out _, 0, 0, 0) > 0) { }
        if (_mouseHook != 0) WinNative.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != 0) WinNative.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = 0;
    }

    private nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                var data = *(WinNative.MSLLHOOKSTRUCT*)lParam;
                var at = DateTimeOffset.Now;
                double x = data.pt.X, y = data.pt.Y;
                RawInput? input = (int)wParam switch
                {
                    WinNative.WM_LBUTTONDOWN => new RawInput(RawInputKind.MouseDown, x, y, "left", null, 0, at),
                    WinNative.WM_LBUTTONUP => new RawInput(RawInputKind.MouseUp, x, y, "left", null, 0, at),
                    WinNative.WM_RBUTTONDOWN => new RawInput(RawInputKind.MouseDown, x, y, "right", null, 0, at),
                    WinNative.WM_RBUTTONUP => new RawInput(RawInputKind.MouseUp, x, y, "right", null, 0, at),
                    WinNative.WM_MBUTTONDOWN => new RawInput(RawInputKind.MouseDown, x, y, "middle", null, 0, at),
                    WinNative.WM_MBUTTONUP => new RawInput(RawInputKind.MouseUp, x, y, "middle", null, 0, at),
                    WinNative.WM_MOUSEWHEEL => new RawInput(RawInputKind.Scroll, x, y, null, null, (short)(data.mouseData >> 16) / 120, at),
                    _ => null,
                };
                if (input is { } i) _onInput?.Invoke(i);
            }
            catch
            {
                // Never break the system's input chain.
            }
        }
        return WinNative.CallNextHookEx(0, code, wParam, lParam);
    }

    private nint KeyProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && ((int)wParam == WinNative.WM_KEYDOWN || (int)wParam == WinNative.WM_SYSKEYDOWN))
        {
            try
            {
                var data = *(WinNative.KBDLLHOOKSTRUCT*)lParam;
                var vk = (int)data.vkCode;
                if (vk is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x14))
                {
                    bool ctrl = WinNative.GetKeyState(0x11) < 0, alt = WinNative.GetKeyState(0x12) < 0, shift = WinNative.GetKeyState(0x10) < 0,
                        win = WinNative.GetKeyState(0x5B) < 0 || WinNative.GetKeyState(0x5C) < 0;
                    string? text = null;
                    var state = stackalloc byte[256];
                    for (var k = 0; k < 256; k++) state[k] = (byte)(WinNative.GetKeyState(k) < 0 ? 0x80 : 0);
                    state[0x14] = (byte)(WinNative.GetKeyState(0x14) & 1);
                    var buffer = stackalloc char[8];
                    // flags 4: don't change the keyboard state (Windows 10 1607+), so dead keys keep working for the person.
                    var n = WinNative.ToUnicode(data.vkCode, data.scanCode, state, buffer, 8, 4);
                    if (n > 0) text = new string(buffer, 0, n);
                    var special = KeyNames.Windows(vk);
                    var name = special ?? (text is { Length: > 0 } && !char.IsControl(text[0]) ? text.ToUpperInvariant() : ((ConsoleKey)vk).ToString());
                    var mods = new List<string>();
                    if (ctrl) mods.Add("Ctrl");
                    if (alt) mods.Add("Alt");
                    if (shift && special is not null) mods.Add("Shift");
                    if (win) mods.Add("Win");
                    var keyLabel = special ?? (vk is >= 0x30 and <= 0x5A ? ((char)vk).ToString() : ((ConsoleKey)vk).ToString());
                    var combo = mods.Count > 0 ? string.Join("+", mods) + "+" + keyLabel : name;
                    var printable = text is { Length: > 0 } && !char.IsControl(text[0]) && !ctrl && !alt && !win ? text : null;
                    _onInput?.Invoke(new RawInput(RawInputKind.Key, 0, 0, combo, printable, 0, DateTimeOffset.Now));
                }
            }
            catch
            {
            }
        }
        return WinNative.CallNextHookEx(0, code, wParam, lParam);
    }

    public ForegroundInfo Foreground()
    {
        var hwnd = WinNative.GetForegroundWindow();
        if (hwnd == 0) return new ForegroundInfo(null, null);
        var buffer = stackalloc char[512];
        var length = WinNative.GetWindowText(hwnd, buffer, 512);
        var title = length > 0 ? new string(buffer, 0, length) : null;
        string? app = null;
        WinNative.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            app = p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try { using var p = Process.GetProcessById((int)pid); app = p.ProcessName; }
            catch (Exception e2) when (e2 is ArgumentException or InvalidOperationException) { }
        }
        return new ForegroundInfo(app, title);
    }

    public void Stop()
    {
        if (_thread is not null && _threadId != 0) WinNative.PostThreadMessage(_threadId, WinNative.WM_QUIT, 0, 0);
        _thread?.Join(2000);
        _thread = null;
        _threadId = 0;
    }

    public void Dispose() => Stop();
}
