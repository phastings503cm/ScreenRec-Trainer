using ScreenRec.Core.Ai;
using ScreenRec.Core.Export;
using ScreenRec.Core.Guardian;
using ScreenRec.Core.Recording;
using ScreenRec.Core.Storage;

namespace ScreenRec.Core;

public sealed record PublishOptions(bool Skill = true, bool Automation = true, bool UploadVideo = true, string? Cron = null);

public sealed record PublishResult(string? SkillName, GuardianAutomation? Automation, GuardianVideoIntake? Video, IReadOnlyList<string> Problems);

/// <summary>The app's services in one place, shared by the desktop UI and the command line.</summary>
public sealed class Workspace
{
    public Workspace(AppSettings? settings = null, ISecretStore? secrets = null)
    {
        Settings = settings ?? AppSettings.Load();
        Secrets = secrets ?? SecretStores.CreateDefault();
        Accounts = new GuardianAccountStore(Secrets);
        if (Accounts.Load() is { } saved)
        {
            Guardian = new GuardianClient(saved.Account.Address, saved.Session);
            GuardianAccount = saved.Account;
        }
    }

    public AppSettings Settings { get; }
    public ISecretStore Secrets { get; }
    public GuardianAccountStore Accounts { get; }
    public GuardianClient? Guardian { get; private set; }
    public GuardianAccount? GuardianAccount { get; private set; }
    public bool IsSignedIn => Guardian?.IsSignedIn == true;

    public IReadOnlyList<RecordingSession> Sessions() => RecordingSession.List(AppPaths.Sessions);

    // MARK: Guardian sign-in

    public GuardianClient NewGuardianClient(string? address = null) => new(address ?? Settings.GuardianAddress);

    /// <summary>After a successful sign-in on <paramref name="client"/>: keep the token, look up the org name.</summary>
    public async Task<GuardianAccount> CompleteSignInAsync(GuardianClient client, GuardianSession session, string method, CancellationToken cancellationToken = default)
    {
        string? orgName = null;
        try
        {
            var me = await client.MeAsync(cancellationToken).ConfigureAwait(false);
            client.OrgId ??= me.OrgId;
            orgName = me.Orgs.FirstOrDefault(o => o.Id == client.OrgId)?.Name;
            session = session with { Name = session.Name ?? me.Name, Email = session.Email.Length > 0 ? session.Email : me.Email };
        }
        catch (GuardianError) { /* the sign-in stands; /me is informational */ }
        Guardian = client;
        Guardian.Restore(session with { OrgId = client.OrgId });
        GuardianAccount = Accounts.Save(client, session, method, orgName);
        Settings.GuardianAddress = client.Address;
        Settings.Save();
        return GuardianAccount;
    }

    public void SignOut()
    {
        Guardian?.SignOut();
        Guardian = null;
        GuardianAccount = null;
        Accounts.SignOut();
        if (Settings.Provider == AiProvider.Guardian) Settings.Provider = AiProvider.Spark;
    }

    // MARK: AI

