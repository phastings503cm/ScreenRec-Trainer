using System.Text;
using ScreenRec.Core.Recording;

namespace ScreenRec.Core.Ai;

/// <summary>One thing the person did, as cut from the raw recording before any AI: a click, a burst of typing, a shortcut, a
/// scroll, an app switch or a note — with the frame just before and the frame after it.</summary>
public sealed record StepCandidate
{
    public int Index { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    /// <summary>click / double-click / right-click / type / shortcut / scroll / switch-app / note / observe.</summary>
    public string Kind { get; init; } = "";
    public string? App { get; init; }
    public string? Window { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    /// <summary>What was typed (only when typed-text capture was on; "•" bullets otherwise) or the note.</summary>
    public string? Text { get; init; }
    public IReadOnlyList<string> Keys { get; init; } = [];
    public FrameInfo? Before { get; init; }
    public FrameInfo? After { get; init; }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append($"[{FormatTime(Start)}] {Kind}");
        if (X is { } x && Y is { } y) sb.Append($" at ({x:0.00},{y:0.00})");
        if (Keys.Count > 0) sb.Append(" keys: ").Append(string.Join(" ", Keys.Take(12))).Append(Keys.Count > 12 ? " …" : "");
        if (!string.IsNullOrEmpty(Text)) sb.Append(Kind == "note" ? $" — expert's note: \"{Text}\"" : $" text: \"{Truncate(Text, 120)}\"");
        if (App is not null || Window is not null) sb.Append($" in {App ?? "?"}{(Window is { Length: > 0 } w ? $" — \"{Truncate(w, 80)}\"" : "")}");
        return sb.ToString();
    }

    public static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

public static class Segmenter
{
    private const double TypingGap = 1.5;   // seconds between keys that still count as one burst
    private const double DoubleClick = 0.45;
    private const double ScrollGap = 1.2;

    /// <summary>Cut a recording into steps. Long recordings are thinned to <paramref name="maxSteps"/> by merging the closest
    /// neighbours of the same kind and then by even sampling, so the model sees the whole session.</summary>
    public static IReadOnlyList<StepCandidate> Segment(IReadOnlyList<InputEvent> events, IReadOnlyList<FrameInfo> frames, int maxSteps = 60)
    {
        var steps = new List<StepCandidate>();
        string? app = null, window = null;
        var ordered = events.OrderBy(e => e.T).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            switch (e.Kind)
            {
                case InputKind.Focus:
                    var changedApp = e.App != app && e.App is not null && app is not null;
                    app = e.App ?? app;
                    window = e.Window ?? window;
                    if (changedApp) steps.Add(new StepCandidate { Start = e.T, End = e.T, Kind = "switch-app", App = app, Window = window });
                    break;
                case InputKind.MouseDown:
                    var kind = e.Button switch { "right" => "right-click", "middle" => "middle-click", _ => "click" };
                    // Drag: a mouse-up far from the mouse-down.
                    var up = ordered.Skip(i + 1).FirstOrDefault(u => u.Kind == InputKind.MouseUp && u.Button == e.Button);
                    if (up is not null && e.X is { } x0 && up.X is { } x1 && e.Y is { } y0 && up.Y is { } y1 &&
                        Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) > 0.02)
                        kind = "drag";
                    if (kind == "click" && steps.Count > 0 && steps[^1] is { Kind: "click" } prev && e.T - prev.Start < DoubleClick &&
                        Near(prev.X, prev.Y, e.X, e.Y))
                    {
                        steps[^1] = prev with { Kind = "double-click", End = e.T };
                        break;
                    }
                    steps.Add(new StepCandidate { Start = e.T, End = up?.T ?? e.T, Kind = kind, X = e.X, Y = e.Y, App = app, Window = window });
                    break;
                case InputKind.Key:
                    var key = e.Button ?? "";
                    var isShortcut = key.Contains('+') && (key.Contains("Ctrl") || key.Contains("Cmd") || key.Contains("Alt") || key.Contains("Option") || key.Contains("Win"));
                    if (isShortcut)
                    {
                        steps.Add(new StepCandidate { Start = e.T, End = e.T, Kind = "shortcut", Keys = [key], App = app, Window = window });
                        break;
                    }
                    var ch = e.Text;
                    if (steps.Count > 0 && steps[^1] is { Kind: "type" } burst && e.T - burst.End < TypingGap && burst.App == app)
                    {
                        steps[^1] = burst with
                        {
                            End = e.T,
                            Text = ch is not null ? (burst.Text ?? "") + ch : burst.Text,
                            Keys = ch is null ? [.. burst.Keys, key] : burst.Keys,
                        };
                    }
                    else
                    {
                        steps.Add(new StepCandidate { Start = e.T, End = e.T, Kind = "type", Text = ch, Keys = ch is null ? [key] : [], App = app, Window = window });
                    }
                    break;
                case InputKind.Scroll:
                    if (steps.Count > 0 && steps[^1] is { Kind: "scroll" } s && e.T - s.End < ScrollGap)
                        steps[^1] = s with { End = e.T };
                    else
                        steps.Add(new StepCandidate { Start = e.T, End = e.T, Kind = "scroll", X = e.X, Y = e.Y, App = app, Window = window });
                    break;
                case InputKind.Note:
                    steps.Add(new StepCandidate { Start = e.T, End = e.T, Kind = "note", Text = e.Text, App = app, Window = window });
                    break;
            }
        }

