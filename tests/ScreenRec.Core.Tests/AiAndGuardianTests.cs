using System.Net;
using System.Text.Json.Nodes;
using ScreenRec.Core.Ai;
using ScreenRec.Core.Export;
using ScreenRec.Core.Guardian;
using ScreenRec.Core.Recording;
using ScreenRec.Core.Storage;

namespace ScreenRec.Core.Tests;

public class AiTests
{
    [Theory]
    [InlineData("spark:8888", "http://spark:8888/v1")]
    [InlineData("http://10.0.0.5:8888/v1/", "http://10.0.0.5:8888/v1")]
    [InlineData("https://spark:11443/v1/chat/completions", "https://spark:11443/v1")]
    [InlineData("https://guardian.503cm.com/api/harness/v1", "https://guardian.503cm.com/api/harness/v1")]
    public void Base_urls_normalize(string typed, string expected) => Assert.Equal(expected, OpenAiChatClient.NormalizeBaseUrl(typed));

    [Fact]
    public async Task Spark_request_uses_glm_template_kwargs_images_and_key()
    {
        var http = new FakeHttp((_, _) => FakeHttp.Json("""{"model":"GLM-5.3-Flash-EXL3","choices":[{"message":{"content":"<think>hm</think>ready","reasoning_content":"x"}}],"usage":{"prompt_tokens":5,"completion_tokens":2}}"""));
        var client = new OpenAiChatClient("http://spark:8888/v1", "GLM-5.3-Flash-EXL3", () => "k123", handler: http, kind: "GLM Spark");
        var r = await client.CompleteAsync([ChatMessage.User(new TextPart("look"), new ImagePart([1, 2, 3]))], new ChatOptions { ReasoningEffort = "low" });
        Assert.Equal("ready", r.Content);
        Assert.Equal(5, r.PromptTokens);
        var (_, url, body, headers) = Assert.Single(http.Requests);
        Assert.Equal("http://spark:8888/v1/chat/completions", url);
        Assert.Equal("Bearer k123", headers["authorization"]);
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("GLM-5.3-Flash-EXL3", (string?)json["model"]);
        Assert.Equal("low", (string?)json["chat_template_kwargs"]!["reasoning_effort"]);
        Assert.Null(json["reasoning_effort"]); // vLLM on the Spark takes it in the template only
        Assert.StartsWith("data:image/jpeg;base64,", (string?)json["messages"]![0]!["content"]![1]!["image_url"]!["url"]);
    }

    [Fact]
    public async Task Refused_optional_setting_is_dropped_and_retried()
    {
        var n = 0;
        var http = new FakeHttp((_, body) => n++ == 0
            ? FakeHttp.Json("""{"error":{"message":"response_format is not supported"}}""", HttpStatusCode.BadRequest)
            : FakeHttp.Json("""{"choices":[{"message":{"content":"{}"}}]}"""));
        var client = new OpenAiChatClient("http://x/v1", "m", () => null, handler: http);
        await client.CompleteAsync([ChatMessage.User("hi")], new ChatOptions { JsonObject = true });
        Assert.Equal(2, http.Requests.Count);
        Assert.Contains("response_format", http.Requests[0].Body);
        Assert.DoesNotContain("response_format", http.Requests[1].Body);
    }

