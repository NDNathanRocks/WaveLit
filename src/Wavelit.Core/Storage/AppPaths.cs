namespace Wavelit.Core.Storage;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavelit");
        Directory.CreateDirectory(Root);
        foreach (var dir in new[] { CacheRoot, MediaCache, ShadertoyCache, ThumbnailCache, Logs, WebView2, Models, Creations })
            Directory.CreateDirectory(dir);
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string LibraryFile => Path.Combine(Root, "library.json");
    public string SecretsFile => Path.Combine(Root, "secrets.dat");
    public string CacheRoot => Path.Combine(Root, "cache");
    public string MediaCache => Path.Combine(CacheRoot, "media");
    public string ShadertoyCache => Path.Combine(CacheRoot, "shadertoy");
    public string ShadertoyMedia => Path.Combine(ShadertoyCache, "media");
    public string ThumbnailCache => Path.Combine(CacheRoot, "thumbs");
    public string Logs => Path.Combine(Root, "logs");
    public string WebView2 => Path.Combine(Root, "WebView2");
    /// <summary>Optional Local AI pack (several GB; never touched by "Clear cache").</summary>
    public string Models => Path.Combine(Root, "models");
    /// <summary>Images made on the Create page. Not a cache: they can't be downloaded again.</summary>
    public string Creations => Path.Combine(Root, "creations");
}
