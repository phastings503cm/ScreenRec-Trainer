using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenRec.Core.Guardian;

public enum GuardianErrorKind { Network, Credentials, TwoFactorRequired, Unauthorized, Forbidden, Limit, NotFound, Conflict, Server, BadAddress, BrowserSignIn }

public sealed class GuardianError(GuardianErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public GuardianErrorKind Kind { get; } = kind;
    public int? Status { get; init; }
    public string? Code { get; init; }
    /// <summary>The 2FA challenge to finish with <see cref="GuardianClient.VerifyTwoFactorAsync"/>.</summary>
    public string? Challenge { get; init; }

    internal static GuardianError FromResponse(int status, string body)
    {
        string? message = null, code = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject json)
            {
                message = json["message"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonArray a => string.Join("; ", a.Select(x => x?.ToString())),
                    _ => null,
                } ?? (json["error"] as JsonObject)?["message"]?.ToString();
                code = json["code"]?.ToString() ?? (json["error"] is JsonValue ev ? ev.ToString() : (json["error"] as JsonObject)?["code"]?.ToString());
            }
        }
        catch (JsonException) { }
        var kind = status switch
        {
            401 => GuardianErrorKind.Unauthorized,
            403 => GuardianErrorKind.Forbidden,
            402 or 429 => GuardianErrorKind.Limit,
            404 => GuardianErrorKind.NotFound,
            409 => GuardianErrorKind.Conflict,
            _ => GuardianErrorKind.Server,
        };
        var text = kind switch
        {
            GuardianErrorKind.Unauthorized => message is { Length: > 0 } && message != "invalid session" && message != "missing session"
                ? $"Guardian: {message}" : "Your Guardian sign-in has ended. Sign in again.",
            GuardianErrorKind.Forbidden => $"Guardian says your role doesn't allow this{(message is { Length: > 0 } ? $": {message}" : "")}. Ask an org admin for access.",
            GuardianErrorKind.Limit => $"Guardian limit reached{(message is { Length: > 0 } ? $": {message}" : "")}.",
            GuardianErrorKind.NotFound => $"Guardian doesn't have that here (HTTP 404){(message is { Length: > 0 } ? $": {message}" : "")}.",
            _ => $"Guardian answered HTTP {status}{(message is { Length: > 0 } ? $": {message}" : "")}.",
        };
        return new GuardianError(kind, text) { Status = status, Code = code };
    }
}

public sealed record GuardianSession(string UserId, string Email, string? Name, string Token, DateTimeOffset SignedInAt, string? OrgId = null)
{
    public override string ToString() => $"GuardianSession {{ {Email} }}";
}

public sealed record GuardianOrg(string Id, string Name, bool Active);
public sealed record GuardianIdentity(string Id, string Email, string? Name, IReadOnlyList<GuardianOrg> Orgs, string? OrgId, IReadOnlyList<string> Modules, IReadOnlyList<string> Permissions);
public sealed record GuardianModel(string Id, string? Label = null, int? ContextWindow = null);
public sealed record GuardianSkill(string Name, string Description, IReadOnlyDictionary<string, string> Files);
public sealed record GuardianUpload(string AttachmentId, string Name, long Size, string ContentType);
public sealed record GuardianVideoIntake(string Id, string Status, string? Title, double? Confidence, string? ProducedAutomationId, string? ProducedSkillId, int CandidateCount, string? Error);
public sealed record GuardianAutomation(string Id, string Name, string? Mode);

