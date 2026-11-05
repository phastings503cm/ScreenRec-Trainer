using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ScreenRec.Core.Guardian;

/// <summary>A PKCE pair and the state that ties the answer to this request (RFC 7636, S256).</summary>
public sealed record GuardianPkce(string Verifier, string Challenge, string State)
{
    public static GuardianPkce Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new GuardianPkce(verifier, ChallengeFor(verifier), Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public override string ToString() => "GuardianPkce { … }";
}

/// <summary>"Sign in with your browser" for Microsoft / SSO accounts (RFC 8252): listen on a loopback port, open
/// &lt;guardian&gt;/app/connect/vision?redirect_uri=http://127.0.0.1:&lt;port&gt;/callback&amp;state&amp;code_challenge…, Guardian
/// signs the person in however their org does and redirects back with a one-time code, which is traded for a token.</summary>
public static class GuardianBrowserSignIn
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static string ConnectUrl(string address, string redirectUri, GuardianPkce pkce, string device) =>
        $"{address.TrimEnd('/')}/app/connect/vision?redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(pkce.State)}"
        + $"&code_challenge={Uri.EscapeDataString(pkce.Challenge)}&code_challenge_method=S256&device={Uri.EscapeDataString(device)}"
        + $"&client={GuardianClient.ClientTag}";

    public static string DeviceName() => $"ScreenRec Trainer on {Environment.MachineName}";

    public static async Task<GuardianSession> SignInAsync(GuardianClient client, Action<string>? openBrowser = null, TimeSpan? timeout = null,
                                                          CancellationToken cancellationToken = default)
    {
        var pkce = GuardianPkce.Create();
        using var receiver = LoopbackReceiver.Start();
        var url = ConnectUrl(client.Address, receiver.RedirectUri, pkce, DeviceName());
        try
        {
            (openBrowser ?? OpenInBrowser)(url);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new GuardianError(GuardianErrorKind.BrowserSignIn, $"Couldn't open your browser ({e.Message}). Sign in with email and password instead.");
        }
        var code = await receiver.WaitForCodeAsync(pkce.State, timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return await client.ExchangeConnectCodeAsync(code, pkce.Verifier, receiver.RedirectUri, cancellationToken).ConfigureAwait(false);
    }

    public static void OpenInBrowser(string url)
    {
        if (OperatingSystem.IsMacOS()) Process.Start("open", [url])?.Dispose();
        else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        else Process.Start("xdg-open", [url])?.Dispose();
    }
}

/// <summary>A one-shot HTTP listener on 127.0.0.1 for the browser's redirect (a bare socket: no URL reservation, no admin).</summary>
public sealed class LoopbackReceiver : IDisposable
{
    private readonly TcpListener _listener;

    private LoopbackReceiver(TcpListener listener, bool ipv6)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IsIPv6 = ipv6;
    }

    /// <summary>Listen on a free loopback port. IPv6 ([::1]) first: guardian.503cm.com's firewall refuses a query that carries
    /// "http://127.0.0.1…" (an SSRF rule) while Guardian's connect page accepts both loopback forms.</summary>
    public static LoopbackReceiver Start(bool preferIPv6 = true)
    {
        if (preferIPv6 && Socket.OSSupportsIPv6)
        {
            try
            {
                var v6 = new TcpListener(IPAddress.IPv6Loopback, 0);
                v6.Start();
                return new LoopbackReceiver(v6, true);
            }
            catch (SocketException) { /* no IPv6 loopback: use 127.0.0.1 */ }
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackReceiver(listener, false);
    }

    public int Port { get; }
    public bool IsIPv6 { get; }
    public string RedirectUri => IsIPv6 ? $"http://[::1]:{Port}/callback" : $"http://127.0.0.1:{Port}/callback";

    public async Task<string> WaitForCodeAsync(string state, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (true)
        {
            TcpClient connection;
            try { connection = await _listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GuardianError(GuardianErrorKind.BrowserSignIn, $"Nothing came back from the browser within {(int)timeout.TotalMinutes} minutes. Start the sign-in again.");
            }
            using (connection)
            {
                var stream = connection.GetStream();
                string? target;
                try { target = await ReadTargetAsync(stream, cts.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { continue; }
                if (target is null || !target.StartsWith("/callback", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 404, "Not here", "ScreenRec Trainer is waiting for Guardian's sign-in answer.", cts.Token).ConfigureAwait(false);
                    continue;
                }
                var query = ParseQuery(target);
                if (!query.TryGetValue("state", out var got) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(state)))
                {
                    await RespondAsync(stream, 400, "Not this sign-in", "This answer is for another sign-in (an older tab?). Start again from the app.", cts.Token).ConfigureAwait(false);
                    continue;
                }
                if (query.TryGetValue("error", out var refused))
                {
                    await RespondAsync(stream, 200, "Not connected", "Guardian didn't connect the app. You can close this tab.", cts.Token).ConfigureAwait(false);
                    throw new GuardianError(GuardianErrorKind.BrowserSignIn, refused == "access_denied" ? "You chose Deny in Guardian." : $"Guardian couldn't finish the sign-in ({query.GetValueOrDefault("error_description") ?? refused}).");
                }
                if (!query.TryGetValue("code", out var code) || code.Length == 0)
                {
                    await RespondAsync(stream, 400, "Sign-in didn't finish", "Guardian's answer had no code.", cts.Token).ConfigureAwait(false);
                    throw new GuardianError(GuardianErrorKind.BrowserSignIn, "Guardian's answer had no sign-in code. Try again.");
                }
                await RespondAsync(stream, 200, "Connected", "ScreenRec Trainer is signed in to Guardian. You can close this tab and go back to the app.", cts.Token).ConfigureAwait(false);
                return code;
            }
        }
    }

    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        read.CancelAfter(TimeSpan.FromSeconds(10));
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), read.Token).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var parts = Encoding.ASCII.GetString(buffer, 0, total).Split("\r\n", 2)[0].Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
    }

    public static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = target.IndexOf('?');
        if (at < 0) return result;
        foreach (var pair in target[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }
        return result;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string title, string message, CancellationToken cancellationToken)
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>ScreenRec Trainer</title><style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:15vh auto;padding:0 1.5rem}</style></head>"
                   + $"<body><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (SocketException) { }
    }
}
