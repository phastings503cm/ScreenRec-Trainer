namespace ScreenRec.Core.Storage;

/// <summary>Where the app keeps its data. Windows: %APPDATA%\ScreenRecTrainer. macOS: ~/Library/Application Support/ScreenRecTrainer.
/// SCREENREC_HOME overrides both (tests, portable installs).</summary>
public static class AppPaths
{
    public const string AppFolderName = "ScreenRecTrainer";

    private static string? _override;

    /// <summary>Point everything at another folder (tests).</summary>
    public static void UseRoot(string? folder) => _override = folder;

    public static string Root
    {
        get
        {
            var root = _override ?? Environment.GetEnvironmentVariable("SCREENREC_HOME");
            if (string.IsNullOrWhiteSpace(root))
            {
                if (OperatingSystem.IsMacOS())
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", AppFolderName);
                else
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
            }
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string Sessions => Ensure(Path.Combine(Root, "sessions"));
    public static string Experts => Ensure(Path.Combine(Root, "experts"));
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string GuardianFile => Path.Combine(Root, "guardian.json");
    public static string Logs => Ensure(Path.Combine(Root, "logs"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