/// <summary>The Guardian calls this app makes. Everything is HTTPS + JSON with a bearer token:
/// <list type="bullet">
/// <item>sign in: POST /api/auth/login {email,password,client} → token or a 2FA challenge; POST /api/auth/login/2fa; or the browser
/// (PKCE) flow /app/connect/vision → POST /api/harness/connect/token (same as Vision for Windows/Mac).</item>
/// <item>harness (the desktop-app API): GET /api/harness/me, /api/harness/v1/models, POST /api/harness/skills, /api/harness/prompts.</item>
/// <item>the web app's API with the same token: POST /api/uploads/init → PUT → /api/uploads/:id/finalize (or multipart for big
/// files), POST /api/videos {attachmentId, kind:"WORKFLOW"} (Guardian's Video intake drafts an automation from a screen
/// recording), POST /api/automations {name, description, prompt, mode:"DESKTOP", cron, timezone}.</item>
/// </list></summary>
public sealed class GuardianClient
{
    public const string DefaultAddress = "https://guardian.503cm.com";
    /// <summary>Guardian's connect page knows vision-windows / vision-mac / vision-harness; this app signs in as the platform's Vision
    /// client so Guardian's consent page and Connected apps list name it sensibly.</summary>
    public static string ClientTag => OperatingSystem.IsMacOS() ? "vision-mac" : "vision-windows";
    public const string Source = "screenrec-trainer";
    public const long SinglePutLimit = 0x3200000; // Guardian's web app switches to multipart above 50 MB
    public const long PartSize = 0x1000000;       // 16 MB parts

    private readonly HttpClient _http;
    private readonly HttpClient _storage;

    public GuardianClient(string address, GuardianSession? session = null, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        Address = NormalizeAddress(address) ?? throw new GuardianError(GuardianErrorKind.BadAddress, $"'{address}' isn't a Guardian address (e.g. https://guardian.503cm.com).");
        Session = session;
        OrgId = session?.OrgId;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(90);
        _storage = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _storage.Timeout = TimeSpan.FromHours(2);
    }

    public string Address { get; }
    public GuardianSession? Session { get; private set; }
    public string? OrgId { get; set; }
    public bool IsSignedIn => Session is not null;
    public string ProxyBaseUrl => Address + "/api/harness/v1";