    public async Task<IChatClient> CreateChatClientAsync(CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(30, Settings.RequestTimeoutSeconds));
        switch (Settings.Provider)
        {
            case AiProvider.Guardian:
                if (Guardian?.Session is not { } session) throw new AiError("Sign in to Guardian to use Guardian's models.");
                var model = Settings.GuardianModel;
                if (string.IsNullOrWhiteSpace(model))
                {
                    var models = await Guardian.ModelsAsync(cancellationToken).ConfigureAwait(false);
                    model = models.FirstOrDefault()?.Id ?? throw new AiError("Guardian offers no models to this account.");
                }
                var headers = Guardian.OrgId is { Length: > 0 } org ? new Dictionary<string, string> { ["x-guardian-org"] = org } : null;
                return new OpenAiChatClient(Guardian.ProxyBaseUrl, model, () => Guardian?.Session?.Token ?? session.Token, timeout, headers: headers, kind: "Guardian");
            case AiProvider.Spark:
                return new OpenAiChatClient(Settings.SparkBaseUrl, Settings.SparkModel, () => Secrets.Read(SecretNames.SparkApiKey), timeout, kind: "GLM Spark");
            default:
                return new OpenAiChatClient(Settings.OtherBaseUrl, Settings.OtherModel, () => Secrets.Read(SecretNames.OtherApiKey), timeout);
        }
    }

    public PipelineOptions PipelineOptions() => new()
    {
        ImagesPerRequest = Settings.ImagesPerRequest,
        ImageMaxEdge = Settings.ModelImageMaxEdge,
        MaxSteps = Settings.MaxSteps,
        ReasoningEffort = Settings.ReasoningEffort,
    };

    public async Task<SessionAnalysis> ProcessAsync(RecordingSession session, IProgress<PipelineProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = await CreateChatClientAsync(cancellationToken).ConfigureAwait(false);
        session.Manifest.Status = SessionStatus.Processing;
        session.Manifest.LastError = null;
        session.Save();
        try
        {
            var analysis = await new AnalysisPipeline(client, PipelineOptions()).RunAsync(session, progress, cancellationToken).ConfigureAwait(false);
            Exporters.WriteAll(analysis, session);
            session.Manifest.Status = SessionStatus.Processed;
            session.Save();
            return analysis;
        }
        catch (Exception e)
        {
            session.Manifest.Status = SessionStatus.Failed;
            session.Manifest.LastError = e is OperationCanceledException ? "Processing was cancelled." : e.Message;
            session.Save();
            throw;
        }
    }

    // MARK: Guardian publishing

    public async Task<PublishResult> PublishAsync(RecordingSession session, SessionAnalysis analysis, PublishOptions options,
                                                  IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var guardian = Guardian ?? throw new GuardianError(GuardianErrorKind.Unauthorized, "Sign in to Guardian first.");
        var problems = new List<string>();
        var expert = session.Manifest.Expert;
        string? skillName = null;
        GuardianAutomation? automation = null;
        GuardianVideoIntake? video = null;

        if (options.Skill)
        {
            progress?.Report("Saving the skill in Guardian…");
            try
            {
                skillName = Exporters.SkillName(analysis, expert);
                var skillMd = Exporters.SkillMarkdown(analysis, expert);
                await guardian.PutSkillAsync(new GuardianSkill(skillName, $"How {(expert.Length > 0 ? expert : "the expert")} does {analysis.Title}",
                    new Dictionary<string, string> { ["SKILL.md"] = skillMd }), cancellationToken).ConfigureAwait(false);
                session.Manifest.Guardian.SkillName = skillName;
                session.Manifest.Guardian.SkillPublishedAt = DateTimeOffset.Now;
            }
            catch (GuardianError e) { problems.Add("Skill: " + e.Message); skillName = null; }
        }

        if (options.Automation)
        {
            progress?.Report("Creating the automation in Guardian…");
            try
            {
                automation = await guardian.CreateAutomationAsync(analysis.Title, Description(analysis, expert), Exporters.AutomationPrompt(analysis, expert),
                    options.Cron, "DESKTOP", null, cancellationToken).ConfigureAwait(false);
                session.Manifest.Guardian.AutomationId = automation.Id;
                session.Manifest.Guardian.AutomationCreatedAt = DateTimeOffset.Now;
            }
            catch (GuardianError e) { problems.Add("Automation: " + e.Message); }
        }

        if (options.UploadVideo && session.VideoPath is { } videoPath && File.Exists(videoPath))
        {
            try
            {
                var path = videoPath;
                var type = "video/x-msvideo";
                if (VideoConverter.FindFfmpeg(Settings.FfmpegPath) is { } ffmpeg)
                {
                    progress?.Report("Converting the recording to MP4…");
                    var mp4 = Path.ChangeExtension(videoPath, ".mp4");
                    if (File.Exists(mp4) || await VideoConverter.ToMp4Async(ffmpeg, videoPath, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        path = mp4;
                        type = "video/mp4";
                    }
                }
                progress?.Report("Uploading the recording to Guardian…");
                var upload = await guardian.UploadFileAsync(path, type, new Progress<double>(p => progress?.Report($"Uploading the recording… {p:P0}")), cancellationToken).ConfigureAwait(false);
                session.Manifest.Guardian.AttachmentId = upload.AttachmentId;
                progress?.Report("Starting Guardian's video intake…");
                video = await guardian.StartVideoIntakeAsync(upload.AttachmentId, analysis.Title, "WORKFLOW", cancellationToken).ConfigureAwait(false);
                session.Manifest.Guardian.VideoIntakeId = video.Id;
                session.Manifest.Guardian.VideoIntakeStatus = video.Status;
            }
            catch (GuardianError e) { problems.Add("Video intake: " + e.Message); }
        }

        try
        {
            await guardian.PushNoteAsync($"ScreenRec Trainer published \"{analysis.Title}\" from a screen recording{(expert.Length > 0 ? $" of {expert}" : "")}.",
                problems.Count == 0 ? "completed" : "completed with problems: " + string.Join("; ", problems), cancellationToken).ConfigureAwait(false);
        }
        catch (GuardianError) { /* the notebook is best effort */ }

        if (skillName is not null || automation is not null || video is not null) session.Manifest.Status = SessionStatus.Published;
        session.Save();
        return new PublishResult(skillName, automation, video, problems);
    }

    /// <summary>Combine every processed recording of an expert into one Guardian skill ("be an expert at her stuff").</summary>
    public async Task<(ExpertProfile Profile, string? SkillName, string? Problem)> BuildExpertAsync(string expert, bool publish, CancellationToken cancellationToken = default)
    {
        var analyses = Sessions()
            .Where(s => string.Equals(s.Manifest.Expert, expert, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(expert))
            .Select(AnalysisPipeline.Load).OfType<SessionAnalysis>().ToList();
        var client = await CreateChatClientAsync(cancellationToken).ConfigureAwait(false);
        var profile = await new AnalysisPipeline(client, PipelineOptions()).BuildExpertAsync(expert, analyses, cancellationToken).ConfigureAwait(false);
        var files = Exporters.ExpertSkillFiles(profile, analyses);
        var dir = Path.Combine(AppPaths.Experts, Exporters.Slug(expert));
        foreach (var (rel, text) in files)
        {
            var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        File.WriteAllText(Path.Combine(dir, "profile.json"), System.Text.Json.JsonSerializer.Serialize(profile, AnalysisPipeline.Json));
        if (!publish) return (profile, null, null);
        if (Guardian is null) return (profile, null, "Not signed in to Guardian.");
        var name = Exporters.Slug(expert + " expert", 60);
        try
        {
            await Guardian.PutSkillAsync(new GuardianSkill(name, $"Work the way {expert} does", files), cancellationToken).ConfigureAwait(false);
            return (profile, name, null);
        }
        catch (GuardianError e)
        {
            return (profile, null, e.Message);
        }
    }

    private static string Description(SessionAnalysis a, string expert) =>
        $"{a.Summary}{(expert.Length > 0 ? $" Learned from {expert}'s screen recording." : "")} Automatability {a.Automatability:P0}.".Trim();
}
