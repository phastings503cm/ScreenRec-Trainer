using Avalonia;

namespace ScreenRec.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // --self-test renders the main window headlessly-ish (opens, waits, closes) — used by CI and the build check.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
