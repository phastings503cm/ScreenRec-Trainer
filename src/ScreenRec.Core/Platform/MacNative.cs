using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenRec.Core.Platform;

/// <summary>The few CoreGraphics / CoreFoundation / Objective-C calls the macOS recorder needs.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacNative
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AS = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)] public struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct CGSize { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)] public struct CGRect { public CGPoint Origin; public CGSize Size; }

    // Displays and images
    [LibraryImport(CG)] public static partial uint CGMainDisplayID();
    [LibraryImport(CG)] public static partial int CGGetActiveDisplayList(uint max, uint* displays, out uint count);
    [LibraryImport(CG)] public static partial CGRect CGDisplayBounds(uint display);
    [LibraryImport(CG)] public static partial nint CGDisplayCreateImage(uint display);
    [LibraryImport(CG)] public static partial nuint CGImageGetWidth(nint image);
    [LibraryImport(CG)] public static partial nuint CGImageGetHeight(nint image);
    [LibraryImport(CG)] public static partial void CGImageRelease(nint image);
    [LibraryImport(CG)] public static partial nint CGColorSpaceCreateDeviceRGB();
    [LibraryImport(CG)] public static partial void CGColorSpaceRelease(nint space);
    [LibraryImport(CG)] public static partial nint CGBitmapContextCreate(void* data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);
    [LibraryImport(CG)] public static partial void CGContextDrawImage(nint context, CGRect rect, nint image);
    [LibraryImport(CG)] public static partial void CGContextSetInterpolationQuality(nint context, int quality);
    [LibraryImport(CG)] public static partial void CGContextRelease(nint context);

    // Permissions
    [LibraryImport(CG)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CGPreflightScreenCaptureAccess();
    [LibraryImport(CG)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CGRequestScreenCaptureAccess();
    [LibraryImport(CG)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CGPreflightListenEventAccess();
    [LibraryImport(CG)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CGRequestListenEventAccess();
    [LibraryImport(AS)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool AXIsProcessTrusted();

    // Events
    public const int kCGSessionEventTap = 1;
    public const int kCGHeadInsertEventTap = 0;
    public const int kCGEventTapOptionListenOnly = 1;
    public const int kCGEventLeftMouseDown = 1, kCGEventLeftMouseUp = 2, kCGEventRightMouseDown = 3, kCGEventRightMouseUp = 4;
    public const int kCGEventKeyDown = 10, kCGEventScrollWheel = 22, kCGEventOtherMouseDown = 25, kCGEventOtherMouseUp = 26;
    public const int kCGEventTapDisabledByTimeout = unchecked((int)0xFFFFFFFE), kCGEventTapDisabledByUserInput = unchecked((int)0xFFFFFFFF);
    public const int kCGKeyboardEventKeycode = 9, kCGScrollWheelEventDeltaAxis1 = 11;
    public const ulong FlagShift = 0x20000, FlagControl = 0x40000, FlagAlt = 0x80000, FlagCommand = 0x100000;

    [LibraryImport(CG)] public static partial nint CGEventTapCreate(int tap, int place, int options, ulong mask, delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint> callback, nint userInfo);
    [LibraryImport(CG)] public static partial void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.U1)] bool enable);
    [LibraryImport(CG)] public static partial CGPoint CGEventGetLocation(nint ev);
    [LibraryImport(CG)] public static partial long CGEventGetIntegerValueField(nint ev, int field);
    [LibraryImport(CG)] public static partial ulong CGEventGetFlags(nint ev);
    [LibraryImport(CG)] public static partial void CGEventKeyboardGetUnicodeString(nint ev, nuint max, out nuint actual, char* buffer);
    [LibraryImport(CG)] public static partial nint CGEventCreate(nint source);

    // Run loops
    [LibraryImport(CF)] public static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);
    [LibraryImport(CF)] public static partial nint CFRunLoopGetCurrent();
    [LibraryImport(CF)] public static partial void CFRunLoopAddSource(nint loop, nint source, nint mode);
    [LibraryImport(CF)] public static partial void CFRunLoopRun();
    [LibraryImport(CF)] public static partial void CFRunLoopStop(nint loop);
    [LibraryImport(CF)] public static partial void CFRelease(nint obj);
    [LibraryImport(CF)] public static partial void CFMachPortInvalidate(nint port);

    // CF data
    [LibraryImport(CF)] public static partial nint CFArrayGetCount(nint array);
    [LibraryImport(CF)] public static partial nint CFArrayGetValueAtIndex(nint array, nint index);
    [LibraryImport(CF)] public static partial nint CFDictionaryGetValue(nint dict, nint key);
    [LibraryImport(CF, StringMarshalling = StringMarshalling.Utf8)] public static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);
    [LibraryImport(CF)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CFStringGetCString(nint str, byte* buffer, nint size, uint encoding);
    [LibraryImport(CF)] public static partial nint CFStringGetLength(nint str);
    [LibraryImport(CF)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CFNumberGetValue(nint number, int type, void* value);
    [LibraryImport(CG)] public static partial nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    public const uint kCFStringEncodingUTF8 = 0x08000100;
    public const int kCFNumberSInt64Type = 4;
    public const uint kCGWindowListOptionOnScreenOnly = 1, kCGWindowListExcludeDesktopElements = 16;

    private static nint _commonModes;
    public static nint CommonModes
    {
        get
        {
            if (_commonModes == 0)
            {
                var lib = NativeLibrary.Load(CF);
                _commonModes = *(nint*)NativeLibrary.GetExport(lib, "kCFRunLoopCommonModes");
            }
            return _commonModes;
        }
    }

    public static nint CFStr(string s) => CFStringCreateWithCString(0, s, kCFStringEncodingUTF8);

    public static string? ReadCFString(nint str)
    {
        if (str == 0) return null;
        var length = CFStringGetLength(str);
        var size = (int)length * 4 + 1;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (!CFStringGetCString(str, p, size, kCFStringEncodingUTF8)) return null;
        }
        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    public static long? ReadCFNumber(nint number)
    {
        if (number == 0) return null;
        long value;
        return CFNumberGetValue(number, kCFNumberSInt64Type, &value) ? value : null;
    }

    // Objective-C (NSWorkspace.frontmostApplication)
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] public static partial nint objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] public static partial nint sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] public static partial nint Send(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] public static partial int SendInt(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] public static partial nint SendPtr(nint receiver, nint selector, nint arg);

    private static readonly Lazy<bool> AppKitLoaded = new(() =>
    {
        try { NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"); return true; }
        catch (DllNotFoundException) { return false; }
    });

    /// <summary>(name, pid) of the frontmost app.</summary>
    public static (string? Name, int Pid) FrontmostApp()
    {
        if (!AppKitLoaded.Value) return (null, 0);
        var pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
        try
        {
            var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            if (workspace == 0) return (null, 0);
            var app = Send(workspace, sel_registerName("frontmostApplication"));
            if (app == 0) return (null, 0);
            var name = Send(app, sel_registerName("localizedName"));
            var pid = SendInt(app, sel_registerName("processIdentifier"));
            return (ReadCFString(name), pid); // NSString is toll-free bridged with CFString.
        }
        finally
        {
            if (pool != 0) Send(pool, sel_registerName("drain"));
        }
    }

    /// <summary>The title of the frontmost normal window owned by <paramref name="pid"/> (needs Screen Recording permission
    /// on recent macOS; null otherwise).</summary>
    public static string? WindowTitleFor(int pid)
    {
        if (pid <= 0) return null;
        var list = CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements, 0);
        if (list == 0) return null;
        var kPid = CFStr("kCGWindowOwnerPID");
        var kName = CFStr("kCGWindowName");
        var kLayer = CFStr("kCGWindowLayer");
        try
        {
            var count = CFArrayGetCount(list);
            for (nint i = 0; i < count; i++)
            {
                var dict = CFArrayGetValueAtIndex(list, i);
                if (ReadCFNumber(CFDictionaryGetValue(dict, kPid)) != pid) continue;
                if (ReadCFNumber(CFDictionaryGetValue(dict, kLayer)) is not 0) continue;
                var name = ReadCFString(CFDictionaryGetValue(dict, kName));
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            return null;
        }
        finally
        {
            CFRelease(kPid); CFRelease(kName); CFRelease(kLayer); CFRelease(list);
        }
    }

    public static CGPoint MouseLocation()
    {
        var ev = CGEventCreate(0);
        if (ev == 0) return default;
        try { return CGEventGetLocation(ev); }
        finally { CFRelease(ev); }
    }
}
