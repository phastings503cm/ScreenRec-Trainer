using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenRec.Core.Storage;

/// <summary>Keeps tokens and API keys out of the settings file. Windows: DPAPI (current user) in secrets.bin.
/// macOS: the login Keychain (service "ScreenRecTrainer"). Elsewhere (and when the Keychain refuses): an AES file whose key
/// sits beside it with owner-only permissions — better than plain text, not a vault.</summary>
public interface ISecretStore
{
    string? Read(string name);
    void Write(string name, string value);
    void Delete(string name);
}

public static class SecretStores
{
    public static ISecretStore CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new DpapiSecretStore(Path.Combine(AppPaths.Root, "secrets.bin"));
        if (OperatingSystem.IsMacOS()) return new KeychainSecretStore(new FileSecretStore(AppPaths.Root));
        return new FileSecretStore(AppPaths.Root);
    }
}

/// <summary>In memory only (tests).</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();
    public string? Read(string name) => _values.GetValueOrDefault(name);
    public void Write(string name, string value) => _values[name] = value;
    public void Delete(string name) => _values.Remove(name);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string path) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScreenRecTrainer/v1");
    private readonly object _gate = new();

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            return new();
        }
    }

    private void Save(Dictionary<string, string> values)
    {
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(values), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, bytes);
    }

    public string? Read(string name) { lock (_gate) return Load().GetValueOrDefault(name); }
    public void Write(string name, string value) { lock (_gate) { var v = Load(); v[name] = value; Save(v); } }
    public void Delete(string name) { lock (_gate) { var v = Load(); if (v.Remove(name)) Save(v); } }
}

/// <summary>macOS Keychain through /usr/bin/security (no native binding needed). Falls back to <paramref name="fallback"/>
/// when the Keychain is locked or unavailable (CI, SSH sessions).</summary>
public sealed class KeychainSecretStore(ISecretStore fallback) : ISecretStore
{
    public const string Service = "ScreenRecTrainer";

    private static (int Code, string Out) Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return (-1, ""); }
            return (p.ExitCode, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "");
        }
    }

    public string? Read(string name)
    {
        var (code, output) = Run("find-generic-password", "-s", Service, "-a", name, "-w");
        if (code == 0) return output.TrimEnd('\n', '\r');
        return fallback.Read(name);
    }

    public void Write(string name, string value)
    {
        // -U updates an existing item. The value goes through argv briefly (the security tool has no stdin mode for -w).
        var (code, _) = Run("add-generic-password", "-U", "-s", Service, "-a", name, "-w", value);
        if (code == 0) fallback.Delete(name);
        else fallback.Write(name, value);
    }

    public void Delete(string name)
    {
        Run("delete-generic-password", "-s", Service, "-a", name);
        fallback.Delete(name);
    }
}

/// <summary>AES-GCM file with a per-install key (0600 on Unix).</summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _dataPath;
    private readonly string _keyPath;
    private readonly object _gate = new();

    public FileSecretStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _dataPath = Path.Combine(folder, "secrets.dat");
        _keyPath = Path.Combine(folder, ".secrets.key");
    }

    private byte[] Key()
    {
        if (File.Exists(_keyPath)) return File.ReadAllBytes(_keyPath);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_keyPath, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_dataPath)) return new();
        try
        {
            var all = File.ReadAllBytes(_dataPath);
            var nonce = all[..12];
            var tag = all[12..28];
            var cipher = all[28..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key(), 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or ArgumentException)
        {
            return new();
        }
    }

    private void Save(Dictionary<string, string> values)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(values);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(Key(), 16)) aes.Encrypt(nonce, plain, cipher, tag);
        File.WriteAllBytes(_dataPath, [.. nonce, .. tag, .. cipher]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dataPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public string? Read(string name) { lock (_gate) return Load().GetValueOrDefault(name); }
    public void Write(string name, string value) { lock (_gate) { var v = Load(); v[name] = value; Save(v); } }
    public void Delete(string name) { lock (_gate) { var v = Load(); if (v.Remove(name)) Save(v); } }
}

public static class SecretNames
{
    public const string GuardianToken = "guardian/token";
    public const string GuardianCookies = "guardian/cookies";
    public const string SparkApiKey = "spark/api-key";
    public const string OtherApiKey = "openai-compatible/api-key";
}
