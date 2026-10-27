using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wavelit.Core.Desktop;

/// <summary>A taskbar look Wavelit can ask TranslucentTB for (plan §23.1, option A).</summary>
public sealed record TaskbarLook(string Id, string Name, string Blurb, string Accent, string Color);

/// <summary>
/// Works with TranslucentTB (free, open source, on the Microsoft Store) for a clear or glass taskbar. On Windows 11
/// that look needs code inside Explorer; TranslucentTB already does it well, so Wavelit doesn't inject anything:
/// it only edits the appearance keys of TranslucentTB's own settings.json, which TranslucentTB watches and applies
/// live. The user's file is backed up the first time and put back with <see cref="Restore"/>; every other key
/// (rules, ignored windows, language…) is left exactly as it was.
/// Format: TranslucentTB's settings.schema.json (desktop_appearance, visible_window_appearance,
/// maximized_window_appearance; accent = normal | opaque | clear | blur | acrylic; colour #RRGGBBAA).
/// </summary>
public sealed class TranslucentTb(string? settingsPath, string backupPath)
{
    public const string StoreProductId = "9PF4KZ2VN4W9";
    public const string StoreUri = "ms-windows-store://pdp/?ProductId=" + StoreProductId;
    public const string StoreWebUrl = "https://apps.microsoft.com/detail/" + StoreProductId;
    /// <summary>Package family prefix of the Store build (the suffix is the publisher hash).</summary>
    public const string PackagePrefix = "TranslucentTB_";

    public static readonly IReadOnlyList<TaskbarLook> Looks =
    [
        new("clear", "Clear", "Fully see-through: your wallpaper runs right to the bottom edge.", "clear", "#00000000"),
        new("tint", "Tinted", "See-through with a soft dark tint, so icons stay crisp on bright wallpapers.", "clear", "#0000004D"),
        new("glass", "Glass", "Frosted blur over the wallpaper.", "blur", "#00000020"),
        new("acrylic", "Acrylic", "Windows' own acrylic material, lightly tinted.", "acrylic", "#10101040"),
    ];

    /// <summary>The Store build's settings file (null when TranslucentTB isn't installed).</summary>
    public static string? FindSettings(string localAppData)
    {
        string packages = Path.Combine(localAppData, "Packages");
        if (!Directory.Exists(packages)) return null;
        var folder = Directory.EnumerateDirectories(packages, PackagePrefix + "*").FirstOrDefault();
        return folder is null ? null : Path.Combine(folder, "RoamingState", "settings.json");
    }

    public bool IsInstalled => settingsPath is not null;

    /// <summary>The look Wavelit applied last, if the file still says so (null = TranslucentTB's own settings).</summary>
    public string? CurrentLook()
    {
        if (Read() is not { } root || root["desktop_appearance"] is not JsonObject d) return null;
        string? accent = d["accent"]?.GetValue<string>(), color = d["color"]?.GetValue<string>();
        return Looks.FirstOrDefault(l => l.Accent == accent && string.Equals(l.Color, color, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <param name="solidWhenMaximized">Switch to Windows' normal taskbar while a window is maximized (easier to read).</param>
    public void Apply(TaskbarLook look, bool solidWhenMaximized)
    {
        if (settingsPath is null) throw new InvalidOperationException("TranslucentTB isn't installed.");
        var root = Read() ?? new JsonObject();
        if (!File.Exists(backupPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            // An empty marker means "there was no file": Restore then deletes ours.
            File.WriteAllText(backupPath, File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "");
        }
        JsonObject Appearance(string accent, string color) => new()
        {
            ["accent"] = accent,
            ["color"] = color,
            ["show_line"] = false,
            ["blur_radius"] = 9,
        };
        root["desktop_appearance"] = Appearance(look.Accent, look.Color);
        // A visible (not maximized) window: keep the same look, so the taskbar doesn't change as windows open.
        var visible = Section(root, "visible_window_appearance");
        visible["enabled"] = false;
        var maximized = Section(root, "maximized_window_appearance");
        maximized["enabled"] = solidWhenMaximized;
        maximized["accent"] = "acrylic";
        maximized["color"] = "#1A1A1ACC";
        maximized["show_line"] = false;
        Write(root);
    }

    /// <summary>Puts TranslucentTB's own settings back exactly as they were before Wavelit's first change.</summary>
    public void Restore()
    {
        if (settingsPath is null || !File.Exists(backupPath)) return;
        string original = File.ReadAllText(backupPath);
        if (original.Length == 0) File.Delete(settingsPath);
        else WriteText(original);
        File.Delete(backupPath);
    }

    public bool HasBackup => File.Exists(backupPath);

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject o) return o;
        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    private JsonObject? Read()
    {
        if (settingsPath is null || !File.Exists(settingsPath)) return null;
        try
        {
            // TranslucentTB's file allows comments and trailing commas.
            return JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;   // a broken file is replaced by a valid one (the original stays in the backup)
        }
    }

    private void Write(JsonObject root) => WriteText(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    // Atomic: TranslucentTB reloads the moment the file changes, so it must never see half a file.
    private void WriteText(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        string temp = settingsPath + ".wavelit.tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, settingsPath!, overwrite: true);
    }
}
