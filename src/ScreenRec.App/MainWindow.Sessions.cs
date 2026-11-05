using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ScreenRec.Core;
using ScreenRec.Core.Ai;
using ScreenRec.Core.Export;
using ScreenRec.Core.Recording;

namespace ScreenRec.App;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<string> _sessions = new();
    private List<RecordingSession> _sessionObjects = new();
    private readonly ListBox _sessionList = new() { MinWidth = 300 };
    private readonly StackPanel _detail = new() { Spacing = 10 };
    private readonly Button _process = new() { Content = "Process with AI" };
    private readonly Button _publish = new() { Content = "Publish to Guardian…" };
    private readonly Button _openGuide = new() { Content = "Open training guide" };
    private readonly Button _openFolder = new() { Content = "Show files" };
    private readonly Button _delete = new() { Content = "Delete" };
    private readonly ProgressBar _progress = new() { IsVisible = false, Minimum = 0, Maximum = 1 };
    private readonly TextBlock _progressText = Label("", "muted");
    private readonly TextBlock _detailError = Label("", "error");
    private CancellationTokenSource? _work;

    private RecordingSession? Selected => _sessionList.SelectedIndex is var i && i >= 0 && i < _sessionObjects.Count ? _sessionObjects[i] : null;

    private Control BuildSessionsTab()
    {
        _sessionList.ItemsSource = _sessions;
        _sessionList.SelectionChanged += (_, _) => ShowDetail();
        _detailError.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) _detailError.IsVisible = !string.IsNullOrEmpty(_detailError.Text); };
        _progressText.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) _progressText.IsVisible = !string.IsNullOrEmpty(_progressText.Text); };
        _detailError.IsVisible = _progressText.IsVisible = false;
        _process.Click += async (_, _) => await ProcessSelectedAsync();
        _publish.Click += async (_, _) => await PublishSelectedAsync();
        _openGuide.Click += (_, _) => { if (Selected is { } s) Open(Path.Combine(s.AnalysisFolder, "training.html")); };
        _openFolder.Click += (_, _) => { if (Selected is { } s) Open(s.Folder); };
        _delete.Click += (_, _) =>
        {
            if (Selected is not { } s || _work is not null) return;
            s.Delete();
            RefreshSessions();
        };

        var left = new DockPanel();
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(0, 0, 0, 8) };
        refresh.Click += (_, _) => RefreshSessions();
        DockPanel.SetDock(refresh, Dock.Top);
        left.Children.Add(refresh);
        left.Children.Add(_sessionList);

        var right = Page(Stack(10,
            Row(8, _process, _publish, _openGuide, _openFolder, _delete),
            _progress, _progressText, _detailError, _detail));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*"), Margin = new Thickness(8) };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        RefreshSessions();
        return grid;
    }

    private void RefreshSessions(string? select = null)
    {
        var keep = select ?? Selected?.Id;
        _sessionObjects = _ws.Sessions().ToList();
        _sessions.Clear();
        foreach (var s in _sessionObjects)
        {
            var m = s.Manifest;
            _sessions.Add($"{m.Title}\n{m.StartedAt.ToLocalTime():g} · {TimeSpan.FromSeconds(m.DurationSeconds):h\\:mm\\:ss} · {Status(m.Status)}{(m.Expert.Length > 0 ? $" · {m.Expert}" : "")}");
        }
        var index = keep is null ? -1 : _sessionObjects.FindIndex(s => s.Id == keep);
        _sessionList.SelectedIndex = index >= 0 ? index : _sessionObjects.Count > 0 ? 0 : -1;
        ShowDetail();
    }

    private static string Status(SessionStatus s) => s switch
    {
        SessionStatus.Recorded => "recorded",
        SessionStatus.Processing => "processing…",
        SessionStatus.Processed => "processed ✓",
        SessionStatus.Published => "in Guardian ✓",
        SessionStatus.Failed => "needs attention",
        _ => s.ToString().ToLowerInvariant(),
    };

    private void UpdatePublishEnabled()
    {
        var s = Selected;
        var analysis = s is null ? null : AnalysisPipeline.Load(s);
        _process.IsEnabled = s is not null && _work is null && s.Manifest.Status != SessionStatus.Recording;
        _process.Content = analysis is null ? "Process with AI" : "Process again";
        _publish.IsEnabled = analysis is not null && _ws.IsSignedIn && _work is null;
        _openGuide.IsEnabled = analysis is not null;
        _openFolder.IsEnabled = _delete.IsEnabled = s is not null && _work is null;
        ToolTip.SetTip(_publish, _ws.IsSignedIn ? null : "Sign in on the Guardian tab first.");
    }

    private void ShowDetail()
    {
        _detail.Children.Clear();
        _detailError.Text = "";
        UpdatePublishEnabled();
        if (Selected is not { } s)
        {
            _detail.Children.Add(Label("No recording selected. Make one on the Record tab.", "muted"));
            return;
        }
        var m = s.Manifest;
        _detail.Children.Add(Label(m.Title, "h2"));
        _detail.Children.Add(Label($"{m.StartedAt.ToLocalTime():f} · {m.DurationSeconds:0} s · {m.FrameCount} key frames · {m.EventCount} events · {m.Platform}{(m.Expert.Length > 0 ? $" · expert: {m.Expert}" : "")}", "muted"));
        if (m.Description.Length > 0) _detail.Children.Add(Label("“" + m.Description + "”"));
        if (m.LastError is { Length: > 0 } err) _detailError.Text = err;
        foreach (var w in m.Warnings) _detail.Children.Add(Label("⚠ " + w, "muted"));
        if (m.Guardian.AutomationId is { } aid && _ws.Guardian is { } g)
            _detail.Children.Add(LinkButton($"Guardian automation → {aid}", g.AutomationUrl(aid)));
        if (m.Guardian.VideoIntakeId is { } vid && _ws.Guardian is { } g2)
            _detail.Children.Add(LinkButton($"Guardian video intake → {vid} ({m.Guardian.VideoIntakeStatus})", g2.VideoUrl(vid)));
        if (m.Guardian.SkillName is { } skill) _detail.Children.Add(Label($"Guardian skill: {skill}", "muted"));

        var analysis = AnalysisPipeline.Load(s);
        if (analysis is null)
        {
            var frames = s.ReadFrames();
            if (frames.Count > 0) _detail.Children.Add(Thumb(s.FramePath(frames[^1]), 520));
            _detail.Children.Add(Label("Not processed yet. \"Process with AI\" sends the key frames and events to the model chosen in Settings (your GLM Spark, Guardian, or another server).", "muted"));
            return;
        }
        _detail.Children.Add(Card(Stack(6,
            Label(analysis.Title, "h2"),
            Label(analysis.Summary),
            Label($"Goal: {analysis.Goal}", "muted"),
            Label($"Apps: {string.Join(", ", analysis.Applications)} · Automatability {analysis.Automatability:P0}{(analysis.AutomatabilityReason is { } r ? $" — {r}" : "")}", "muted"),
            Label($"Model {analysis.Model} · {analysis.PromptTokens + analysis.CompletionTokens:N0} tokens", "muted"))));

        var proc = Stack(8, Label("Procedure", "h2"));
        foreach (var p in analysis.Procedure)
        {
            var item = Stack(2, Label($"{p.Number}. {p.Title}", null), Label(p.Instructions, "muted"));
            ((TextBlock)item.Children[0]).FontWeight = FontWeight.SemiBold;
            if (p.Check is { Length: > 0 }) item.Children.Add(Label("✓ " + p.Check, "muted"));
            var frame = p.SourceSteps.Select(n => analysis.Steps.FirstOrDefault(x => x.Index == n)).FirstOrDefault(x => x is not null);
            var file = frame?.AfterFrame ?? frame?.BeforeFrame;
            if (file is not null && File.Exists(Path.Combine(s.FramesFolder, file))) item.Children.Add(Thumb(Path.Combine(s.FramesFolder, file), 360));
            proc.Children.Add(item);
        }
        _detail.Children.Add(Card(proc));
        if (analysis.Decisions.Count > 0)
            _detail.Children.Add(Card(Stack(4, [Label("How the expert decides", "h2"), .. analysis.Decisions.Select(d => Label($"• When {d.When} → {d.Then}"))])));
        if (analysis.Pitfalls.Count > 0)
            _detail.Children.Add(Card(Stack(4, [Label("Watch out for", "h2"), .. analysis.Pitfalls.Select(p => Label("• " + p))])));
        if (analysis.OpenQuestions.Count > 0)
            _detail.Children.Add(Card(Stack(4, [Label("Questions for the expert", "h2"), .. analysis.OpenQuestions.Select(p => Label("• " + p))])));
        var runbook = new TextBox { Text = Exporters.AutomationPrompt(analysis, m.Expert), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Menlo, Consolas, monospace"), MaxHeight = 320 };
        _detail.Children.Add(Card(Stack(6, Label("Automation runbook (what Guardian's worker gets)", "h2"), runbook)));
    }

    private static Button LinkButton(string text, string url)
    {
        var b = new Button { Content = text, Classes = { "link" }, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Color.Parse("#3b82f6")) };
        b.Click += (_, _) => Open(url);
        return b;
    }

    private static Control Thumb(string path, double width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Image { Source = Bitmap.DecodeToWidth(stream, (int)width * 2), Width = width, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform };
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        {
            return Label("(screenshot unavailable)", "muted");
        }
    }

    private void Busy(bool on, string text = "")
    {
        _progress.IsVisible = on;
        _progress.IsIndeterminate = on;
        _progressText.Text = text;
        UpdatePublishEnabled();
    }

    private async Task ProcessSelectedAsync()
    {
        if (Selected is not { } s) return;
        _work = new CancellationTokenSource();
        Busy(true, "Connecting to the model…");
        _detailError.Text = "";
        var progress = new Progress<PipelineProgress>(p =>
        {
            _progressText.Text = p.Message;
            if (p.Total > 0 && p.Stage == "describe")
            {
                _progress.IsIndeterminate = false;
                _progress.Value = p.Done / (double)p.Total;
            }
            else _progress.IsIndeterminate = true;
        });
        try
        {
            await Task.Run(() => _ws.ProcessAsync(s, progress, _work.Token));
            _progressText.Text = "Processed. Review below, then publish to Guardian.";
        }
        catch (Exception e)
        {
            _progressText.Text = "";
            _detailError.Text = "Processing failed: " + e.Message;
        }
        finally
        {
            _work.Dispose();
            _work = null;
            Busy(false, _progressText.Text ?? "");
            var keep = _detailError.Text;
            RefreshSessions(s.Id);
            if (!string.IsNullOrEmpty(keep)) _detailError.Text = keep;
        }
    }

    private async Task PublishSelectedAsync()
    {
        if (Selected is not { } s || AnalysisPipeline.Load(s) is not { } analysis) return;
        var dialog = new PublishDialog(analysis, _ws.Settings.UploadVideoToGuardian && s.VideoPath is not null);
        var options = await dialog.ShowDialog<PublishOptions?>(this);
        if (options is null) return;
        _work = new CancellationTokenSource();
        Busy(true, "Publishing to Guardian…");
        try
        {
            var result = await Task.Run(() => _ws.PublishAsync(s, analysis, options, new Progress<string>(t => _progressText.Text = t), _work.Token));
            var parts = new List<string>();
            if (result.SkillName is { } skill) parts.Add($"skill \"{skill}\"");
            if (result.Automation is { } a) parts.Add($"automation \"{a.Name}\"");
            if (result.Video is { } v) parts.Add($"video intake ({v.Status})");
            _progressText.Text = parts.Count > 0 ? "In Guardian: " + string.Join(", ", parts) + "." : "Nothing was published.";
            if (result.Problems.Count > 0) _detailError.Text = string.Join("\n", result.Problems);
        }
        catch (Exception e)
        {
            _detailError.Text = "Publishing failed: " + e.Message;
        }
        finally
        {
            _work.Dispose();
            _work = null;
            var text = _progressText.Text;
            var error = _detailError.Text;
            Busy(false, text ?? "");
            RefreshSessions(s.Id);
            _progressText.Text = text;
            _detailError.Text = error;
        }
    }
}

