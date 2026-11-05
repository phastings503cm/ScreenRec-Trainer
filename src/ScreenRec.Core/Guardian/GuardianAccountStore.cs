using System.Text.Json;
using ScreenRec.Core.Storage;

namespace ScreenRec.Core.Guardian;

/// <summary>Who is signed in (guardian.json, no secrets) and the token (secret store).</summary>
public sealed record GuardianAccount
{
    public string Address { get; init; } = GuardianClient.DefaultAddress;
    public string Email { get; init; } = "";
    public string? Name { get; init; }
    public string? UserId { get; init; }
    public string? OrgId { get; init; }
    public string? OrgName { get; init; }
    public string Method { get; init; } = "password";
    public DateTimeOffset SignedInAt { get; init; } = DateTimeOffset.Now;
    public string Label => string.IsNullOrWhiteSpace(Name) ? Email : $"{Name} ({Email})";
}

public sealed class GuardianAccountStore(ISecretStore secrets, string? path = null)
{
    private readonly string _path = path ?? AppPaths.GuardianFile;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public (GuardianAccount Account, GuardianSession Session)? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var account = JsonSerializer.Deserialize<GuardianAccount>(File.ReadAllText(_path), Json);
            var token = secrets.Read(SecretNames.GuardianToken);
            if (account is null || string.IsNullOrWhiteSpace(token)) return null;
            return (account, new GuardianSession(account.UserId ?? account.Email, account.Email, account.Name, token, account.SignedInAt, account.OrgId));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public GuardianAccount? LastAccount()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<GuardianAccount>(File.ReadAllText(_path), Json) : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    public GuardianAccount Save(GuardianClient client, GuardianSession session, string method, string? orgName = null)
    {
        var account = new GuardianAccount
        {
            Address = client.Address, Email = session.Email, Name = session.Name, UserId = session.UserId,
            OrgId = client.OrgId ?? session.OrgId, OrgName = orgName, Method = method, SignedInAt = session.SignedInAt,
        };
        secrets.Write(SecretNames.GuardianToken, session.Token);
        File.WriteAllText(_path, JsonSerializer.Serialize(account, Json));
        return account;
    }

    /// <summary>Forget the token; keep the address and email for the next sign-in form.</summary>
    public void SignOut() => secrets.Delete(SecretNames.GuardianToken);
}
