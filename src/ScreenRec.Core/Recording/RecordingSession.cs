using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenRec.Core.Recording;

public enum InputKind
{
    MouseDown,
    MouseUp,
    Scroll,
    Key,
    /// <summary>The foreground app or window changed.</summary>
    Focus,
    /// <summary>A note the person added while recording ("now I check the totals").</summary>
    Note,
    Pause,
    Resume,
}

/// <summary>One thing that happened, at a time offset from the start. Mouse positions are normalized to the recorded display
/// (0..1), so they line up with frames whatever the display's scale.</summary>
public sealed record InputEvent
{
    public double T { get; init; }
    public InputKind Kind { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    /// <summary>left / right / middle (mouse); the key name ("Enter", "Tab", "a", "Cmd+C") for keys.</summary>
    public string? Button { get; init; }
    /// <summary>Text typed (only when typed-text capture is on), or the note's text.</summary>
    public string? Text { get; init; }
    public string? App { get; init; }
    public string? Window { get; init; }
    public int? Delta { get; init; }
}

public sealed record FrameInfo
{
    public int Index { get; init; }
    public double T { get; init; }
    public string File { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public string? App { get; init; }
    public string? Window { get; init; }
    /// <summary>How different this frame is from the previous one (0 = identical, 1 = completely different).</summary>
    public double Change { get; init; }
}

public enum SessionStatus { Recording, Recorded, Processing, Processed, Published, Failed }

public sealed class SessionManifest
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>What the person said they were doing — the most useful single hint the model gets.</summary>
    public string Description { get; set; } = "";
    public string Expert { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string Platform { get; set; } = "";
    public string Machine { get; set; } = "";
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }
    public double FramesPerSecond { get; set; }
    public int FrameCount { get; set; }
    public int EventCount { get; set; }
    public string? VideoFile { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Recording;
    public string? LastError { get; set; }
    public List<string> Warnings { get; set; } = new();
    public GuardianPublishState Guardian { get; set; } = new();
}

public sealed class GuardianPublishState
{
    public string? SkillName { get; set; }
    public DateTimeOffset? SkillPublishedAt { get; set; }
    public string? AutomationId { get; set; }
    public DateTimeOffset? AutomationCreatedAt { get; set; }
    public string? AttachmentId { get; set; }
    public string? VideoIntakeId { get; set; }
    public string? VideoIntakeStatus { get; set; }
}

/// <summary>A recording on disk: sessions/&lt;id&gt;/{session.json, events.jsonl, frames.jsonl, frames/*.jpg, recording.avi|mp4, analysis/}.</summary>
public sealed class RecordingSession
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static readonly JsonSerializerOptions Line = new(Json) { WriteIndented = false };

    public RecordingSession(string folder, SessionManifest manifest)
    {
        Folder = folder;
        Manifest = manifest;
    }

    public string Folder { get; }
    public SessionManifest Manifest { get; }

    public string Id => Manifest.Id;
    public string FramesFolder => Path.Combine(Folder, "frames");
    public string EventsFile => Path.Combine(Folder, "events.jsonl");
    public string FramesFile => Path.Combine(Folder, "frames.jsonl");
    public string ManifestFile => Path.Combine(Folder, "session.json");
    public string AnalysisFolder => Path.Combine(Folder, "analysis");
    public string? VideoPath => Manifest.VideoFile is { } v ? Path.Combine(Folder, v) : null;

    public static string NewId(DateTimeOffset at) => at.ToLocalTime().ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    public static RecordingSession Create(string root, SessionManifest manifest)
    {
        if (string.IsNullOrEmpty(manifest.Id)) manifest.Id = NewId(manifest.StartedAt == default ? DateTimeOffset.Now : manifest.StartedAt);
        var folder = Path.Combine(root, manifest.Id);
        Directory.CreateDirectory(Path.Combine(folder, "frames"));
        var session = new RecordingSession(folder, manifest);
        session.Save();
        return session;
    }

    public static RecordingSession? Open(string folder)
    {
        var file = Path.Combine(folder, "session.json");
        if (!File.Exists(file)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(file), Json);
            return manifest is null ? null : new RecordingSession(folder, manifest);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public static IReadOnlyList<RecordingSession> List(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root).Select(Open).OfType<RecordingSession>()
            .OrderByDescending(s => s.Manifest.StartedAt).ToList();
    }

    public void Save()
    {
        var tmp = ManifestFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Manifest, Json));
        File.Move(tmp, ManifestFile, true);
    }

    public IReadOnlyList<InputEvent> ReadEvents() => ReadLines<InputEvent>(EventsFile);
    public IReadOnlyList<FrameInfo> ReadFrames() => ReadLines<FrameInfo>(FramesFile);

    public string FramePath(FrameInfo frame) => Path.Combine(FramesFolder, frame.File);

    private static List<T> ReadLines<T>(string path)
    {
        var list = new List<T>();
        if (!File.Exists(path)) return list;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<T>(line, Line) is { } item) list.Add(item);
            }
            catch (JsonException)
            {
                // A line cut short by a crash: skip it, keep the rest.
            }
        }
        return list;
    }

    public void Delete()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
    }
}
