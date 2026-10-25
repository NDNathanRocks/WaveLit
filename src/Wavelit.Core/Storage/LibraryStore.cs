using Wavelit.Core.Library;
using Wavelit.Core.Library.BuiltIn;

namespace Wavelit.Core.Storage;

/// <summary>The user's saved wallpapers (built-ins are always present and never persisted).</summary>
public sealed class LibraryStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly List<WallpaperItem> _items;

    public LibraryStore(AppPaths paths)
    {
        _path = paths.LibraryFile;
        _items = JsonFile.Read<List<WallpaperItem>>(_path) ?? new();
        _items.RemoveAll(i => i.IsBuiltIn);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<WallpaperItem> All
    {
        get
        {
            lock (_gate)
                return BuiltInShaders.Items.Concat(_items.OrderByDescending(i => i.AddedAt)).ToList();
        }
    }

    public WallpaperItem? Find(string? id)
    {
        if (id is null) return null;
        if (BuiltInShaders.Find(id) is { } builtIn) return builtIn;
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }

    /// <summary>Adds or replaces an item (matched by id).</summary>
    public WallpaperItem Upsert(WallpaperItem item)
    {
        if (item.IsBuiltIn) return item;
        lock (_gate)
        {
            int idx = _items.FindIndex(i => i.Id == item.Id);
            if (idx >= 0) _items[idx] = item with { AddedAt = _items[idx].AddedAt };
            else _items.Add(item);
            JsonFile.Write(_path, _items);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.RemoveAll(i => i.Id == id) > 0;
            if (removed) JsonFile.Write(_path, _items);
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }
}
