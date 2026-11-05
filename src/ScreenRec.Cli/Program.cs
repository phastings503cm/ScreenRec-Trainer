using ScreenRec.Core;
using ScreenRec.Core.Ai;
using ScreenRec.Core.Export;
using ScreenRec.Core.Guardian;
using ScreenRec.Core.Recording;
using ScreenRec.Core.Storage;

// screenrec — the same engine as the desktop app, for scripts, testing and remote use.

var argv = args.ToList();
if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        screenrec — record a screen session, turn it into training + a Guardian automation

          screenrec record [--seconds N] [--title T] [--about TEXT] [--expert NAME] [--fps F] [--synthetic]
          screenrec demo              (a made-up expense-approval recording, to try the pipeline)
          screenrec check-input       (prove clicks/keys are being seen)
          screenrec list
          screenrec process <session-id|latest> [--provider spark|guardian|openai] [--url URL] [--model M]
          screenrec publish <session-id|latest> [--no-skill] [--no-automation] [--no-video] [--cron "0 9 * * 1-5"]
          screenrec expert <name> [--publish]
          screenrec login [--email E] [--browser] [--address https://guardian.503cm.com]
          screenrec logout | whoami | models
          screenrec test-ai           (one request to the configured model server)
          screenrec settings          (print the settings file path and values)
        Data: SCREENREC_HOME, else the app data folder.
        """);
    return 0;
}

string? Opt(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return null;
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}
bool Flag(string name) => argv.Remove(name);

var ws = new Workspace();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

RecordingSession Find(string? which)
{
    var sessions = ws.Sessions();
    if (sessions.Count == 0) throw new InvalidOperationException("No recordings yet. Run: screenrec record");
    if (which is null or "latest") return sessions[0];
    return sessions.FirstOrDefault(s => s.Id == which || s.Id.StartsWith(which, StringComparison.Ordinal))
           ?? throw new InvalidOperationException($"No recording '{which}'. Run: screenrec list");
}

void ApplyAiOptions()
{
    if (Opt("--provider") is { } p)
        ws.Settings.Provider = p.ToLowerInvariant() switch { "guardian" => AiProvider.Guardian, "openai" or "other" => AiProvider.OpenAiCompatible, _ => AiProvider.Spark };
    var url = Opt("--url");
    var model = Opt("--model");
    if (ws.Settings.Provider == AiProvider.Spark) { if (url is not null) ws.Settings.SparkBaseUrl = url; if (model is not null) ws.Settings.SparkModel = model; }
    else if (ws.Settings.Provider == AiProvider.OpenAiCompatible) { if (url is not null) ws.Settings.OtherBaseUrl = url; if (model is not null) ws.Settings.OtherModel = model; }
    else if (model is not null) ws.Settings.GuardianModel = model;
}

try
{
    switch (argv[0])
    {
        case "record":
        {
            var seconds = double.TryParse(Opt("--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
            var synthetic = Flag("--synthetic");
            var fps = double.TryParse(Opt("--fps"), System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : ws.Settings.FramesPerSecond;
            var options = new RecorderOptions
            {
                FramesPerSecond = fps, MaxWidth = ws.Settings.FrameMaxWidth, JpegQuality = ws.Settings.JpegQuality, DisplayIndex = ws.Settings.DisplayIndex,
                CaptureTypedText = ws.Settings.CaptureTypedText, Title = Opt("--title") ?? "", Description = Opt("--about") ?? "",
                Expert = Opt("--expert") ?? ws.Settings.ExpertName,
            };
            using IScreenCapture capture = synthetic ? new SyntheticScreenCapture() : PlatformRecording.CreateCapture();
            using IInputMonitor input = synthetic ? new NullInputMonitor() : PlatformRecording.CreateInputMonitor();
            await using var recorder = new Recorder(capture, input, options, AppPaths.Sessions);
            var session = recorder.Start();
            Console.WriteLine($"Recording {session.Id} with {capture.Name} — {(seconds > 0 ? $"{seconds:0} s" : "press Ctrl+C or Enter to stop")}.");
            if (recorder.Status.Problem is { } problem) Console.WriteLine("  ! " + problem);
            var stop = seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token) : Task.Run(() => Console.ReadLine(), cts.Token);
            try { await stop; } catch (OperationCanceledException) { }
            session = await recorder.StopAsync();
            var m = session.Manifest;
            Console.WriteLine($"Saved {m.FrameCount} stills, {m.EventCount} events, {m.DurationSeconds:0.0} s → {session.Folder}");
            foreach (var w in m.Warnings) Console.WriteLine("  ! " + w);
            return m.Status == SessionStatus.Failed ? 1 : 0;
        }
        case "check-input":
        {
            // Prints mouse/keyboard events as they are seen: proves the permissions are right before a real recording.
            var seconds = double.TryParse(Opt("--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var cs) ? cs : 10;
            using var monitor = PlatformRecording.CreateInputMonitor();
            var count = 0;
            var ok = monitor.Start(i => { Interlocked.Increment(ref count); Console.WriteLine($"  {i.Kind} {i.Button} ({i.X:0},{i.Y:0})"); });
            Console.WriteLine(ok ? $"Watching input with {monitor.Name} for {seconds:0} s — click and type…" : "Can't watch input: " + monitor.Problem);
            if (!ok) return 1;
            Console.WriteLine($"Foreground: {monitor.Foreground()}");
            await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ContinueWith(_ => { });
            monitor.Stop();
            Console.WriteLine($"{count} input events seen.");
            return count > 0 ? 0 : 1;
        }
        case "demo":
        {
            var demo = DemoSession.Create(AppPaths.Sessions, Opt("--expert") ?? "Maria");
            Console.WriteLine($"Created demo recording {demo.Id} (expense approvals, {demo.Manifest.FrameCount} frames). Next: screenrec process {demo.Id}");
            return 0;
        }
        case "list":
            foreach (var s in ws.Sessions())
                Console.WriteLine($"{s.Id}  {s.Manifest.Status,-10} {s.Manifest.DurationSeconds,7:0.0}s {s.Manifest.FrameCount,5} frames  {s.Manifest.Title}");
            return 0;
        case "process":
        {
            ApplyAiOptions();
            var session = Find(argv.ElementAtOrDefault(1));
            Console.WriteLine($"Processing {session.Id} with {(await ws.CreateChatClientAsync(cts.Token)).Describe}");
            var analysis = await ws.ProcessAsync(session, new Progress<PipelineProgress>(p => Console.WriteLine($"  [{p.Stage}] {p.Message}")), cts.Token);
            Console.WriteLine($"\n{analysis.Title}\n{analysis.Summary}\n");
            foreach (var p in analysis.Procedure) Console.WriteLine($"  {p.Number}. {p.Title}");
            Console.WriteLine($"\nWrote {Path.Combine(session.AnalysisFolder, "training.html")} and skill/SKILL.md, automation.txt");
            return 0;
        }
        case "publish":
        {
            var session = Find(argv.ElementAtOrDefault(1));
            var analysis = AnalysisPipeline.Load(session) ?? throw new InvalidOperationException("Process this recording first: screenrec process " + session.Id);
            if (!ws.IsSignedIn) throw new InvalidOperationException("Sign in first: screenrec login");
            var result = await ws.PublishAsync(session, analysis,
                new PublishOptions(!Flag("--no-skill"), !Flag("--no-automation"), !Flag("--no-video"), Opt("--cron") ?? analysis.SuggestedCron),
                new Progress<string>(Console.WriteLine), cts.Token);
            if (result.SkillName is { } skill) Console.WriteLine($"Skill: {skill}");
            if (result.Automation is { } a) Console.WriteLine($"Automation: {ws.Guardian!.AutomationUrl(a.Id)}");
            if (result.Video is { } v) Console.WriteLine($"Video intake: {ws.Guardian!.VideoUrl(v.Id)} ({v.Status})");
            foreach (var p in result.Problems) Console.WriteLine("  ! " + p);
            return result.Problems.Count > 0 && result.SkillName is null && result.Automation is null ? 1 : 0;
        }
        case "expert":
        {
            var publish = Flag("--publish");
            var name = argv.ElementAtOrDefault(1) ?? ws.Settings.ExpertName;
            var (profile, skill, problem) = await ws.BuildExpertAsync(name, publish, cts.Token);
            Console.WriteLine($"{profile.Name}: {profile.Role}\n{profile.Overview}\nTasks: {string.Join(", ", profile.Tasks.Select(t => t.Name))}");
            Console.WriteLine($"Saved under {Path.Combine(AppPaths.Experts, Exporters.Slug(name))}");
            if (skill is not null) Console.WriteLine($"Guardian skill: {skill}");
            if (problem is not null) Console.WriteLine("  ! " + problem);
            return 0;
        }
        case "login":
        {
            var client = ws.NewGuardianClient(Opt("--address"));
            GuardianSession session;
            string method;
            if (Flag("--browser"))
            {
                Console.WriteLine("Opening your browser to sign in to Guardian…");
                session = await GuardianBrowserSignIn.SignInAsync(client, url => { Console.WriteLine(url); GuardianBrowserSignIn.OpenInBrowser(url); }, cancellationToken: cts.Token);
                method = "browser";
            }
            else
            {
                var email = Opt("--email") ?? Prompt("Email: ");
                var password = Environment.GetEnvironmentVariable("GUARDIAN_PASSWORD") ?? ReadSecret("Password: ");
                try
                {
                    session = await client.SignInAsync(email, password, cts.Token);
                }
                catch (GuardianError e) when (e.Kind == GuardianErrorKind.TwoFactorRequired)
                {
                    session = await client.VerifyTwoFactorAsync(e.Challenge!, Prompt("6-digit code: "), email, cts.Token);
                }
                method = "password";
            }
            var account = await ws.CompleteSignInAsync(client, session, method, cts.Token);
            Console.WriteLine($"Signed in to {account.Address} as {account.Label}{(account.OrgName is { } o ? $" · {o}" : "")}");
            return 0;
        }
        case "logout":
            ws.SignOut();
            ws.Settings.Save();
            Console.WriteLine("Signed out.");
            return 0;
        case "whoami":
        {
            if (!ws.IsSignedIn) { Console.WriteLine("Not signed in."); return 1; }
            var me = await ws.Guardian!.MeAsync(cts.Token);
            Console.WriteLine($"{me.Name} <{me.Email}> org {me.OrgId}; modules: {string.Join(", ", me.Modules)}");
            return 0;
        }
        case "models":
        {
            if (!ws.IsSignedIn) { Console.WriteLine("Not signed in."); return 1; }
            foreach (var m in await ws.Guardian!.ModelsAsync(cts.Token)) Console.WriteLine($"{m.Id}  {m.Label}");
            return 0;
        }
        case "test-ai":
        {
            ApplyAiOptions();
            var client = await ws.CreateChatClientAsync(cts.Token);
            Console.WriteLine("Asking " + client.Describe);
            var r = await client.CompleteAsync([ChatMessage.User("Reply with exactly: ready")], new ChatOptions { MaxTokens = 64, ReasoningEffort = ws.Settings.ReasoningEffort }, cts.Token);
            Console.WriteLine($"{r.Model}: {r.Content}");
            return 0;
        }
        case "settings":
            Console.WriteLine(AppPaths.SettingsFile);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ws.Settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        default:
            Console.Error.WriteLine($"Unknown command '{argv[0]}'. Try: screenrec help");
            return 2;
    }
}
catch (Exception e) when (e is AiError or GuardianError or InvalidOperationException or IOException or OperationCanceledException or PlatformNotSupportedException)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}

static string Prompt(string label)
{
    Console.Write(label);
    return Console.ReadLine()?.Trim() ?? "";
}

static string ReadSecret(string label)
{
    Console.Write(label);
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
        chars.Add(key.KeyChar);
    }
    Console.WriteLine();
    return new string(chars.ToArray());
}
