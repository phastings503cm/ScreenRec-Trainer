using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ScreenRec.Core.Ai;
using ScreenRec.Core.Recording;

namespace ScreenRec.Core.Export;

/// <summary>Turns an analysis into what people and Guardian use: a training guide (Markdown + self-contained HTML with
/// screenshots), a Guardian skill (SKILL.md with the procedure, rules and pitfalls) and the automation runbook.</summary>
public static class Exporters
{
    public static string Slug(string text, int max = 48)
    {
        var slug = Regex.Replace((text ?? "").ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > max) slug = slug[..max].TrimEnd('-');
        return slug.Length == 0 ? "recorded-task" : slug;
    }

    public static string SkillName(SessionAnalysis analysis, string? expert) =>
        Slug((string.IsNullOrWhiteSpace(expert) ? "" : expert + " ") + analysis.Title, 60);

    public static string TrainingMarkdown(SessionAnalysis a, RecordingSession session, bool linkScreens = true)
    {
        var m = session.Manifest;
        var sb = new StringBuilder();
        sb.AppendLine($"# {a.Title}");
        sb.AppendLine();
        sb.AppendLine($"_Learned from a {TimeSpan.FromSeconds(m.DurationSeconds):h\\:mm\\:ss} screen recording{(m.Expert.Length > 0 ? $" of **{m.Expert}**" : "")} on {m.StartedAt:yyyy-MM-dd}. Model: {a.Model}._");
        sb.AppendLine();
        if (a.Summary.Length > 0) sb.AppendLine(a.Summary).AppendLine();
        if (a.Goal.Length > 0) sb.AppendLine($"**Goal:** {a.Goal}").AppendLine();
        List(sb, "Applications", a.Applications);
        List(sb, "You need", a.Inputs);
        List(sb, "You produce", a.Outputs);

        sb.AppendLine("## Procedure").AppendLine();
        foreach (var p in a.Procedure)
        {
            sb.AppendLine($"### {p.Number}. {p.Title}").AppendLine();
            sb.AppendLine(p.Instructions).AppendLine();
            if (p.Why is { Length: > 0 }) sb.AppendLine($"*Why:* {p.Why}").AppendLine();
            if (p.Check is { Length: > 0 }) sb.AppendLine($"*Check:* {p.Check}").AppendLine();
            if (linkScreens && ScreenFor(a, p) is { } frame) sb.AppendLine($"![Step {p.Number}](frames/{frame})").AppendLine();
        }
        if (a.Decisions.Count > 0)
        {
            sb.AppendLine("## How the expert decides").AppendLine();
            foreach (var d in a.Decisions) sb.AppendLine($"- **When** {d.When} → **then** {d.Then}");
            sb.AppendLine();
        }
        List(sb, "Watch out for", a.Pitfalls, "##");
        List(sb, "Glossary", a.Glossary, "##");
        List(sb, "Questions for the expert", a.OpenQuestions, "##");
        sb.AppendLine("## Automation").AppendLine();
        sb.AppendLine($"Automatability: **{a.Automatability:P0}**{(a.AutomatabilityReason is { Length: > 0 } r ? $" — {r}" : "")}").AppendLine();
        sb.AppendLine("```text").AppendLine(a.AutomationPrompt.Trim()).AppendLine("```").AppendLine();
        sb.AppendLine("## Recorded steps (detail)").AppendLine();
        foreach (var s in a.Steps)
            sb.AppendLine($"{s.Index}. `{StepCandidate.FormatTime(s.Start)}` {s.Action}{(s.Why is { Length: > 0 } ? $" — {s.Why}" : "")}{(s.Confidence < 0.4 ? " _(uncertain)_" : "")}");
        return sb.ToString();
    }

    private static string? ScreenFor(SessionAnalysis a, ProcedureStep p)
    {
        foreach (var n in p.SourceSteps)
            if (a.Steps.FirstOrDefault(s => s.Index == n) is { } step) return step.AfterFrame ?? step.BeforeFrame;
        return null;
    }

    private static void List(StringBuilder sb, string title, IReadOnlyList<string> items, string level = "**")
    {
        if (items.Count == 0) return;
        if (level == "**") sb.AppendLine($"**{title}:**");
        else sb.AppendLine($"{level} {title}").AppendLine();
        foreach (var i in items) sb.AppendLine($"- {i}");
        sb.AppendLine();
    }

