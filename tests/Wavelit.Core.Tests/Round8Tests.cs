using System.Text.Json.Nodes;
using Wavelit.Core.Desktop;
using Wavelit.Core.Playback;

namespace Wavelit.Core.Tests;

/// <summary>Round 8 (plan §23): TranslucentTB looks, and covered wallpapers that wake without a flash.</summary>
public class Round8Tests
{
    private static (string Settings, string Backup) TempFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavelit-ttb-" + Guid.NewGuid().ToString("N"));
        return (Path.Combine(dir, "Packages", "TranslucentTB_abc123", "RoamingState", "settings.json"), Path.Combine(dir, "wavelit", "translucenttb-original.json"));
    }

    [Fact]
    public void Finds_the_store_build_settings()
    {
        var (settings, _) = TempFiles();
        string localAppData = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(settings))))!;
        Assert.Null(TranslucentTb.FindSettings(localAppData));
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        Assert.Equal(settings, TranslucentTb.FindSettings(localAppData));
        Assert.False(new TranslucentTb(null, "x").IsInstalled);
    }

    [Fact]
    public void A_look_changes_only_appearance_and_keeps_the_users_other_settings()
    {
        var (settings, backup) = TempFiles();
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        const string original = """
            {
              // the user's own file
              "desktop_appearance": { "accent": "normal", "color": "#FF000080" },
              "ignored_windows": { "process_name": ["game.exe"] },
              "maximized_window_appearance": { "enabled": false, "rules": { "process_name": { "x.exe": { "accent": "opaque" } } } },
              "language": "fr-FR",
            }
            """;
        File.WriteAllText(settings, original);
        var ttb = new TranslucentTb(settings, backup);
        var glass = TranslucentTb.Looks.Single(l => l.Id == "glass");
        ttb.Apply(glass, solidWhenMaximized: true);

        var json = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        Assert.Equal("blur", (string?)json["desktop_appearance"]!["accent"]);
        Assert.Equal("#00000020", (string?)json["desktop_appearance"]!["color"]);
        Assert.Equal("fr-FR", (string?)json["language"]);
        Assert.Equal("game.exe", (string?)json["ignored_windows"]!["process_name"]![0]);
        Assert.True((bool)json["maximized_window_appearance"]!["enabled"]!);
        Assert.NotNull(json["maximized_window_appearance"]!["rules"]);                 // the user's rules survive
        Assert.Equal("glass", ttb.CurrentLook());
        Assert.False(File.Exists(settings + ".wavelit.tmp"));

        // A second look keeps the first backup (the user's original, not Wavelit's look).
        ttb.Apply(TranslucentTb.Looks.Single(l => l.Id == "clear"), solidWhenMaximized: false);
        Assert.Equal(original, File.ReadAllText(backup));

        ttb.Restore();
        Assert.Equal(original, File.ReadAllText(settings));
        Assert.False(ttb.HasBackup);
        Assert.Null(ttb.CurrentLook());
    }

    [Fact]
    public void Restore_removes_a_file_that_did_not_exist_before()
    {
        var (settings, backup) = TempFiles();
        var ttb = new TranslucentTb(settings, backup);
        ttb.Apply(TranslucentTb.Looks[0], solidWhenMaximized: false);
        Assert.True(File.Exists(settings));
        ttb.Restore();
        Assert.False(File.Exists(settings));
    }

    [Fact]
    public void A_broken_settings_file_is_replaced_and_kept_in_the_backup()
    {
        var (settings, backup) = TempFiles();
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{ not json");
        new TranslucentTb(settings, backup).Apply(TranslucentTb.Looks[0], solidWhenMaximized: true);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(settings)));
        Assert.Equal("{ not json", File.ReadAllText(backup));
    }

    [Fact]
    public void Looks_use_valid_accents_and_colours()
    {
        foreach (var l in TranslucentTb.Looks)
        {
            Assert.Contains(l.Accent, new[] { "normal", "opaque", "clear", "blur", "acrylic" });
            Assert.Matches("^#[0-9A-Fa-f]{8}$", l.Color);
        }
    }

    // ---- covered wallpapers

    [Fact]
    public void Covered_wallpapers_freeze_first_and_suspend_only_after_the_grace()
    {
        var covered = new PlaybackDecision(PlaybackState.Suspended, "Covered by a maximized window", Covered: true);
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(PlaybackState.Frozen, CoverGrace.Apply(covered, null, now).State);
        Assert.Equal(PlaybackState.Frozen, CoverGrace.Apply(covered, now.AddSeconds(-3), now).State);
        Assert.Equal(PlaybackState.Suspended, CoverGrace.Apply(covered, now - CoverGrace.Delay, now).State);

        // Not covered (display off, locked, a fullscreen game) suspends at once.
        var off = new PlaybackDecision(PlaybackState.Suspended, "Display is off");
        Assert.Equal(PlaybackState.Suspended, CoverGrace.Apply(off, null, now).State);
    }
}
