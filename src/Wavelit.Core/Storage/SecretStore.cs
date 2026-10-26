using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wavelit.Core.Storage;

public static class SecretNames
{
    public const string PexelsApiKey = "pexels";
    public const string ShadertoyApiKey = "shadertoy";
    public const string PixabayApiKey = "pixabay";
    public const string CloudflareAccountId = "cloudflare-account";
    public const string CloudflareApiToken = "cloudflare-token";
}

/// <summary>
/// API keys encrypted with DPAPI (bound to the current Windows user). Keys never leave this class
/// except to be placed in outgoing requests.
/// </summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = "Wavelit.Secrets.v1"u8.ToArray();
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, string> _values;

    public SecretStore(AppPaths paths)
    {
        _path = paths.SecretsFile;
        _values = Load();
    }

    public event EventHandler<string>? Changed;

    public string? Get(string name)
    {
        lock (_gate)
            return _values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    public bool Has(string name) => Get(name) is not null;

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(value)) _values.Remove(name);
            else _values[name] = value.Trim();
            Save();
        }
        Changed?.Invoke(this, name);
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Unreadable (e.g. copied from another user/PC): start fresh rather than crash.
            return new();
        }
    }

    private void Save()
    {
        byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_values));
        byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, _path, overwrite: true);
    }
}

public static class Redact
{
    /// <summary>Removes query strings (Shadertoy puts the API key there) before a URL is logged.</summary>
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        int q = url.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? url : url[..q] + "?…";
    }
}
