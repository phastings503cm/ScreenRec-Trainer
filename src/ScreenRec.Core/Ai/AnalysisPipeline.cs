using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenRec.Core.Recording;

namespace ScreenRec.Core.Ai;

public sealed record PipelineProgress(string Stage, int Done, int Total, string Message);

public sealed class PipelineOptions
{
    public int ImagesPerRequest { get; init; } = 4;
    public int ImageMaxEdge { get; init; } = 1280;
    public int MaxSteps { get; init; } = 60;
    public string? ReasoningEffort { get; init; } = "low";
    /// <summary>Some servers take text only: describe from the events and window titles alone.</summary>
    public bool TextOnly { get; init; }
    public int MaxParallel { get; init; } = 2;
}

/// <summary>Recording → steps → per-step vision descriptions → procedure, decisions, pitfalls and an automation runbook.
/// Each stage is saved under analysis/ so a failed run resumes where it stopped.</summary>
public sealed class AnalysisPipeline(IChatClient client, PipelineOptions options)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public const string StepsFile = "steps.json";
    public const string AnalysisFile = "analysis.json";

    private int _promptTokens, _completionTokens;

    public static SessionAnalysis? Load(RecordingSession session)
    {
        var path = Path.Combine(session.AnalysisFolder, AnalysisFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SessionAnalysis>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public static void Save(RecordingSession session, SessionAnalysis analysis)
    {
        Directory.CreateDirectory(session.AnalysisFolder);
        File.WriteAllText(Path.Combine(session.AnalysisFolder, AnalysisFile), JsonSerializer.Serialize(analysis, Json));
    }

    public async Task<SessionAnalysis> RunAsync(RecordingSession session, IProgress<PipelineProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(session.AnalysisFolder);
        var events = session.ReadEvents();
        var frames = session.ReadFrames();
        if (frames.Count == 0) throw new AiError("This recording has no frames to look at.");
        var candidates = Segmenter.Segment(events, frames, options.MaxSteps);
        progress?.Report(new PipelineProgress("segment", candidates.Count, candidates.Count, $"Found {candidates.Count} steps in {events.Count} events and {frames.Count} frames."));

        var steps = await DescribeStepsAsync(session, candidates, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new PipelineProgress("synthesize", 0, 1, "Writing the procedure, decisions and automation runbook…"));
        var analysis = await SynthesizeAsync(session, steps, cancellationToken).ConfigureAwait(false);
        analysis.Steps = steps;
        analysis.PromptTokens = _promptTokens;
        analysis.CompletionTokens = _completionTokens;
        Save(session, analysis);
        progress?.Report(new PipelineProgress("done", 1, 1, $"Done: {analysis.Procedure.Count} procedure steps, {analysis.Decisions.Count} decision rules."));
        return analysis;
    }

    // MARK: Stage 1 — describe each step from its before/after screenshots

    private async Task<List<StepDescription>> DescribeStepsAsync(RecordingSession session, IReadOnlyList<StepCandidate> candidates,
                                                                IProgress<PipelineProgress>? progress, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(session.AnalysisFolder, StepsFile);
        var cache = new Dictionary<int, StepDescription>();
        if (File.Exists(cachePath))
        {
            try
            {
                foreach (var s in JsonSerializer.Deserialize<List<StepDescription>>(File.ReadAllText(cachePath), Json) ?? [])
                    cache[s.Index] = s;
            }
            catch (JsonException) { }
        }
        // Batches: each request covers a few steps (images budget), in order, with the previous batch's result as context.
        var perBatch = Math.Max(1, options.ImagesPerRequest / 2);
        var batches = candidates.Chunk(perBatch).ToList();
        var done = 0;
        var gate = new SemaphoreSlim(Math.Max(1, options.MaxParallel));
        var results = new StepDescription[candidates.Count];
        var tasks = batches.Select(async batch =>
        {
            if (batch.All(c => cache.ContainsKey(c.Index)))
            {
                foreach (var c in batch) results[c.Index - 1] = cache[c.Index];
                Interlocked.Add(ref done, batch.Length);
                return;
            }
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var described = await DescribeBatchAsync(session, batch, candidates, cancellationToken).ConfigureAwait(false);
                foreach (var d in described) results[d.Index - 1] = d;
                lock (cache)
                {
                    foreach (var d in described) cache[d.Index] = d;
                    File.WriteAllText(cachePath, JsonSerializer.Serialize(cache.Values.OrderBy(v => v.Index).ToList(), Json));
                }
            }
            finally
            {
                gate.Release();
            }
            var n = Interlocked.Add(ref done, batch.Length);
            progress?.Report(new PipelineProgress("describe", n, candidates.Count, $"Described {n} of {candidates.Count} steps."));
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).ToList();
    }

    internal const string DescribeSystem = """
        You are watching a screen recording of an expert doing their job, so that a new colleague can learn to do it exactly the
        same way. You get a few consecutive steps. For each step there is the action the computer recorded (a click with its
        position as fractions of the screen, typing, a shortcut, an app switch, or a note the expert wrote) and screenshots
        BEFORE the action (a red ring marks where they clicked) and AFTER it.

        For every step, say precisely what was done, using the visible labels of buttons, menus, fields and columns — never
        pixel positions. Infer why it was done from the context. Note values that were entered or checked, but replace personal
        data, account numbers and passwords with a description of what kind of value it is ("the invoice number from the email").
        If the expert made a judgement (chose one option over another, checked something before continuing), state the rule.
        If a screenshot doesn't show enough to be sure, say so and lower the confidence. Don't invent steps.

        Answer with JSON only:
        {"steps":[{"index":<n>,"action":"imperative instruction","target":"UI element label","why":"purpose","result":"what changed",
        "dataUsed":["…"],"decision":"rule or null","app":"application","confidence":0.0-1.0}]}
        """;

    private async Task<List<StepDescription>> DescribeBatchAsync(RecordingSession session, StepCandidate[] batch, IReadOnlyList<StepCandidate> all,
                                                                CancellationToken cancellationToken)
    {
        var parts = new List<ChatPart>();
        var m = session.Manifest;
        var context = new StringBuilder();
        context.AppendLine($"Recording: \"{m.Title}\"{(m.Expert.Length > 0 ? $" by {m.Expert}" : "")}, {Platform(m.Platform)}.");
        if (m.Description.Length > 0) context.AppendLine($"The expert says this recording is about: {m.Description}");
        var first = batch[0].Index;
        var earlier = all.Where(c => c.Index < first).TakeLast(4).ToList();
        if (earlier.Count > 0)
        {
            context.AppendLine("Just before these steps:");
            foreach (var e in earlier) context.AppendLine("  " + e.Summary());
        }
        parts.Add(new TextPart(context.ToString()));

        foreach (var step in batch)
        {
            parts.Add(new TextPart($"\nStep {step.Index}: {step.Summary()}"));
            if (options.TextOnly) continue;
            var marks = step.X is { } x && step.Y is { } y && step.Kind is "click" or "double-click" or "right-click" or "drag" or "middle-click" ? new[] { (x, y) } : null;
            if (step.Before is { } before && File.Exists(session.FramePath(before)))
            {
                parts.Add(new TextPart($"BEFORE (t={StepCandidate.FormatTime(before.T)}):"));
                parts.Add(new ImagePart(ImageTools.PrepareForModel(session.FramePath(before), options.ImageMaxEdge, marks)));
            }
            if (step.After is { } after && after.File != step.Before?.File && File.Exists(session.FramePath(after)))
            {
                parts.Add(new TextPart($"AFTER (t={StepCandidate.FormatTime(after.T)}):"));
                parts.Add(new ImagePart(ImageTools.PrepareForModel(session.FramePath(after), options.ImageMaxEdge)));
            }
        }
        parts.Add(new TextPart($"\nDescribe steps {string.Join(", ", batch.Select(b => b.Index))} as JSON."));

        JsonObject json;
        try
        {
            var result = await client.CompleteAsync([ChatMessage.System(DescribeSystem), ChatMessage.User([.. parts])],
                new ChatOptions { JsonObject = true, ReasoningEffort = options.ReasoningEffort, MaxTokens = 4096 }, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _promptTokens, result.PromptTokens);
            Interlocked.Add(ref _completionTokens, result.CompletionTokens);
            json = JsonExtract.Object(result.Content);
        }
        catch (AiError error) when (!options.TextOnly && error.Status is 400 or 413 or 422)
        {
            // The server refused images (a text-only model, or too big): describe from the events alone.
            var textOnly = new AnalysisPipeline(client, new PipelineOptions { TextOnly = true, ReasoningEffort = options.ReasoningEffort });
            var fallback = await textOnly.DescribeBatchAsync(session, batch, all, cancellationToken).ConfigureAwait(false);
            foreach (var f in fallback) f.Note = "Described without screenshots (the server refused images): " + error.Message;
            return fallback;
        }

        var answered = (json["steps"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var list = new List<StepDescription>();
        foreach (var step in batch)
        {
            var a = answered.FirstOrDefault(s => (int?)Num(s["index"]) == step.Index) ?? (answered.Count == batch.Length ? answered[Array.IndexOf(batch, step)] : null);
            list.Add(new StepDescription
            {
                Index = step.Index,
                Start = step.Start,
                Kind = step.Kind,
                App = Str(a?["app"]) ?? step.App,
                Action = Str(a?["action"]) ?? Fallback(step),
                Target = Str(a?["target"]),
                Why = Str(a?["why"]),
                Result = Str(a?["result"]),
                DataUsed = Strs(a?["dataUsed"]),
                Decision = Str(a?["decision"]) is { } d && !d.Equals("null", StringComparison.OrdinalIgnoreCase) ? d : null,
                Confidence = Math.Clamp(Num(a?["confidence"]) ?? (a is null ? 0.1 : 0.5), 0, 1),
                BeforeFrame = step.Before?.File,
                AfterFrame = step.After?.File,
                Note = step.Kind == "note" ? step.Text : null,
            });
        }
        return list;
    }

    private static string Fallback(StepCandidate s) => s.Kind switch
    {
        "note" => $"Expert's note: {s.Text}",
        "switch-app" => $"Switch to {s.App}",
        _ => s.Summary(),
    };

    private static string Platform(string p) => p switch { "windows" => "on Windows", "macos" => "on a Mac", _ => p };

    // MARK: Stage 2 — the whole procedure

    internal const string SynthesizeSystem = """
        You turn the step-by-step record of an expert's screen session into training material and an automation, so that a
        person (and Guardian's AI worker) can do this job the way the expert does. Be concrete: name the applications, screens,
        buttons and fields exactly as the steps do. Merge low-level steps into meaningful procedure steps (typically 5–25).
        Capture the expert's judgement as explicit WHEN/THEN rules. List things that could go wrong and how the expert avoided
        them. List what you could not determine as questions for the expert. Never include real personal data or secrets.

        The automationPrompt is a runbook for an AI agent that controls a desktop/browser: numbered steps, what to look for
        on screen before each action, the checks that prove each step worked, and when to stop and ask a human. It must stand
        alone (the agent hasn't seen the recording). Use {{placeholders}} for inputs that change each run.

        Answer with JSON only:
        {"title":"short task name","summary":"2-3 sentences","goal":"business outcome","applications":["…"],
         "inputs":["what the task needs"],"outputs":["what it produces"],
         "procedure":[{"number":1,"title":"…","instructions":"…","why":"…","check":"how to know it worked","sourceSteps":[1,2]}],
         "decisions":[{"when":"…","then":"…"}],"pitfalls":["…"],"glossary":["term — meaning"],"openQuestions":["…"],
         "automationPrompt":"…","suggestedCron":null,"automatability":0.0-1.0,"automatabilityReason":"…"}
        """;

    private async Task<SessionAnalysis> SynthesizeAsync(RecordingSession session, IReadOnlyList<StepDescription> steps, CancellationToken cancellationToken)
    {
        var m = session.Manifest;
        var sb = new StringBuilder();
        sb.AppendLine($"Recording \"{m.Title}\"{(m.Expert.Length > 0 ? $" by {m.Expert}" : "")}, {Platform(m.Platform)}, {TimeSpan.FromSeconds(m.DurationSeconds):h\\:mm\\:ss} long, recorded {m.StartedAt:yyyy-MM-dd ddd HH:mm}.");
        if (m.Description.Length > 0) sb.AppendLine($"The expert's description: {m.Description}");
        sb.AppendLine("\nSteps:");
        foreach (var s in steps)
        {
            sb.Append($"{s.Index}. [{StepCandidate.FormatTime(s.Start)}] {s.Action}");
            if (s.App is not null) sb.Append($" (in {s.App})");
            if (s.Why is { Length: > 0 }) sb.Append($" — why: {s.Why}");
            if (s.Result is { Length: > 0 }) sb.Append($" — result: {s.Result}");
            if (s.DataUsed.Count > 0) sb.Append($" — data: {string.Join("; ", s.DataUsed)}");
            if (s.Decision is { Length: > 0 }) sb.Append($" — decision: {s.Decision}");
            if (s.Note is { Length: > 0 }) sb.Append($" — EXPERT NOTE: {s.Note}");
            if (s.Confidence < 0.4) sb.Append(" (uncertain)");
            sb.AppendLine();
        }
        var result = await client.CompleteAsync([ChatMessage.System(SynthesizeSystem), ChatMessage.User(sb.ToString())],
            new ChatOptions { JsonObject = true, ReasoningEffort = options.ReasoningEffort, MaxTokens = 12000 }, cancellationToken).ConfigureAwait(false);
        _promptTokens += result.PromptTokens;
        _completionTokens += result.CompletionTokens;
        var json = JsonExtract.Object(result.Content);
        return FromJson(json, session, result.Model);
    }

    internal static SessionAnalysis FromJson(JsonObject json, RecordingSession session, string model)
    {
        var analysis = new SessionAnalysis
        {
            SessionId = session.Id,
            Title = Str(json["title"]) ?? session.Manifest.Title,
            Summary = Str(json["summary"]) ?? "",
            Goal = Str(json["goal"]) ?? "",
            Applications = Strs(json["applications"]),
            Inputs = Strs(json["inputs"]),
            Outputs = Strs(json["outputs"]),
            Pitfalls = Strs(json["pitfalls"]),
            Glossary = Strs(json["glossary"]),
            OpenQuestions = Strs(json["openQuestions"]),
            AutomationPrompt = Str(json["automationPrompt"]) ?? "",
            SuggestedCron = Str(json["suggestedCron"]) is { } cron && cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is 5 or 6 ? cron : null,
            Automatability = Math.Clamp(Num(json["automatability"]) ?? 0.5, 0, 1),
            AutomatabilityReason = Str(json["automatabilityReason"]),
            Model = model,
            CreatedAt = DateTimeOffset.Now,
        };
        var n = 0;
        foreach (var p in (json["procedure"] as JsonArray ?? []).OfType<JsonObject>())
        {
            analysis.Procedure.Add(new ProcedureStep
            {
                Number = (int?)Num(p["number"]) ?? ++n,
                Title = Str(p["title"]) ?? "",
                Instructions = Str(p["instructions"]) ?? "",
                Why = Str(p["why"]),
                Check = Str(p["check"]),
                SourceSteps = (p["sourceSteps"] as JsonArray ?? []).Select(Num).Where(v => v is not null).Select(v => (int)v!.Value).ToList(),
            });
            n = analysis.Procedure[^1].Number;
        }
        foreach (var d in (json["decisions"] as JsonArray ?? []).OfType<JsonObject>())
            if (Str(d["when"]) is { } when) analysis.Decisions.Add(new DecisionRule { When = when, Then = Str(d["then"]) ?? "" });
        if (string.IsNullOrWhiteSpace(analysis.AutomationPrompt)) analysis.Warnings.Add("The model wrote no automation runbook; one was built from the procedure.");
        if (string.IsNullOrWhiteSpace(analysis.AutomationPrompt))
            analysis.AutomationPrompt = string.Join("\n", analysis.Procedure.Select(p => $"{p.Number}. {p.Title}: {p.Instructions}{(p.Check is null ? "" : $" Check: {p.Check}")}"));
        return analysis;
    }

    // MARK: Stage 3 — the expert across recordings

    internal const string ExpertSystem = """
        You are building a knowledge profile of one expert from several analysed recordings of their work, so that Guardian can
        train a person (and an AI assistant) to be as good as they are. Combine what the sessions show: their responsibilities,
        the applications they use, each task they do (when and why), the rules of thumb behind their decisions, the mistakes they
        avoid, their vocabulary, and what is still unknown. Keep every rule concrete and attributable to what was observed.
        Answer with JSON only:
        {"role":"…","overview":"…","responsibilities":["…"],"applications":["…"],
         "tasks":[{"name":"…","when":"…","summary":"…","sessionId":"…"}],"rules":[{"when":"…","then":"…"}],
         "pitfalls":["…"],"glossary":["…"],"openQuestions":["…"]}
        """;

    public async Task<ExpertProfile> BuildExpertAsync(string expert, IReadOnlyList<SessionAnalysis> analyses, CancellationToken cancellationToken = default)
    {
        if (analyses.Count == 0) throw new AiError("Process at least one recording first.");
        var sb = new StringBuilder($"Expert: {expert}\n");
        foreach (var a in analyses)
        {
            sb.AppendLine($"\n## Session {a.SessionId}: {a.Title}\nGoal: {a.Goal}\nSummary: {a.Summary}\nApplications: {string.Join(", ", a.Applications)}");
            foreach (var p in a.Procedure) sb.AppendLine($"  {p.Number}. {p.Title} — {p.Instructions}");
            foreach (var d in a.Decisions) sb.AppendLine($"  RULE: when {d.When} then {d.Then}");
            foreach (var p in a.Pitfalls) sb.AppendLine($"  PITFALL: {p}");
            foreach (var q in a.OpenQuestions) sb.AppendLine($"  QUESTION: {q}");
        }
        var result = await client.CompleteAsync([ChatMessage.System(ExpertSystem), ChatMessage.User(sb.ToString())],
            new ChatOptions { JsonObject = true, ReasoningEffort = options.ReasoningEffort, MaxTokens = 8000 }, cancellationToken).ConfigureAwait(false);
        var json = JsonExtract.Object(result.Content);
        var profile = new ExpertProfile
        {
            Name = expert,
            Role = Str(json["role"]) ?? "",
            Overview = Str(json["overview"]) ?? "",
            Responsibilities = Strs(json["responsibilities"]),
            Applications = Strs(json["applications"]),
            Pitfalls = Strs(json["pitfalls"]),
            Glossary = Strs(json["glossary"]),
            OpenQuestions = Strs(json["openQuestions"]),
            SessionIds = analyses.Select(a => a.SessionId).ToList(),
            Model = result.Model,
            UpdatedAt = DateTimeOffset.Now,
        };
        foreach (var t in (json["tasks"] as JsonArray ?? []).OfType<JsonObject>())
            profile.Tasks.Add(new ExpertTask { Name = Str(t["name"]) ?? "", When = Str(t["when"]) ?? "", Summary = Str(t["summary"]) ?? "", SessionId = Str(t["sessionId"]) ?? "" });
        foreach (var r in (json["rules"] as JsonArray ?? []).OfType<JsonObject>())
            if (Str(r["when"]) is { } when) profile.Rules.Add(new DecisionRule { When = when, Then = Str(r["then"]) ?? "" });
        return profile;
    }

    // MARK: JSON helpers (models answer loosely)

    internal static string? Str(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        JsonValue v => v.ToJsonString(),
        JsonArray a => string.Join("\n", a.Select(Str).OfType<string>()),
        JsonObject o => o.ToJsonString(),
        _ => null,
    };

    internal static List<string> Strs(JsonNode? node) => node switch
    {
        JsonArray a => a.Select(x => x is JsonObject o ? string.Join(" — ", o.Select(kv => Str(kv.Value)).OfType<string>()) : Str(x)).OfType<string>().ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => [s.Trim()],
        _ => [],
    };

    internal static double? Num(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };
}
