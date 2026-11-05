using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScreenRec.Core.Ai;

public abstract record ChatPart;
public sealed record TextPart(string Text) : ChatPart;
public sealed record ImagePart(byte[] Jpeg) : ChatPart;

public sealed record ChatMessage(string Role, IReadOnlyList<ChatPart> Parts)
{
    public static ChatMessage System(string text) => new("system", [new TextPart(text)]);
    public static ChatMessage User(string text) => new("user", [new TextPart(text)]);
    public static ChatMessage User(params ChatPart[] parts) => new("user", parts);
}

public sealed record ChatOptions
{
    public double? Temperature { get; init; } = 0.2;
    public int? MaxTokens { get; init; } = 8192;
    /// <summary>low / high / max. Sent as chat_template_kwargs.reasoning_effort (vLLM GLM-5.3, the Spark recipe) and as the
    /// top-level reasoning_effort for hosted proxies.</summary>
    public string? ReasoningEffort { get; init; }
    /// <summary>Ask for a JSON object (response_format json_object) — dropped if the server refuses it.</summary>
    public bool JsonObject { get; init; }
}

public sealed record ChatResult(string Content, string? Reasoning, int PromptTokens, int CompletionTokens, string Model);

public sealed class AiError(string message, int? status = null, Exception? inner = null) : Exception(message, inner)
{
    public int? Status { get; } = status;
}

public interface IChatClient
{
    string Describe { get; }
    Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Talks to any OpenAI-compatible /v1/chat/completions: the GLM-5.3 Flash Spark server (http://&lt;head&gt;:8888/v1,
/// model GLM-5.3-Flash-EXL3, optional VLLM_API_KEY), Guardian's harness proxy (&lt;guardian&gt;/api/harness/v1 with the sign-in
/// token), or anything else (Ollama, LM Studio, Spark Swapper routes). Images go as data: URLs.</summary>
public sealed class OpenAiChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<string?> _apiKey;
    private readonly HashSet<string> _dropped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _extraHeaders;

    public OpenAiChatClient(string baseUrl, string model, Func<string?> apiKey, TimeSpan? timeout = null, HttpMessageHandler? handler = null,
                            IReadOnlyDictionary<string, string>? headers = null, string kind = "OpenAI-compatible")
    {
        _baseUrl = NormalizeBaseUrl(baseUrl) ?? throw new AiError($"'{baseUrl}' isn't a server address (expected something like http://spark:8888/v1).");
        Model = model;
        _apiKey = apiKey;
        Kind = kind;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = timeout ?? TimeSpan.FromMinutes(10);
        _extraHeaders = headers?.ToDictionary(k => k.Key, k => k.Value) ?? new();
    }

    public string Model { get; private set; }
    public string Kind { get; }
    public string BaseUrl => _baseUrl;
    public string Describe => $"{Kind} · {Model} @ {_baseUrl}";

    /// <summary>"spark:8888" → "http://spark:8888/v1"; a trailing /chat/completions is dropped.</summary>
    public static string? NormalizeBaseUrl(string? typed)
    {
        var text = (typed ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return null;
        if (text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) text = text[..^"/chat/completions".Length];
        if (uri.AbsolutePath is "" or "/") text += "/v1";
        return text;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("ScreenRecTrainer/1.0");
        if (_apiKey() is { Length: > 0 } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        foreach (var (k, v) in _extraHeaders) request.Headers.TryAddWithoutValidation(k, v);
        return request;
    }

    /// <summary>The ids the server lists at /models.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "/models");
        using var response = await Send(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, text);
        var json = JsonNode.Parse(text);
        var list = json?["data"] as JsonArray ?? json?["models"] as JsonArray ?? [];
        return list.Select(m => m?["id"]?.GetValue<string>()).OfType<string>().ToList();
    }

    /// <summary>Fill in the model from /models when none is set.</summary>
    public async Task EnsureModelAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(Model)) return;
        var models = await ListModelsAsync(cancellationToken).ConfigureAwait(false);
        Model = models.FirstOrDefault() ?? throw new AiError("The server lists no models.");
    }

