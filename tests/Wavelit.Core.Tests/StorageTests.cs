using Microsoft.Extensions.Logging.Abstractions;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wavelit-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public StorageTests() => _paths = new AppPaths(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Settings_round_trip_and_clamp()
    {
        var store = new SettingsStore(_paths);
        store.Update(s => s with { ShaderFpsCap = 500, Volume = -3, Displays = s.Displays.Assign("x", null, []) });

        var reloaded = new SettingsStore(_paths).Current;
        Assert.Equal(144, reloaded.ShaderFpsCap);
        Assert.Equal(0, reloaded.Volume);
        Assert.Equal("x", reloaded.Displays.SharedItemId);
    }

    [Fact]
    public void Version_1_settings_switch_from_duplicate_to_extend_once()
    {
        File.WriteAllText(_paths.SettingsFile, """{ "schemaVersion": 1, "displays": { "layout": "Duplicate", "sharedItemId": "x" } }""");
        var store = new SettingsStore(_paths);
        Assert.Equal(DisplayLayout.Span, store.Current.Displays.Layout);
        Assert.Equal("x", store.Current.Displays.SharedItemId);

        // A later choice of Duplicate is respected.
        store.Update(s => s with { Displays = s.Displays with { Layout = DisplayLayout.Duplicate } });
        Assert.Equal(DisplayLayout.Duplicate, new SettingsStore(_paths).Current.Displays.Layout);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        File.WriteAllText(_paths.SettingsFile, "{ not json");
        var store = new SettingsStore(_paths);
        Assert.Equal(60, store.Current.ShaderFpsCap); // Pristine motion is the default
        Assert.True(File.Exists(_paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void Secrets_are_encrypted_at_rest()
    {
        var secrets = new SecretStore(_paths);
        secrets.Set(SecretNames.PexelsApiKey, "  my-secret-key  ");
        Assert.Equal("my-secret-key", new SecretStore(_paths).Get(SecretNames.PexelsApiKey));
        Assert.DoesNotContain("my-secret-key", File.ReadAllText(_paths.SecretsFile));

        secrets.Set(SecretNames.PexelsApiKey, "");
        Assert.False(new SecretStore(_paths).Has(SecretNames.PexelsApiKey));
    }

    [Fact]
    public void Library_persists_user_items_but_not_built_ins()
    {
        var library = new LibraryStore(_paths);
        library.Upsert(LocalFiles.CreateUrlItem("example.com")!);
        var reloaded = new LibraryStore(_paths);
        Assert.Contains(reloaded.All, i => i.Source == WallpaperSources.Url);
        Assert.Contains(reloaded.All, i => i.IsBuiltIn);
        Assert.DoesNotContain("builtin-", File.ReadAllText(_paths.LibraryFile));
    }

    [Fact]
    public async Task Cache_evicts_least_recently_used_first_and_keeps_protected_files()
    {
        var cache = new MediaCache(_paths, new NoHttp(), NullLogger<MediaCache>.Instance);
        string Make(string name, int kb, int minutesAgo)
        {
            string path = Path.Combine(_paths.MediaCache, name);
            File.WriteAllBytes(path, new byte[kb * 1024]);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
            return path;
        }
        string oldest = Make("a.mp4", 100, 30);
        string protectedOld = Make("b.mp4", 100, 20);
        string newest = Make("c.mp4", 100, 1);

        await cache.EvictAsync(150 * 1024, new HashSet<string> { "b.mp4" });

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(protectedOld));
        Assert.False(File.Exists(newest)); // still over the limit after skipping the protected file
    }

    [Fact]
    public async Task Cache_refuses_non_https_downloads()
    {
        var cache = new MediaCache(_paths, new NoHttp(), NullLogger<MediaCache>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.DownloadAsync("http://example.com/a.mp4", "a.mp4", null, CancellationToken.None));
    }

    [Fact]
    public void Redaction_removes_query_strings()
    {
        Assert.Equal("https://www.shadertoy.com/api/v1/shaders?…", Redact.Url("https://www.shadertoy.com/api/v1/shaders?key=SECRET"));
        Assert.Equal("https://a/b", Redact.Url("https://a/b"));
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in tests.");
    }
}
