using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Create.Shaders;

/// <summary>One Studio creation, kept so the user can go back to it or branch from it.</summary>
public sealed record StudioEntry
{
    public required string Id { get; init; }
    public required ShaderKind Kind { get; init; }
    public required string Prompt { get; init; }
    public Interactivity Interactivity { get; init; }
    public string Palette { get; init; } = ShaderPalettes.Auto;
    public MotionSpeed Speed { get; init; } = MotionSpeed.Calm;
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public required string Code { get; init; }
    public string Mood { get; init; } = "abstract";
    public bool UsesPointer { get; init; }
    /// <summary>Model that wrote it (or "pasted").</summary>
    public string Model { get; init; } = "";
    public double FrameMs { get; init; }
    public string? ThumbnailPath { get; init; }
    /// <summary>The entry this one refined, if any.</summary>
    public string? ParentId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>The recipe it was made from (Remix loads it back), if any.</summary>
    public ShaderRecipe? Recipe { get; init; }
    /// <summary>How it reacts to music (Audio tab), if it does.</summary>
    public MusicReaction? Music { get; init; }
    /// <summary>Slider values the user chose (null = the shader's defaults).</summary>
    public double[]? Params { get; init; }

    public ShaderRequest Request => new(Kind, Prompt, Interactivity, Palette, Speed, Recipe: Recipe, Music: Music);
}

/// <summary>
/// The Studio's history (studio/history.json, newest first, at most <see cref="Capacity"/>), with each entry's
/// thumbnail in the same folder. Thread-safe.
/// </summary>
public sealed class StudioHistory
{
    public const int Capacity = 50;
    private readonly string _folder;
    private readonly string _file;
    private readonly Lock _gate = new();
    private List<StudioEntry> _entries;

    public StudioHistory(AppPaths paths)
    {
        _folder = Path.Combine(paths.Creations, "studio");
        Directory.CreateDirectory(_folder);
        _file = Path.Combine(_folder, "history.json");
        _entries = JsonFile.Read<List<StudioEntry>>(_file)?.Where(e => e is { Id: not null, Code: not null }).ToList() ?? [];
    }

    public IReadOnlyList<StudioEntry> All
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    /// <summary>Adds a creation (and its PNG thumbnail) at the top; drops the oldest beyond the capacity.</summary>
    public StudioEntry Add(StudioEntry entry, byte[]? thumbnailPng)
    {
        string? thumb = null;
        if (thumbnailPng is { Length: > 0 })
        {
            thumb = Path.Combine(_folder, entry.Id + ".png");
            File.WriteAllBytes(thumb, thumbnailPng);
        }
        var stored = entry with { ThumbnailPath = thumb };
        List<StudioEntry> dropped;
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == stored.Id);
            _entries.Insert(0, stored);
            dropped = _entries.Skip(Capacity).ToList();
            _entries = _entries.Take(Capacity).ToList();
            JsonFile.Write(_file, _entries);
        }
        foreach (var old in dropped) TryDelete(old.ThumbnailPath);
        return stored;
    }

    public void Remove(string id)
    {
        StudioEntry? removed;
        lock (_gate)
        {
            removed = _entries.FirstOrDefault(e => e.Id == id);
            if (removed is null) return;
            _entries.Remove(removed);
            JsonFile.Write(_file, _entries);
        }
        TryDelete(removed.ThumbnailPath);
    }

    public static string NewId() => "st-" + Guid.NewGuid().ToString("N")[..12];

    private void TryDelete(string? path)
    {
        if (path is null || !Path.GetFullPath(path).StartsWith(_folder, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Turns Studio creations into things Wavelit can show.</summary>
public static class GeneratedShaders
{
    /// <summary>A live wallpaper for the library. The thumbnail is copied next to the creations so history trimming can't remove it.</summary>
    public static WallpaperItem CreateItem(AppPaths paths, StudioEntry entry) => CreateItem(paths, entry, copyThumbnail: true);

    /// <summary>The custom-overlay id a Studio overlay is kept under.</summary>
    public static string OverlayId(StudioEntry entry) => InteractiveSettings.CustomPrefix + MediaCache.FileNameFor(entry.Code, "")[..12];

    public static WallpaperItem CreateItem(AppPaths paths, StudioEntry entry, bool copyThumbnail)
    {
        string id = "gshader-" + MediaCache.FileNameFor(entry.Code, "");
        return new WallpaperItem
        {
            Id = id,
            Kind = WallpaperKind.Shader,
            Source = WallpaperSources.GeneratedShader,
            Title = entry.Title,
            Author = AuthorLine(entry.Model),
            ShaderCode = entry.Code,
            Prompt = entry.Prompt,
            MadeWith = entry.Model is "" or "pasted" ? "ai-chat" : entry.Model,
            ThumbnailPath = copyThumbnail ? CopyThumbnail(paths, entry.ThumbnailPath, id) : null,
        };
    }

    /// <summary>An overlay for the Overlays gallery, keyed by a stable id from its code.</summary>
    public static (string Id, CustomOverlay Overlay) CreateOverlay(AppPaths paths, StudioEntry entry)
    {
        string id = OverlayId(entry);
        return (id, new CustomOverlay(entry.Title, entry.Code, entry.UsesPointer, CopyThumbnail(paths, entry.ThumbnailPath, id), entry.Prompt,
            entry.Params, entry.Model is "" or "pasted" ? "ai-chat" : entry.Model));
    }

    public static string AuthorLine(string model) => model switch
    {
        "" or "pasted" => "You, with an AI chat",
        _ when model.StartsWith("claude-", StringComparison.Ordinal) => "You, with Claude",
        _ => "You, with " + model,
    };

    private static string? CopyThumbnail(AppPaths paths, string? source, string id)
    {
        if (source is null || !File.Exists(source)) return null;
        string target = Path.Combine(paths.Creations, id + ".png");
        try
        {
            File.Copy(source, target, overwrite: true);
            return target;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
