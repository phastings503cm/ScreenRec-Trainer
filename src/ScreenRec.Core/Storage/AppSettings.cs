using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenRec.Core.Storage;

/// <summary>Which AI system processes recordings.</summary>
public enum AiProvider
{
    /// <summary>A GLM-5.3 Flash server on DGX Sparks (MiaAI-Lab recipe): OpenAI-compatible, http://&lt;head&gt;:8888/v1.</summary>
    Spark,
    /// <summary>The models the Guardian sign-in allows, through Guardian's harness proxy (/api/harness/v1).</summary>
    Guardian,
    /// <summary>Any other OpenAI-compatible server (a Vision / Spark Swapper route, Ollama, vLLM, LM Studio…).</summary>
    OpenAiCompatible,
}

public sealed class AppSettings
{
    // AI
    public AiProvider Provider { get; set; } = AiProvider.Spark;
    /// <summary>The Spark head's OpenAI base URL. The recipe serves http://&lt;head&gt;:8888/v1.</summary>
    public string SparkBaseUrl { get; set; } = "http://127.0.0.1:8888/v1";
    public string SparkModel { get; set; } = "GLM-5.3-Flash-EXL3";
    /// <summary>Secret-store key name holding VLLM_API_KEY (empty when the server has none).</summary>
    public string OtherBaseUrl { get; set; } = "http://127.0.0.1:11434/v1";
    public string OtherModel { get; set; } = "";
    /// <summary>The Guardian model id to use when Provider = Guardian (empty = first one Guardian lists).</summary>
    public string GuardianModel { get; set; } = "";
    /// <summary>low / high / max — GLM-5.3 always thinks; "low" keeps long structured output reliable (recipe advice).</summary>
    public string ReasoningEffort { get; set; } = "low";
    public int RequestTimeoutSeconds { get; set; } = 600;
    /// <summary>Screenshots per AI request when describing steps.</summary>
    public int ImagesPerRequest { get; set; } = 4;
    /// <summary>The longest edge an image sent to the model is scaled to.</summary>
    public int ModelImageMaxEdge { get; set; } = 1280;
    /// <summary>At most this many steps are described (long recordings are sampled evenly).</summary>
    public int MaxSteps { get; set; } = 60;

    // Recording
    public double FramesPerSecond { get; set; } = 2.0;
    public int FrameMaxWidth { get; set; } = 1920;
    public int JpegQuality { get; set; } = 70;
    /// <summary>Record the characters typed (off: only that typing happened and which special keys were used).</summary>
    public bool CaptureTypedText { get; set; } = false;
    /// <summary>Which display to record (0 = main).</summary>
    public int DisplayIndex { get; set; } = 0;
    /// <summary>ffmpeg for MP4 output (empty = look on PATH; none = MJPEG AVI only).</summary>
    public string FfmpegPath { get; set; } = "";

    // Guardian
    public string GuardianAddress { get; set; } = "https://guardian.503cm.com";
    /// <summary>Also upload the recording to Guardian's Video intake (Guardian's own frame + narration analysis).</summary>
    public bool UploadVideoToGuardian { get; set; } = true;

    // Who is being "cloned"
    public string ExpertName { get; set; } = "";

    [JsonIgnore] public string ActiveBaseUrl => Provider switch
    {
        AiProvider.Spark => SparkBaseUrl,
        AiProvider.OpenAiCompatible => OtherBaseUrl,
        _ => "",
    };

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken file starts over with defaults; the old one is kept beside it.
            try { File.Copy(path, path + ".broken", true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}
