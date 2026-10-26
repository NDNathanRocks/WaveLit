using System.Text.Json.Serialization;

namespace Wavelit.Core.Library;

[JsonConverter(typeof(JsonStringEnumConverter<DisplayLayout>))]
public enum DisplayLayout
{
    /// <summary>The same wallpaper on every display (each display renders it at its own resolution).</summary>
    Duplicate,
    /// <summary>Each display has its own wallpaper.</summary>
    PerDisplay,
    /// <summary>"Extend": one wallpaper continues across all displays as a single canvas (the default).</summary>
    Span,
}

/// <summary>Which wallpaper goes where. Immutable; changes produce a new instance.</summary>
public sealed record DisplayAssignments
{
    public DisplayLayout Layout { get; init; } = DisplayLayout.Span;
    /// <summary>Item used for Duplicate/Span, and the fallback for displays with no explicit item.</summary>
    public string? SharedItemId { get; init; }
    public Dictionary<string, string> PerDisplay { get; init; } = new();

    public string? ItemFor(string monitorId)
        => Layout == DisplayLayout.PerDisplay && PerDisplay.TryGetValue(monitorId, out var id) ? id : SharedItemId;

    /// <summary>Assigns <paramref name="itemId"/> to one display (switching to per-display mode) or to all.</summary>
    public DisplayAssignments Assign(string itemId, string? monitorId, IEnumerable<string> allMonitorIds)
    {
        if (monitorId is null)
        {
            // "All displays": keep "Same on every display" if the user chose it, otherwise extend.
            return this with
            {
                Layout = Layout == DisplayLayout.Duplicate ? DisplayLayout.Duplicate : DisplayLayout.Span,
                SharedItemId = itemId,
                PerDisplay = new(),
            };
        }

        var map = new Dictionary<string, string>(PerDisplay);
        if (Layout != DisplayLayout.PerDisplay && SharedItemId is not null)
        {
            // Entering per-display mode: the other displays keep what they were showing.
            foreach (var id in allMonitorIds) map.TryAdd(id, SharedItemId);
        }
        map[monitorId] = itemId;
        return this with { Layout = DisplayLayout.PerDisplay, PerDisplay = map, SharedItemId = SharedItemId ?? itemId };
    }

    public DisplayAssignments WithLayout(DisplayLayout layout, IEnumerable<string> allMonitorIds)
    {
        if (layout == Layout) return this;
        if (layout == DisplayLayout.PerDisplay)
        {
            var map = new Dictionary<string, string>(PerDisplay);
            if (SharedItemId is not null)
                foreach (var id in allMonitorIds) map.TryAdd(id, SharedItemId);
            return this with { Layout = layout, PerDisplay = map };
        }
        return this with { Layout = layout };
    }

    /// <summary>Drops references to an item that was removed from the library.</summary>
    public DisplayAssignments WithoutItem(string itemId, string? fallbackItemId)
    {
        var map = PerDisplay.Where(kv => kv.Value != itemId).ToDictionary(kv => kv.Key, kv => kv.Value);
        return this with
        {
            SharedItemId = SharedItemId == itemId ? fallbackItemId : SharedItemId,
            PerDisplay = map,
        };
    }

    public IEnumerable<string> ReferencedItemIds()
        => PerDisplay.Values.Append(SharedItemId).OfType<string>().Distinct();
}