    /// <summary>One HTML file with the screenshots embedded — opens anywhere, emails as one attachment.</summary>
    public static string TrainingHtml(SessionAnalysis a, RecordingSession session)
    {
        var md = TrainingMarkdown(a, session, linkScreens: true);
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>")
            .Append(WebUtility.HtmlEncode(a.Title))
            .Append("</title><style>body{font-family:system-ui,-apple-system,'Segoe UI',sans-serif;max-width:60rem;margin:2rem auto;padding:0 1rem;line-height:1.5;color:#1d1d1f}")
            .Append("img{max-width:100%;border:1px solid #ccc;border-radius:6px}pre{background:#f4f4f6;padding:1rem;white-space:pre-wrap;border-radius:6px}code{background:#f4f4f6;padding:0 .25rem}")
            .Append("h3{margin-top:2rem}</style></head><body>");
        var inCode = false;
        var inList = false;
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                html.Append(inCode ? "</pre>" : "<pre>");
                inCode = !inCode;
                continue;
            }
            if (inCode) { html.Append(WebUtility.HtmlEncode(line)).Append('\n'); continue; }
            var isItem = line.StartsWith("- ", StringComparison.Ordinal) || Regex.IsMatch(line, @"^\d+\. ");
            if (inList && !isItem) { html.Append("</ul>"); inList = false; }
            if (line.StartsWith("### ", StringComparison.Ordinal)) html.Append("<h3>").Append(Inline(line[4..])).Append("</h3>");
            else if (line.StartsWith("## ", StringComparison.Ordinal)) html.Append("<h2>").Append(Inline(line[3..])).Append("</h2>");
            else if (line.StartsWith("# ", StringComparison.Ordinal)) html.Append("<h1>").Append(Inline(line[2..])).Append("</h1>");
            else if (Regex.Match(line, @"^!\[(.*?)\]\(frames/(.+?)\)$") is { Success: true } img)
            {
                var path = Path.Combine(session.FramesFolder, img.Groups[2].Value);
                if (File.Exists(path))
                    html.Append("<p><img alt=\"").Append(WebUtility.HtmlEncode(img.Groups[1].Value)).Append("\" src=\"data:image/jpeg;base64,")
                        .Append(Convert.ToBase64String(ImageTools.PrepareForModel(path, 1400, null, 80))).Append("\"></p>");
            }
            else if (isItem)
            {
                if (!inList) { html.Append("<ul>"); inList = true; }
                html.Append("<li>").Append(Inline(Regex.Replace(line, @"^(- |\d+\. )", ""))).Append("</li>");
            }
            else if (line.Length > 0) html.Append("<p>").Append(Inline(line)).Append("</p>");
        }
        if (inList) html.Append("</ul>");
        return html.Append("</body></html>").ToString();
    }

    private static string Inline(string text)
    {
        var s = WebUtility.HtmlEncode(text);
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<![\w*])_(.+?)_(?!\w)", "<em>$1</em>");
        s = Regex.Replace(s, @"\*(.+?)\*", "<em>$1</em>");
        s = Regex.Replace(s, @"`(.+?)`", "<code>$1</code>");
        return s;
    }

    /// <summary>A Guardian / Agent Skills SKILL.md: front matter (name, description = when to use it), then the procedure the
    /// way the expert does it. Guardian chats and workers load it when a request matches.</summary>
    public static string SkillMarkdown(SessionAnalysis a, string? expert)
    {
        var name = SkillName(a, expert);
        var who = string.IsNullOrWhiteSpace(expert) ? "the expert" : expert!.Trim();
        var description = Oneline($"How {who} does \"{a.Title}\"{(a.Applications.Count > 0 ? $" in {string.Join(", ", a.Applications.Take(4))}" : "")}. Use when asked to {Lower(a.Goal.Length > 0 ? a.Goal : a.Title)}, or to train someone on it.");
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"description: {Yaml(description)}");
        sb.AppendLine("---").AppendLine();
        sb.AppendLine($"# {a.Title}").AppendLine();
        sb.AppendLine($"Learned by watching {who} do this on screen ({a.CreatedAt:yyyy-MM-dd}). Follow it the way they do; where it says *check*, confirm before going on.").AppendLine();
        if (a.Summary.Length > 0) sb.AppendLine(a.Summary).AppendLine();
        if (a.Inputs.Count > 0) { sb.AppendLine("## Inputs"); foreach (var i in a.Inputs) sb.AppendLine($"- {i}"); sb.AppendLine(); }
        sb.AppendLine("## Steps");
        foreach (var p in a.Procedure)
        {
            sb.AppendLine($"{p.Number}. **{p.Title}** — {p.Instructions}");
            if (p.Check is { Length: > 0 }) sb.AppendLine($"   - Check: {p.Check}");
            if (p.Why is { Length: > 0 }) sb.AppendLine($"   - Why: {p.Why}");
        }
        sb.AppendLine();
        if (a.Decisions.Count > 0) { sb.AppendLine("## Rules of thumb"); foreach (var d in a.Decisions) sb.AppendLine($"- When {d.When}: {d.Then}"); sb.AppendLine(); }
        if (a.Pitfalls.Count > 0) { sb.AppendLine("## Pitfalls"); foreach (var p in a.Pitfalls) sb.AppendLine($"- {p}"); sb.AppendLine(); }
        if (a.Outputs.Count > 0) { sb.AppendLine("## Done when"); foreach (var o in a.Outputs) sb.AppendLine($"- {o}"); sb.AppendLine(); }
        if (a.OpenQuestions.Count > 0) { sb.AppendLine("## Not yet known (ask the expert)"); foreach (var q in a.OpenQuestions) sb.AppendLine($"- {q}"); sb.AppendLine(); }
        return sb.ToString();
    }

    /// <summary>The expert's whole skill (across recordings): the "train a person to be an expert at her stuff" artifact.</summary>
    public static string ExpertSkillMarkdown(ExpertProfile e, IReadOnlyList<SessionAnalysis> analyses)
    {
        var name = Slug(e.Name + " expert", 60);
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"description: {Yaml(Oneline($"Work the way {e.Name} does{(e.Role.Length > 0 ? $" ({e.Role})" : "")}: {string.Join("; ", e.Tasks.Select(t => t.Name).Take(8))}. Use for any of these tasks, to answer how {e.Name} would handle something, or to coach a trainee."))}");
        sb.AppendLine("---").AppendLine();
        sb.AppendLine($"# Working like {e.Name}").AppendLine();
        if (e.Overview.Length > 0) sb.AppendLine(e.Overview).AppendLine();
        if (e.Responsibilities.Count > 0) { sb.AppendLine("## Responsibilities"); foreach (var r in e.Responsibilities) sb.AppendLine($"- {r}"); sb.AppendLine(); }
        if (e.Applications.Count > 0) sb.AppendLine($"**Tools:** {string.Join(", ", e.Applications)}").AppendLine();
        if (e.Tasks.Count > 0)
        {
            sb.AppendLine("## Tasks");
            foreach (var t in e.Tasks) sb.AppendLine($"- **{t.Name}**{(t.When.Length > 0 ? $" (when: {t.When})" : "")} — {t.Summary}");
            sb.AppendLine();
        }
        if (e.Rules.Count > 0) { sb.AppendLine("## How they decide"); foreach (var r in e.Rules) sb.AppendLine($"- When {r.When}: {r.Then}"); sb.AppendLine(); }
        if (e.Pitfalls.Count > 0) { sb.AppendLine("## Mistakes they avoid"); foreach (var p in e.Pitfalls) sb.AppendLine($"- {p}"); sb.AppendLine(); }
        if (e.Glossary.Count > 0) { sb.AppendLine("## Vocabulary"); foreach (var g in e.Glossary) sb.AppendLine($"- {g}"); sb.AppendLine(); }
        if (e.OpenQuestions.Count > 0) { sb.AppendLine("## Still to learn from them"); foreach (var q in e.OpenQuestions) sb.AppendLine($"- {q}"); sb.AppendLine(); }
        sb.AppendLine("## Procedures");
        sb.AppendLine("Each recorded task's full procedure is in `procedures/`. Read the one that matches before doing the task.");
        foreach (var a in analyses) sb.AppendLine($"- `procedures/{Slug(a.Title)}.md` — {a.Title}");
        return sb.ToString();
    }

    public static Dictionary<string, string> ExpertSkillFiles(ExpertProfile e, IReadOnlyList<SessionAnalysis> analyses)
    {
        var files = new Dictionary<string, string> { ["SKILL.md"] = ExpertSkillMarkdown(e, analyses) };
        foreach (var a in analyses)
            files[$"procedures/{Slug(a.Title)}.md"] = SkillMarkdown(a, e.Name);
        return files;
    }

    /// <summary>The prompt a Guardian automation gets: the runbook plus the rules, so the worker decides like the expert.</summary>
    public static string AutomationPrompt(SessionAnalysis a, string? expert)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task: {a.Title}{(string.IsNullOrWhiteSpace(expert) ? "" : $" — done the way {expert} does it")}.");
        if (a.Goal.Length > 0) sb.AppendLine($"Goal: {a.Goal}");
        sb.AppendLine().AppendLine(a.AutomationPrompt.Trim());
        if (a.Decisions.Count > 0)
        {
            sb.AppendLine().AppendLine("Decision rules:");
            foreach (var d in a.Decisions) sb.AppendLine($"- When {d.When}: {d.Then}");
        }
        if (a.Pitfalls.Count > 0)
        {
            sb.AppendLine().AppendLine("Avoid:");
            foreach (var p in a.Pitfalls) sb.AppendLine($"- {p}");
        }
        sb.AppendLine().AppendLine("If the screen doesn't match what a step expects, or a rule above doesn't cover the situation, stop and report what you see instead of guessing.");
        return sb.ToString();
    }

    public static void WriteAll(SessionAnalysis a, RecordingSession session)
    {
        var dir = session.AnalysisFolder;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "training.md"), TrainingMarkdown(a, session).Replace("](frames/", "](../frames/"));
        File.WriteAllText(Path.Combine(dir, "training.html"), TrainingHtml(a, session));
        Directory.CreateDirectory(Path.Combine(dir, "skill"));
        File.WriteAllText(Path.Combine(dir, "skill", "SKILL.md"), SkillMarkdown(a, session.Manifest.Expert));
        File.WriteAllText(Path.Combine(dir, "automation.txt"), AutomationPrompt(a, session.Manifest.Expert));
    }

    private static string Oneline(string s) => Regex.Replace(s, @"\s+", " ").Trim();
    private static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..].TrimEnd('.') : s;
    private static string Yaml(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