    public static string? NormalizeAddress(string? typed)
    {
        var text = (typed ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLocal(uri.Host)) return null; // plain http only for a dev Guardian on this machine/LAN
        var path = uri.AbsolutePath;
        var at = path.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) path = path[..at];
        return $"{uri.Scheme}://{uri.Authority}{path.TrimEnd('/')}";
    }

    private static bool IsLocal(string host)
    {
        if (host is "localhost" || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !host.Contains('.')) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    public void SignOut() => Session = null;

    // MARK: Sign-in

    public async Task<GuardianSession> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new GuardianError(GuardianErrorKind.Credentials, "Enter the email and password you use at Guardian.");
        var json = await SendForSignInAsync("/api/auth/login", new JsonObject { ["email"] = email.Trim(), ["password"] = password, ["client"] = ClientTag }, cancellationToken).ConfigureAwait(false);
        if (json["twoFactorRequired"]?.GetValue<bool>() == true && Text(json["challenge"]) is { Length: > 0 } challenge)
            throw new GuardianError(GuardianErrorKind.TwoFactorRequired, "This Guardian account uses two-factor sign-in. Enter the 6-digit code from your authenticator app.") { Challenge = challenge };
        Session = ReadSession(json, email.Trim()) ?? throw new GuardianError(GuardianErrorKind.Credentials,
            Text(json["message"]) ?? "Guardian accepted the password but didn't hand over a sign-in for desktop apps. Use \"Sign in with your browser\".");
        return Session;
    }

    public async Task<GuardianSession> VerifyTwoFactorAsync(string challenge, string code, string? email = null, CancellationToken cancellationToken = default)
    {
        var digits = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (digits.Length != 6 || !digits.All(char.IsAsciiDigit))
            throw new GuardianError(GuardianErrorKind.Credentials, "The code is the 6-digit number from your authenticator app.");
        var json = await SendForSignInAsync("/api/auth/login/2fa", new JsonObject { ["challenge"] = challenge, ["code"] = digits }, cancellationToken).ConfigureAwait(false);
        Session = ReadSession(json, email ?? "") ?? throw new GuardianError(GuardianErrorKind.Credentials, "Check the code and try again — codes change every 30 seconds.");
        return Session;
    }

    public async Task<GuardianSession> ExchangeConnectCodeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken = default)
    {
        var json = await SendForSignInAsync("/api/harness/connect/token",
            new JsonObject { ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirectUri }, cancellationToken).ConfigureAwait(false);
        var session = ReadSession(json, "") ?? throw new GuardianError(GuardianErrorKind.BrowserSignIn, Text(json["message"]) ?? "Guardian didn't hand over a sign-in for that approval. Try again.");
        Session = session with { OrgId = Text(json["orgId"]) };
        OrgId = Session.OrgId ?? OrgId;
        return Session;
    }

    /// <summary>Use a saved token (after relaunch).</summary>
    public void Restore(GuardianSession session)
    {
        Session = session;
        OrgId = session.OrgId ?? OrgId;
    }

    internal static GuardianSession? ReadSession(JsonObject json, string typedEmail)
    {
        var token = Text(json["token"]) ?? Text(json["accessToken"]) ?? Text(json["sessionToken"]);
        if (string.IsNullOrWhiteSpace(token)) return null;
        var user = json["user"] as JsonObject;
        var session = json["session"] as JsonObject;
        string? Field(string name) => Text(user?[name]) ?? Text(session?[name]) ?? Text(json[name]);
        var email = Field("email") ?? typedEmail;
        var id = Text(user?["id"]) ?? Text(session?["userId"]) ?? Text(json["userId"]) ?? email;
        return new GuardianSession(id, email, Field("name"), token.Trim(), DateTimeOffset.Now, Text(json["orgId"]));
    }

    // MARK: Who and what

    public async Task<GuardianIdentity> MeAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetAsync("/api/harness/me", cancellationToken).ConfigureAwait(false);
        var user = json["user"] as JsonObject;
        var orgs = (json["orgs"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => Text(o["id"]) is { } id ? new GuardianOrg(id, Text(o["name"]) ?? id, o["active"]?.GetValue<bool>() == true) : null)
            .OfType<GuardianOrg>().ToList();
        return new GuardianIdentity(Text(user?["id"]) ?? Session?.UserId ?? "", Text(user?["email"]) ?? Session?.Email ?? "", Text(user?["name"]) ?? Session?.Name,
            orgs, Text(json["orgId"]), Strings(json["modules"]), Strings(json["permissions"]));
    }

    public async Task<IReadOnlyList<GuardianModel>> ModelsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetAsync("/api/harness/v1/models", cancellationToken).ConfigureAwait(false);
        var list = json["models"] as JsonArray ?? json["data"] as JsonArray ?? [];
        return list.OfType<JsonObject>()
            .Select(m => Text(m["id"]) is { Length: > 0 } id ? new GuardianModel(id, Text(m["label"]), (int?)Number(m["contextWindow"])) : null)
            .OfType<GuardianModel>().ToList();
    }

    // MARK: Skills (the expert's knowledge, usable by Guardian's chats and workers)

    /// <summary>Create or replace one of the person's skills: {name, description, files:{"SKILL.md": …}}.</summary>
    public async Task PutSkillAsync(GuardianSkill skill, CancellationToken cancellationToken = default)
    {
        var files = new JsonObject();
        foreach (var (path, text) in skill.Files) files[path] = text;
        await SendAsync(HttpMethod.Post, "/api/harness/skills", new JsonObject { ["name"] = skill.Name, ["description"] = skill.Description, ["files"] = files }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GuardianSkill>> SkillsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetAsync("/api/harness/skills", cancellationToken).ConfigureAwait(false);
        return (json["skills"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => Text(s["name"]) is { Length: > 0 } name
                ? new GuardianSkill(name, Text(s["description"]) ?? "", (s["files"] as JsonObject ?? []).Where(f => Text(f.Value) is not null).ToDictionary(f => f.Key, f => Text(f.Value)!))
                : null)
            .OfType<GuardianSkill>().ToList();
    }

    /// <summary>Record what was done in the person's Prompt Notebook (best effort).</summary>
    public async Task PushNoteAsync(string text, string? outcome = null, CancellationToken cancellationToken = default)
    {
        var row = new JsonObject { ["text"] = text, ["at"] = Iso(DateTimeOffset.Now), ["source"] = Source };
        if (outcome is not null) row["outcome"] = outcome;
        await SendAsync(HttpMethod.Post, "/api/harness/prompts", new JsonObject { ["prompts"] = new JsonArray(row) }, cancellationToken).ConfigureAwait(false);
    }

    // MARK: Uploads → Video intake

    public async Task<GuardianUpload> UploadFileAsync(string path, string contentType, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Nothing to upload", path);
        var name = info.Name;
        if (info.Length <= SinglePutLimit)
        {
            var init = await SendAsync(HttpMethod.Post, "/api/uploads/init",
                new JsonObject { ["name"] = name, ["contentType"] = contentType, ["size"] = info.Length }, cancellationToken).ConfigureAwait(false);
            var id = Text(init["attachmentId"]) ?? throw new GuardianError(GuardianErrorKind.Server, "Guardian didn't say where to upload.");
            var url = Text(init["uploadUrl"]) ?? throw new GuardianError(GuardianErrorKind.Server, "Guardian didn't give an upload address.");
            await PutBytesAsync(url, path, 0, info.Length, contentType, init["headers"] as JsonObject, cancellationToken).ConfigureAwait(false);
            progress?.Report(0.95);
            await SendAsync(HttpMethod.Post, $"/api/uploads/{Uri.EscapeDataString(id)}/finalize", new JsonObject(), cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            return new GuardianUpload(id, name, info.Length, contentType);
        }

        var start = await SendAsync(HttpMethod.Post, "/api/uploads/multipart/start",
            new JsonObject { ["name"] = name, ["contentType"] = contentType, ["size"] = info.Length, ["partSize"] = PartSize }, cancellationToken).ConfigureAwait(false);
        var attachment = Text(start["attachmentId"]) ?? throw new GuardianError(GuardianErrorKind.Server, "Guardian didn't start the upload.");
        var partSize = (long?)Number(start["partSize"]) ?? PartSize;
        var total = (int?)Number(start["totalParts"]) ?? (int)Math.Ceiling(info.Length / (double)partSize);
        var parts = new JsonArray();
        try
        {
            for (var n = 1; n <= total; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = await GetAsync($"/api/uploads/multipart/{Uri.EscapeDataString(attachment)}/part-url?partNumber={n}", cancellationToken).ConfigureAwait(false);
                var url = Text(part["url"]) ?? throw new GuardianError(GuardianErrorKind.Server, "Guardian didn't give a part address.");
                var offset = (n - 1) * partSize;
                var etag = await PutBytesAsync(url, path, offset, Math.Min(partSize, info.Length - offset), null, null, cancellationToken).ConfigureAwait(false);
                parts.Add(new JsonObject { ["partNumber"] = n, ["etag"] = etag });
                progress?.Report(n / (double)total * 0.98);
            }
            await SendAsync(HttpMethod.Post, $"/api/uploads/multipart/{Uri.EscapeDataString(attachment)}/complete", new JsonObject { ["parts"] = parts }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await SendAsync(HttpMethod.Delete, $"/api/uploads/{Uri.EscapeDataString(attachment)}", null, CancellationToken.None).ConfigureAwait(false); }
            catch (GuardianError) { }
            throw;
        }
        progress?.Report(1);
        return new GuardianUpload(attachment, name, info.Length, contentType);
    }

    private async Task<string> PutBytesAsync(string url, string path, long offset, long length, string? contentType, JsonObject? headers, CancellationToken cancellationToken)
    {
        if (url.StartsWith('/')) url = Address + url;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        file.Position = offset;
        using var content = new StreamContent(new SubStream(file, length));
        content.Headers.ContentLength = length;
        if (contentType is not null) content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        foreach (var (k, v) in headers ?? [])
        {
            var value = Text(v);
            if (value is null) continue;
            if (k.Equals("content-type", StringComparison.OrdinalIgnoreCase)) content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
            else if (!request.Headers.TryAddWithoutValidation(k, value)) content.Headers.TryAddWithoutValidation(k, value);
        }
        // A same-origin upload URL (a Guardian without S3) needs the session; a presigned S3 URL must not get it.
        if (url.StartsWith(Address, StringComparison.OrdinalIgnoreCase) && Session is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Session.Token);
        HttpResponseMessage response;
        try { response = await _storage.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw new GuardianError(GuardianErrorKind.Network, $"The upload didn't go through: {e.Message}", e); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new GuardianError(GuardianErrorKind.Server, $"Storage refused the upload: HTTP {(int)response.StatusCode}") { Status = (int)response.StatusCode };
            return response.Headers.ETag?.Tag ?? (response.Headers.TryGetValues("ETag", out var tags) ? tags.First() : "");
        }
    }

    /// <summary>Start Guardian's Video intake on an uploaded recording. WORKFLOW = a screen recording of a procedure (Guardian
    /// drafts an automation from it); MEETING = a narrated walkthrough (candidates + storyboard).</summary>
    public async Task<GuardianVideoIntake> StartVideoIntakeAsync(string attachmentId, string? title, string kind = "WORKFLOW", CancellationToken cancellationToken = default)
    {
        var options = kind == "MEETING"
            ? new JsonObject { ["autoEmitAutomation"] = false, ["extractCandidates"] = true, ["buildStoryboard"] = true }
            : new JsonObject { ["autoEmitAutomation"] = true, ["extractCandidates"] = false, ["buildStoryboard"] = false };
        var body = new JsonObject { ["attachmentId"] = attachmentId, ["kind"] = kind, ["options"] = options };
        if (!string.IsNullOrWhiteSpace(title)) body["title"] = title.Trim();
        var json = await SendAsync(HttpMethod.Post, "/api/videos", body, cancellationToken).ConfigureAwait(false);
        return ReadIntake(json) ?? new GuardianVideoIntake(Text(json["id"]) ?? "", Text(json["status"]) ?? "QUEUED", title, null, null, null, 0, null);
    }

    public async Task<IReadOnlyList<GuardianVideoIntake>> VideoIntakesAsync(CancellationToken cancellationToken = default)
    {
        var node = await SendNodeAsync(HttpMethod.Get, "/api/videos", null, cancellationToken).ConfigureAwait(false);
        var list = node as JsonArray ?? (node as JsonObject)?["items"] as JsonArray ?? (node as JsonObject)?["videos"] as JsonArray ?? [];
        return list.OfType<JsonObject>().Select(ReadIntake).OfType<GuardianVideoIntake>().ToList();
    }

    public async Task<GuardianVideoIntake?> VideoIntakeAsync(string id, CancellationToken cancellationToken = default) =>
        ReadIntake(await GetAsync($"/api/videos/{Uri.EscapeDataString(id)}", cancellationToken).ConfigureAwait(false));

    internal static GuardianVideoIntake? ReadIntake(JsonObject o) => Text(o["id"]) is { Length: > 0 } id
        ? new GuardianVideoIntake(id, Text(o["status"]) ?? "UNKNOWN", Text(o["title"]), Number(o["confidence"]), Text(o["producedAutomationId"]),
            Text(o["producedSkillId"]), (int?)Number(o["candidateCount"]) ?? 0, Text(o["errorMsg"]))
        : null;

    // MARK: Automations

    /// <summary>Create a Guardian automation: a runbook a Guardian desktop worker runs (mode DESKTOP, browser stays logged in),
    /// on demand (cron null) or on a schedule.</summary>
    public async Task<GuardianAutomation> CreateAutomationAsync(string name, string description, string prompt, string? cron = null, string mode = "DESKTOP",
                                                                string? timezone = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["prompt"] = prompt,
            ["mode"] = mode,
            ["cron"] = cron,
            ["timezone"] = timezone ?? LocalTimeZoneId(),
        };
        var json = await SendAsync(HttpMethod.Post, "/api/automations", body, cancellationToken).ConfigureAwait(false);
        var id = Text(json["id"]) ?? throw new GuardianError(GuardianErrorKind.Server, "Guardian created the automation but didn't say its id.");
        return new GuardianAutomation(id, Text(json["name"]) ?? name, Text(json["mode"]) ?? mode);
    }

    public async Task RunAutomationAsync(string id, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Post, $"/api/automations/{Uri.EscapeDataString(id)}/run", null, cancellationToken).ConfigureAwait(false);

    public string AutomationUrl(string id) => $"{Address}/app/automations/{Uri.EscapeDataString(id)}";
    public string VideoUrl(string id) => $"{Address}/app/videos/{Uri.EscapeDataString(id)}";

    /// <summary>An IANA zone (Guardian's schedules use them); Windows ids are converted.</summary>
    public static string LocalTimeZoneId()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "UTC";
    }

    // MARK: Wire

    internal static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private HttpRequestMessage Request(HttpMethod method, string path, bool authorized = true)
    {
        var request = new HttpRequestMessage(method, Address + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"ScreenRecTrainer/1.0 ({(OperatingSystem.IsMacOS() ? "macOS" : "Windows")})");
        if (authorized && Session?.Token is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (authorized && OrgId is { Length: > 0 } org) request.Headers.TryAddWithoutValidation("x-guardian-org", org);
        return request;
    }

    private Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, path, null, cancellationToken);

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken) =>
        await SendNodeAsync(method, path, body, cancellationToken).ConfigureAwait(false) as JsonObject ?? [];

    private async Task<JsonNode?> SendNodeAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        if (Session is null) throw new GuardianError(GuardianErrorKind.Unauthorized, "Sign in to Guardian first.");
        using var request = Request(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
        else if (method == HttpMethod.Post) request.Content = new StringContent("{}", new UTF8Encoding(false), "application/json");
        return await Exchange(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject> SendForSignInAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, path, authorized: false);
        request.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
        try
        {
            return await Exchange(request, cancellationToken).ConfigureAwait(false) as JsonObject ?? [];
        }
        catch (GuardianError e) when (e.Status is 400 or 401 or 403 or 422)
        {
            throw new GuardianError(GuardianErrorKind.Credentials, e.Status == 401
                ? "Guardian didn't accept that email and password. (Microsoft / SSO accounts: use \"Sign in with your browser\".)"
                : e.Message) { Status = e.Status };
        }
    }

    private async Task<JsonNode?> Exchange(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw new GuardianError(GuardianErrorKind.Network, $"Couldn't reach Guardian at {Address}: {e.InnerException?.Message ?? e.Message}", e); }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested) { throw new GuardianError(GuardianErrorKind.Network, "Guardian didn't answer in time.", e); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw GuardianError.FromResponse((int)response.StatusCode, text);
            if (text.Length == 0) return null;
            try { return JsonNode.Parse(text); }
            catch (JsonException)
            {
                throw new GuardianError(GuardianErrorKind.Server, "Guardian answered with a web page instead of data — check the address.") { Status = (int)response.StatusCode };
            }
        }
    }

    internal static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n ? n.ToJsonString() : null;
    internal static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    internal static IReadOnlyList<string> Strings(JsonNode? node) => (node as JsonArray ?? []).Select(Text).OfType<string>().ToList();
}

/// <summary>A window onto part of a stream (one multipart part).</summary>
internal sealed class SubStream(Stream inner, long length) : Stream
{
    private long _read;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, (int)Math.Min(count, length - _read));
        _read += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