        // No input at all (Input Monitoring off): fall back to the moments the screen changed most.
        if (steps.Count(s => s.Kind != "note" && s.Kind != "switch-app") == 0 && frames.Count > 0)
        {
            var picked = frames.Where(f => f.Index == 0 || f.Change >= 0.02).ToList();
            if (picked.Count == 0) picked = [frames[0]];
            steps.AddRange(picked.Select(f => new StepCandidate { Start = f.T, End = f.T, Kind = "observe", App = f.App, Window = f.Window }));
            steps.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        steps = Thin(steps, maxSteps);
        return steps.Select((s, n) => s with { Index = n + 1, Before = FrameAtOrBefore(frames, s.Start - 0.05) ?? FrameAtOrAfter(frames, s.Start), After = FrameAfter(frames, s.End) }).ToList();
    }

    private static bool Near(double? x0, double? y0, double? x1, double? y1) =>
        x0 is null || x1 is null || y0 is null || y1 is null || Math.Abs(x0.Value - x1.Value) < 0.01 && Math.Abs(y0.Value - y1.Value) < 0.01;

    internal static List<StepCandidate> Thin(List<StepCandidate> steps, int maxSteps)
    {
        maxSteps = Math.Max(3, maxSteps);
        // Merge runs of scrolls and app switches first: they carry little on their own.
        while (steps.Count > maxSteps)
        {
            var merged = false;
            for (var i = steps.Count - 1; i > 0 && steps.Count > maxSteps; i--)
            {
                if (steps[i].Kind == steps[i - 1].Kind && steps[i].Kind is "scroll" or "switch-app" or "type" or "observe" && steps[i].App == steps[i - 1].App)
                {
                    steps[i - 1] = steps[i - 1] with
                    {
                        End = steps[i].End,
                        Text = steps[i - 1].Text is null && steps[i].Text is null ? null : (steps[i - 1].Text ?? "") + " " + (steps[i].Text ?? ""),
                        Keys = [.. steps[i - 1].Keys, .. steps[i].Keys],
                    };
                    steps.RemoveAt(i);
                    merged = true;
                }
            }
            if (!merged) break;
        }
        if (steps.Count <= maxSteps) return steps;
        // Even sampling, always keeping the expert's notes.
        var notes = steps.Where(s => s.Kind == "note").ToList();
        var rest = steps.Where(s => s.Kind != "note").ToList();
        var budget = Math.Max(1, maxSteps - notes.Count);
        var sampled = Enumerable.Range(0, Math.Min(budget, rest.Count)).Select(i => rest[(int)Math.Round(i * (rest.Count - 1) / Math.Max(1.0, budget - 1))]).Distinct();
        return sampled.Concat(notes).OrderBy(s => s.Start).ToList();
    }

    internal static FrameInfo? FrameAtOrBefore(IReadOnlyList<FrameInfo> frames, double t) => frames.LastOrDefault(f => f.T <= t);
    internal static FrameInfo? FrameAtOrAfter(IReadOnlyList<FrameInfo> frames, double t) => frames.FirstOrDefault(f => f.T >= t);

    /// <summary>The frame that shows the result: the first one at least 0.6 s after the action (the UI has reacted), else the last.</summary>
    internal static FrameInfo? FrameAfter(IReadOnlyList<FrameInfo> frames, double t) =>
        frames.FirstOrDefault(f => f.T >= t + 0.6) ?? frames.LastOrDefault();
}