    internal JsonObject Body(IReadOnlyList<ChatMessage> messages, ChatOptions options)
    {
        var list = new JsonArray();
        foreach (var m in messages)
        {
            if (m.Parts.All(p => p is TextPart))
            {
                list.Add(new JsonObject { ["role"] = m.Role, ["content"] = string.Join("\n", m.Parts.Cast<TextPart>().Select(p => p.Text)) });
                continue;
            }
            var parts = new JsonArray();
            foreach (var p in m.Parts)
            {
                parts.Add(p switch
                {
                    TextPart tp => new JsonObject { ["type"] = "text", ["text"] = tp.Text },
                    ImagePart i => new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(i.Jpeg) },
                    },
                    _ => throw new ArgumentOutOfRangeException(nameof(messages)),
                });
            }
            list.Add(new JsonObject { ["role"] = m.Role, ["content"] = parts });
        }
        var body = new JsonObject { ["model"] = Model, ["messages"] = list, ["stream"] = false };
        if (options.Temperature is { } t && !_dropped.Contains("temperature")) body["temperature"] = t;
        if (options.MaxTokens is { } mt && !_dropped.Contains("max_tokens")) body["max_tokens"] = mt;
        if (options.ReasoningEffort is { Length: > 0 } effort)
        {
            if (!_dropped.Contains("chat_template_kwargs")) body["chat_template_kwargs"] = new JsonObject { ["reasoning_effort"] = effort };
            if (!_dropped.Contains("reasoning_effort") && Kind != "GLM Spark") body["reasoning_effort"] = effort;
        }
        if (options.JsonObject && !_dropped.Contains("response_format")) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        return body;
    }

    private static readonly string[] Optional = ["response_format", "reasoning_effort", "chat_template_kwargs", "temperature", "max_tokens"];

    public async Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken = default)
    {
        await EnsureModelAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var body = Body(messages, options);
            using var request = Request(HttpMethod.Post, "/chat/completions");
            request.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
            using var response = await Send(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return Parse(text);

            var status = (int)response.StatusCode;
            // A server that refuses an optional setting: leave that setting out and try again (once per setting).
            if (status is 400 or 422 && attempt < Optional.Length)
            {
                var named = Optional.FirstOrDefault(o => body.ContainsKey(o) && text.Contains(o, StringComparison.OrdinalIgnoreCase))
                            ?? Optional.FirstOrDefault(o => body.ContainsKey(o) && o is "response_format" or "reasoning_effort");
                if (named is not null && _dropped.Add(named)) continue;
            }
            if (status is 429 or 502 or 503 or 504 && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }
            throw Failure(response.StatusCode, text);
        }
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new AiError($"Couldn't reach {_baseUrl}: {e.InnerException?.Message ?? e.Message}", null, e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiError($"{_baseUrl} didn't answer within {_http.Timeout.TotalSeconds:0} s.", null, e);
        }
    }

    private AiError Failure(HttpStatusCode code, string text)
    {
        string? said = null;
        try
        {
            var json = JsonNode.Parse(text);
            said = json?["error"]?["message"]?.GetValue<string>() ?? json?["message"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        said ??= text.Length > 300 ? text[..300] + "…" : text;
        var hint = (int)code switch
        {
            401 or 403 => " (check the API key — the Spark's VLLM_API_KEY, or sign in to Guardian again)",
            404 => " (check the address ends in /v1 and the model id is right)",
            _ => "",
        };
        return new AiError($"{Kind} answered HTTP {(int)code}: {said}{hint}", (int)code);
    }

    internal ChatResult Parse(string text)
    {
        JsonNode? json;
        try { json = JsonNode.Parse(text); }
        catch (JsonException e) { throw new AiError("The model server answered with something that isn't JSON.", null, e); }
        var message = json?["choices"]?[0]?["message"];
        var content = message?["content"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray a => string.Concat(a.Select(p => p?["text"]?.GetValue<string>() ?? "")),
            _ => "",
        };
        var reasoning = message?["reasoning_content"]?.GetValue<string>() ?? message?["reasoning"]?.GetValue<string>();
        var usage = json?["usage"];
        return new ChatResult(StripThinking(content), reasoning,
            usage?["prompt_tokens"]?.GetValue<int>() ?? 0, usage?["completion_tokens"]?.GetValue<int>() ?? 0,
            json?["model"]?.GetValue<string>() ?? Model);
    }

    /// <summary>Removes &lt;think&gt;…&lt;/think&gt; a template left in the answer.</summary>
    public static string StripThinking(string content) =>
        Regex.Replace(content ?? "", @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
}

public static class JsonExtract
{
    /// <summary>The first JSON object in a model's answer — tolerating ```json fences, prose around it and trailing commas.</summary>
    public static JsonObject Object(string answer)
    {
        var text = OpenAiChatClient.StripThinking(answer);
        var fence = Regex.Match(text, @"```(?:json)?\s*(\{.*\})\s*```", RegexOptions.Singleline);
        if (fence.Success) text = fence.Groups[1].Value;
        var start = text.IndexOf('{');
        if (start < 0) throw new AiError("The model didn't answer with JSON.");
        // Walk to the matching brace (strings respected).
        int depth = 0, end = -1;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) { end = i; break; }
        }
        var candidate = end > 0 ? text[start..(end + 1)] : text[start..];
        foreach (var attempt in new[] { candidate, Regex.Replace(candidate, @",\s*([}\]])", "$1") })
        {
            try
            {
                if (JsonNode.Parse(attempt, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) is JsonObject o) return o;
            }
            catch (JsonException) { }
        }
        throw new AiError("The model's JSON couldn't be read (it may have been cut off — try a higher max tokens).");
    }
}