    [Fact]
    public async Task Missing_model_is_read_from_models_and_errors_are_readable()
    {
        var http = new FakeHttp((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/models")
            ? FakeHttp.Json("""{"data":[{"id":"qwen3-vl"}]}""")
            : FakeHttp.Json("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized));
        var client = new OpenAiChatClient("http://x/v1", "", () => null, handler: http);
        var e = await Assert.ThrowsAsync<AiError>(() => client.CompleteAsync([ChatMessage.User("hi")], new ChatOptions()));
        Assert.Equal("qwen3-vl", client.Model);
        Assert.Equal(401, e.Status);
        Assert.Contains("bad key", e.Message);
    }

    [Theory]
    [InlineData("```json\n{\"a\":1}\n```")]
    [InlineData("Sure! {\"a\":1,} hope that helps")]
    [InlineData("<think>{\"no\":0}</think>{\"a\":1}")]
    public void Json_is_extracted_from_loose_answers(string answer) => Assert.Equal(1, (int)JsonExtract.Object(answer)["a"]!);

    [Fact]
    public async Task Pipeline_describes_every_step_and_synthesizes_and_resumes()
    {
        using var dir = new TempDir();
        var session = SampleSessions.Create(dir.Path);
        var model = new FakeModel();
        var pipeline = new AnalysisPipeline(model, new PipelineOptions { ImagesPerRequest = 4, MaxParallel = 1 });
        var analysis = await pipeline.RunAsync(session);

        Assert.Equal("Reconcile vendor invoices", analysis.Title);
        Assert.Equal(2, analysis.Procedure.Count);
        Assert.Equal([1], analysis.Procedure[0].SourceSteps);
        Assert.Single(analysis.Decisions);
        Assert.Equal("0 9 1 * *", analysis.SuggestedCron);
        Assert.Equal(0.7, analysis.Automatability);
        Assert.Equal(6, analysis.Steps.Count);
        Assert.Equal("Click Button 1", analysis.Steps[0].Action);
        Assert.Equal("If the total is negative, stop", analysis.Steps[0].Decision);
        Assert.Equal("check totals", analysis.Steps[2].Note);
        Assert.Equal(3 + 1, model.Calls.Count); // 6 steps in batches of 2, then synthesis
        Assert.Contains(model.Calls[0][1].Parts, p => p is ImagePart);
        Assert.NotNull(AnalysisPipeline.Load(session));

        // A rerun uses the cached step descriptions: only synthesis is asked again.
        var again = new FakeModel();
        await new AnalysisPipeline(again, new PipelineOptions()).RunAsync(session);
        Assert.Single(again.Calls);

        Exporters.WriteAll(analysis, session);
        var skill = File.ReadAllText(Path.Combine(session.AnalysisFolder, "skill", "SKILL.md"));
        Assert.StartsWith("---\nname: maria-reconcile-vendor-invoices\ndescription: \"How Maria does", skill.Replace("\r", ""));
        Assert.Contains("When the total is negative: stop and ask", skill);
        var html = File.ReadAllText(Path.Combine(session.AnalysisFolder, "training.html"));
        Assert.Contains("data:image/jpeg;base64,", html);
        Assert.Contains("<h3>1. Open the ledger</h3>", html);
        var runbook = File.ReadAllText(Path.Combine(session.AnalysisFolder, "automation.txt"));
        Assert.Contains("{{month}}", runbook);
        Assert.Contains("stop and report", runbook);
    }

    [Fact]
    public async Task Expert_profile_combines_sessions()
    {
        using var dir = new TempDir();
        var session = SampleSessions.Create(dir.Path);
        var analysis = await new AnalysisPipeline(new FakeModel(), new PipelineOptions()).RunAsync(session);
        var profile = await new AnalysisPipeline(new FakeModel(), new PipelineOptions()).BuildExpertAsync("Maria", [analysis]);
        Assert.Equal("AP specialist", profile.Role);
        var files = Exporters.ExpertSkillFiles(profile, [analysis]);
        Assert.Contains("name: maria-expert", files["SKILL.md"]);
        Assert.Contains("procedures/reconcile-vendor-invoices.md", files.Keys);
    }
}

public class GuardianTests
{
    private static (GuardianClient Client, FakeHttp Http) Fake(Func<HttpRequestMessage, string?, HttpResponseMessage> respond, bool signedIn = true)
    {
        var http = new FakeHttp(respond);
        var session = signedIn ? new GuardianSession("u1", "maria@503cm.com", "Maria", "tok", DateTimeOffset.Now, "org1") : null;
        return (new GuardianClient("guardian.503cm.com", session, http), http);
    }

    [Fact]
    public void Addresses_normalize()
    {
        Assert.Equal("https://guardian.503cm.com", GuardianClient.NormalizeAddress("guardian.503cm.com/"));
        Assert.Equal("https://guardian.503cm.com", GuardianClient.NormalizeAddress("https://guardian.503cm.com/api/harness/v1"));
        Assert.Equal("http://localhost:3000", GuardianClient.NormalizeAddress("http://localhost:3000"));
        Assert.Null(GuardianClient.NormalizeAddress("http://evil.example.com"));
    }

    [Fact]
    public async Task Password_sign_in_with_two_factor()
    {
        var (client, http) = Fake((req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/api/auth/login" => FakeHttp.Json("""{"twoFactorRequired":true,"challenge":"ch1"}"""),
            "/api/auth/login/2fa" => FakeHttp.Json("""{"token":"gdn_abc","user":{"id":"u9","email":"maria@503cm.com","name":"Maria"}}"""),
            _ => FakeHttp.Json("{}", HttpStatusCode.NotFound),
        }, signedIn: false);
        var e = await Assert.ThrowsAsync<GuardianError>(() => client.SignInAsync("maria@503cm.com", "pw"));
        Assert.Equal(GuardianErrorKind.TwoFactorRequired, e.Kind);
        var session = await client.VerifyTwoFactorAsync(e.Challenge!, "123 456", "maria@503cm.com");
        Assert.Equal("gdn_abc", session.Token);
        Assert.Equal("u9", session.UserId);
        var login = JsonNode.Parse(http.Requests[0].Body!)!;
        Assert.Equal("pw", (string?)login["password"]);
        Assert.StartsWith("vision-", (string?)login["client"]);
        Assert.Equal("123456", (string?)JsonNode.Parse(http.Requests[1].Body!)!["code"]);
    }

    [Fact]
    public async Task Wrong_password_is_a_credentials_error()
    {
        var (client, _) = Fake((_, _) => FakeHttp.Json("""{"message":"invalid credentials","statusCode":401}""", HttpStatusCode.Unauthorized), signedIn: false);
        var e = await Assert.ThrowsAsync<GuardianError>(() => client.SignInAsync("a@b.c", "x"));
        Assert.Equal(GuardianErrorKind.Credentials, e.Kind);
        Assert.Contains("browser", e.Message);
    }

    [Fact]
    public async Task Browser_sign_in_round_trip_with_pkce()
    {
        string? verifier = null;
        var (client, _) = Fake((req, body) =>
        {
            verifier = (string?)JsonNode.Parse(body!)!["code_verifier"];
            return FakeHttp.Json("""{"token":"gdn_browser","orgId":"org7","user":{"id":"u1","email":"m@503cm.com"}}""");
        }, signedIn: false);
        string? opened = null;
        var task = GuardianBrowserSignIn.SignInAsync(client, url =>
        {
            opened = url;
            var q = LoopbackReceiver.ParseQuery(new Uri(url).PathAndQuery);
            Assert.Equal("S256", q["code_challenge_method"]);
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await http.GetStringAsync($"{q["redirect_uri"]}?code=c0de&state={Uri.EscapeDataString(q["state"])}");
            });
        }, TimeSpan.FromSeconds(20));
        var session = await task;
        Assert.Equal("gdn_browser", session.Token);
        Assert.Equal("org7", client.OrgId);
        Assert.StartsWith("https://guardian.503cm.com/app/connect/vision?redirect_uri=http%3A%2F%2F%5B%3A%3A1%5D%3A", opened);
        var challenge = LoopbackReceiver.ParseQuery(new Uri(opened!).PathAndQuery)["code_challenge"];
        Assert.Equal(GuardianPkce.ChallengeFor(verifier!), challenge);
    }

    [Fact]
    public async Task Skill_automation_and_video_intake_calls()
    {
        using var dir = new TempDir();
        var video = Path.Combine(dir.Path, "r.avi");
        File.WriteAllBytes(video, new byte[1000]);
        var (client, http) = Fake((req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/api/harness/skills" => FakeHttp.Json("""{"ok":true}"""),
            "/api/automations" => FakeHttp.Json("""{"id":"auto1","name":"Reconcile","mode":"DESKTOP"}"""),
            "/api/uploads/init" => FakeHttp.Json("""{"attachmentId":"att1","uploadUrl":"https://s3.example.com/put?sig=1","headers":{"x-amz-acl":"private"}}"""),
            "/put" => new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e1\"") } },
            "/api/uploads/att1/finalize" => FakeHttp.Json("{}"),
            "/api/videos" => FakeHttp.Json("""{"id":"vid1","status":"QUEUED"}"""),
            _ => FakeHttp.Json("""{"message":"nope"}""", HttpStatusCode.NotFound),
        });
        await client.PutSkillAsync(new GuardianSkill("maria-x", "How Maria does X", new Dictionary<string, string> { ["SKILL.md"] = "---\nname: maria-x\n---" }));
        var a = await client.CreateAutomationAsync("Reconcile", "desc", "1. do it", "0 9 * * 1");
        var up = await client.UploadFileAsync(video, "video/x-msvideo");
        var intake = await client.StartVideoIntakeAsync(up.AttachmentId, "Reconcile");

        Assert.Equal("auto1", a.Id);
        Assert.Equal("vid1", intake.Id);
        var skillReq = http.Requests.Single(r => r.Url.EndsWith("/api/harness/skills"));
        Assert.Equal("Bearer tok", skillReq.Headers["authorization"]);
        Assert.Equal("org1", skillReq.Headers["x-guardian-org"]);
        var auto = JsonNode.Parse(http.Requests.Single(r => r.Url.EndsWith("/api/automations")).Body!)!;
        Assert.Equal("DESKTOP", (string?)auto["mode"]);
        Assert.Equal("0 9 * * 1", (string?)auto["cron"]);
        Assert.False(string.IsNullOrEmpty((string?)auto["timezone"]));
        var put = http.Requests.Single(r => r.Url.StartsWith("https://s3.example.com"));
        Assert.False(put.Headers.ContainsKey("authorization")); // never send the Guardian token to storage
        Assert.Equal("private", put.Headers["x-amz-acl"]);
        var vid = JsonNode.Parse(http.Requests.Single(r => r.Url.EndsWith("/api/videos")).Body!)!;
        Assert.Equal("att1", (string?)vid["attachmentId"]);
        Assert.Equal("WORKFLOW", (string?)vid["kind"]);
        Assert.True((bool)vid["options"]!["autoEmitAutomation"]!);
    }

    [Fact]
    public async Task Big_uploads_go_multipart()
    {
        using var dir = new TempDir();
        var video = Path.Combine(dir.Path, "big.avi");
        using (var f = File.Create(video)) f.SetLength(GuardianClient.SinglePutLimit + 10);
        var (client, http) = Fake((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/api/uploads/multipart/start" => FakeHttp.Json("""{"attachmentId":"m1","partSize":33554432,"totalParts":2}"""),
            "/api/uploads/multipart/m1/part-url" => FakeHttp.Json($$"""{"url":"https://s3.example.com/part{{req.RequestUri.Query}}"}"""),
            "/part" => new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"p\"") } },
            "/api/uploads/multipart/m1/complete" => FakeHttp.Json("{}"),
            _ => FakeHttp.Json("{}", HttpStatusCode.NotFound),
        });
        var up = await client.UploadFileAsync(video, "video/x-msvideo");
        Assert.Equal("m1", up.AttachmentId);
        var complete = JsonNode.Parse(http.Requests.Single(r => r.Url.EndsWith("/complete")).Body!)!;
        Assert.Equal(2, complete["parts"]!.AsArray().Count);
        Assert.Equal("\"p\"", (string?)complete["parts"]![0]!["etag"]);
    }

    [Fact]
    public async Task Refusals_read_as_sentences()
    {
        var (client, _) = Fake((_, _) => FakeHttp.Json("""{"message":"automations module not enabled"}""", HttpStatusCode.Forbidden));
        var e = await Assert.ThrowsAsync<GuardianError>(() => client.CreateAutomationAsync("a", "b", "c"));
        Assert.Equal(GuardianErrorKind.Forbidden, e.Kind);
        Assert.Contains("automations module not enabled", e.Message);
    }

    [Fact]
    public async Task Workspace_publishes_everything_and_keeps_token_in_secret_store()
    {
        using var dir = new TempDir();
        AppPaths.UseRoot(dir.Path);
        try
        {
            var secrets = new MemorySecretStore();
            var ws = new Workspace(new AppSettings(), secrets);
            var http = new FakeHttp((req, _) => req.RequestUri!.AbsolutePath switch
            {
                "/api/auth/login" => FakeHttp.Json("""{"token":"gdn_t","user":{"id":"u1","email":"maria@503cm.com","name":"Maria"}}"""),
                "/api/harness/me" => FakeHttp.Json("""{"user":{"id":"u1"},"orgs":[{"id":"o1","name":"503CM","active":true}],"orgId":"o1","modules":["automations"]}"""),
                "/api/harness/skills" or "/api/harness/prompts" => FakeHttp.Json("{}"),
                "/api/automations" => FakeHttp.Json("""{"id":"a1"}"""),
                "/api/uploads/init" => FakeHttp.Json("""{"attachmentId":"att","uploadUrl":"https://s3.example.com/x"}"""),
                "/x" => new HttpResponseMessage(HttpStatusCode.OK),
                "/api/uploads/att/finalize" => FakeHttp.Json("{}"),
                "/api/videos" => FakeHttp.Json("""{"id":"v1","status":"QUEUED"}"""),
                _ => FakeHttp.Json("{}", HttpStatusCode.NotFound),
            });
            var client = new GuardianClient(GuardianClient.DefaultAddress, null, http);
            var session = await client.SignInAsync("maria@503cm.com", "pw");
            var account = await ws.CompleteSignInAsync(client, session, "password");
            Assert.Equal("503CM", account.OrgName);
            Assert.Equal("gdn_t", secrets.Read(SecretNames.GuardianToken));
            Assert.DoesNotContain("gdn_t", File.ReadAllText(AppPaths.GuardianFile));

            var rec = SampleSessions.Create(AppPaths.Sessions);
            var analysis = await new AnalysisPipeline(new FakeModel(), new PipelineOptions()).RunAsync(rec);
            ws.Settings.FfmpegPath = "/nonexistent/ffmpeg";
            var result = await ws.PublishAsync(rec, analysis, new PublishOptions(Cron: analysis.SuggestedCron));
            Assert.Empty(result.Problems);
            Assert.Equal("maria-reconcile-vendor-invoices", result.SkillName);
            Assert.Equal("a1", result.Automation!.Id);
            Assert.Equal("v1", result.Video!.Id);
            var reopened = RecordingSession.Open(rec.Folder)!;
            Assert.Equal(SessionStatus.Published, reopened.Manifest.Status);
            Assert.Equal("a1", reopened.Manifest.Guardian.AutomationId);
            var prompt = (string?)JsonNode.Parse(http.Requests.Single(r => r.Url.EndsWith("/api/automations")).Body!)!["prompt"];
            Assert.Contains("done the way Maria does it", prompt);

            // Relaunch: the saved sign-in is restored.
            var again = new Workspace(new AppSettings(), secrets);
            Assert.True(again.IsSignedIn);
            again.SignOut();
            Assert.Null(secrets.Read(SecretNames.GuardianToken));
        }
        finally
        {
            AppPaths.UseRoot(null);
        }
    }
}
