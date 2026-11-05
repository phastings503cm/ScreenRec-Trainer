using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ScreenRec.Core.Ai;
using ScreenRec.Core.Recording;

namespace ScreenRec.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "screenrec-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

/// <summary>Answers HTTP requests from a function and records what was sent.</summary>
public sealed class FakeHttp(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Body, Dictionary<string, string> Headers)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        lock (Requests) Requests.Add((request.Method, request.RequestUri!.ToString(), body, headers));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>A model that answers the pipeline's two prompts with plausible JSON.</summary>
public sealed class FakeModel : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();
    public string Describe => "fake";

    public Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken = default)
    {
        lock (Calls) Calls.Add(messages);
        var system = ((TextPart)messages[0].Parts[0]).Text;
        string content;
        if (system == AnalysisPipeline.DescribeSystem)
        {
            var text = string.Join("\n", messages[1].Parts.OfType<TextPart>().Select(p => p.Text));
            var indexes = System.Text.RegularExpressions.Regex.Matches(text, @"\nStep (\d+):").Select(m => int.Parse(m.Groups[1].Value)).ToList();
            var steps = new JsonArray();
            foreach (var i in indexes)
                steps.Add(new JsonObject { ["index"] = i, ["action"] = $"Click Button {i}", ["target"] = $"Button {i}", ["why"] = "to move on", ["result"] = "a dialog opens", ["confidence"] = 0.9, ["decision"] = i == 1 ? "If the total is negative, stop" : null });
            content = "```json\n" + new JsonObject { ["steps"] = steps }.ToJsonString() + "\n```";
        }
        else if (system == AnalysisPipeline.SynthesizeSystem)
        {
            content = """
                Here you go: {"title":"Reconcile vendor invoices","summary":"Matches invoices to payments.","goal":"Close the month",
                "applications":["Excel","QuickBooks"],"inputs":["invoice list"],"outputs":["reconciled ledger"],
                "procedure":[{"number":1,"title":"Open the ledger","instructions":"Open QuickBooks and go to Vendors.","why":"start","check":"Vendor list shows","sourceSteps":[1]},
                             {"number":2,"title":"Match","instructions":"Match each invoice.","sourceSteps":[2,3]}],
                "decisions":[{"when":"the total is negative","then":"stop and ask"}],"pitfalls":["Don't double-post"],
                "glossary":["AP — accounts payable"],"openQuestions":["Who approves over $10k?"],
                "automationPrompt":"1. Open QuickBooks.\n2. Match invoices {{month}}.","suggestedCron":"0 9 1 * *","automatability":0.7,"automatabilityReason":"rule based",}
                """;
        }
        else
        {
            content = """
                {"role":"AP specialist","overview":"Runs payables.","responsibilities":["month end"],"applications":["QuickBooks"],
                "tasks":[{"name":"Reconcile vendor invoices","when":"monthly","summary":"…","sessionId":"x"}],"rules":[{"when":"negative","then":"stop"}],
                "pitfalls":["double posting"],"glossary":[],"openQuestions":["approval limits"]}
                """;
        }
        return Task.FromResult(new ChatResult(content, null, 100, 50, "fake-model"));
    }
}

public static class SampleSessions
{
    /// <summary>A recorded session with frames and a realistic event stream (no screen needed).</summary>
    public static RecordingSession Create(string root, string expert = "Maria")
    {
        var session = RecordingSession.Create(root, new SessionManifest
        {
            StartedAt = DateTimeOffset.Now, Title = "Vendor reconciliation", Description = "Month-end AP", Expert = expert, Platform = "macos",
            DurationSeconds = 20, Status = SessionStatus.Recorded,
        });
        var capture = new SyntheticScreenCapture(640, 400);
        var frames = new List<FrameInfo>();
        using (var avi = new MjpegAviWriter(Path.Combine(session.Folder, "recording.avi"), 640, 400, 2))
        {
            for (var i = 0; i < 10; i++)
            {
                using var f = capture.Capture(capture.Displays()[0]);
                var jpeg = ImageTools.Jpeg(f.Bitmap, 70);
                avi.WriteFrame(jpeg);
                var name = $"{i:D6}.jpg";
                File.WriteAllBytes(Path.Combine(session.FramesFolder, name), jpeg);
                frames.Add(new FrameInfo { Index = i, T = i * 2, File = name, Width = 640, Height = 400, App = "QuickBooks", Change = 0.1 });
            }
        }
        session.Manifest.VideoFile = "recording.avi";
        File.WriteAllLines(session.FramesFile, frames.Select(f => System.Text.Json.JsonSerializer.Serialize(f, RecordingSession.Line)));
        var events = new List<InputEvent>
        {
            new() { T = 0, Kind = InputKind.Focus, App = "QuickBooks", Window = "Vendors" },
            new() { T = 1, Kind = InputKind.MouseDown, X = 0.2, Y = 0.3, Button = "left" },
            new() { T = 1.1, Kind = InputKind.MouseUp, X = 0.2, Y = 0.3, Button = "left" },
            new() { T = 4, Kind = InputKind.Key, Button = "A", Text = "a" },
            new() { T = 4.2, Kind = InputKind.Key, Button = "B", Text = "b" },
            new() { T = 4.4, Kind = InputKind.Key, Button = "Enter" },
            new() { T = 7, Kind = InputKind.Note, Text = "check totals" },
            new() { T = 9, Kind = InputKind.Key, Button = "Cmd+S" },
            new() { T = 12, Kind = InputKind.Focus, App = "Excel", Window = "Book1" },
            new() { T = 14, Kind = InputKind.Scroll, X = 0.5, Y = 0.5, Delta = -3 },
        };
        File.WriteAllLines(session.EventsFile, events.Select(e => System.Text.Json.JsonSerializer.Serialize(e, RecordingSession.Line)));
        session.Manifest.FrameCount = frames.Count;
        session.Manifest.EventCount = events.Count;
        session.Save();
        return session;
    }
}