/// <summary>What to send to Guardian.</summary>
internal sealed class PublishDialog : Window
{
    public PublishDialog(SessionAnalysis analysis, bool videoAvailable)
    {
        Title = "Publish to Guardian";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        var skill = new CheckBox { Content = "Save as a Guardian skill (the procedure, rules and pitfalls — used by Guardian chats and to train people)", IsChecked = true };
        var automation = new CheckBox { Content = "Create a Guardian automation (desktop worker runbook)", IsChecked = true };
        var video = new CheckBox { Content = "Upload the recording to Guardian's Video intake (Guardian drafts its own automation from the video too)", IsChecked = videoAvailable, IsEnabled = videoAvailable };
        var cron = new TextBox { Text = analysis.SuggestedCron ?? "", PlaceholderText = "empty = run on demand; e.g. 0 9 * * 1-5" };
        var ok = new Button { Content = "Publish", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => Close(new PublishOptions(skill.IsChecked == true, automation.IsChecked == true, video.IsChecked == true,
            string.IsNullOrWhiteSpace(cron.Text) ? null : cron.Text.Trim()));
        cancel.Click += (_, _) => Close(null);
        foreach (var c in new[] { skill, automation, video }) { c.HorizontalAlignment = HorizontalAlignment.Stretch; }
        Content = new Border
        {
            Padding = new Thickness(20),
            Child = MainWindow.Stack(12,
                MainWindow.Label($"\"{analysis.Title}\"", "h2"),
                skill, automation,
                MainWindow.Field("Schedule (cron, optional)", cron, "Leave empty to run it on demand from Guardian (review it there before its first run)."),
                video,
                MainWindow.Row(8, ok, cancel)),
        };
    }
}
