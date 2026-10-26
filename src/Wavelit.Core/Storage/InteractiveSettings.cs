using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wavelit.Core.Storage;

/// <summary>
/// The overlay: a subtle particle or light layer over any wallpaper, most of them responding to the cursor
/// while the desktop is visible (plans §16.5, §17.4). Off by default. Chosen on the Overlays page; a wallpaper
/// can override the style with <c>WallpaperItem.Overlay</c>. (Named "interactive" in settings.json for
/// compatibility with round 3.)
/// </summary>
/// <param name="Style">One of <see cref="Styles"/>.</param>
/// <param name="Amount">0 = a few quiet specks … 1 = lively.</param>
/// <param name="Color">"auto" (each overlay's natural light) or "#RRGGBB".</param>
/// <param name="ClickBursts">A click on the desktop sends out a burst.</param>
/// <param name="Size">0 … 1 (smaller … larger particles).</param>
/// <param name="Speed">0 … 1 (slower … faster drift).</param>
/// <param name="Response">How strongly it reacts to the cursor: one of <see cref="Responses"/>.</param>
/// <param name="Opacity">0 … 1.</param>
public sealed partial record InteractiveSettings(
    string Style = InteractiveSettings.Off,
    double Amount = 0.35,
    string Color = "auto",
    bool ClickBursts = true,
    double Size = 0.5,
    double Speed = 0.5,
    string Response = InteractiveSettings.Gentle,
    double Opacity = 1.0)
{
    public const string Off = "off";
    public const string None = "none", Gentle = "gentle", Playful = "playful";

    /// <summary>Overlay ids; must match the STYLES in Web/interact.js (a test checks this).</summary>
    public static readonly IReadOnlyList<string> Styles =
    [
        Off, "stardust", "fireflies", "constellation", "ripples", "petals", "snowfall",
        "embers", "bokeh", "halo", "silk", "aurora", "cinematic",
    ];

    /// <summary>Overlays that never use the cursor.</summary>
    public static readonly IReadOnlyList<string> Ambient = ["cinematic"];

    public static readonly IReadOnlyList<string> Responses = [None, Gentle, Playful];

    [JsonIgnore] public bool IsOn => Style != Off;

    /// <summary>The overlay needs the cursor feed (on, cursor-aware style, response not "none").</summary>
    [JsonIgnore] public bool UsesPointer => IsOn && Response != None && !Ambient.Contains(Style);

    /// <summary>These settings with a wallpaper's own overlay style, if it has one.</summary>
    public InteractiveSettings For(string? itemOverlay)
        => itemOverlay is not null && Styles.Contains(itemOverlay) ? this with { Style = itemOverlay } : this;

    public InteractiveSettings Normalize() => this with
    {
        Style = Styles.Contains(Style) ? Style : Off,
        Amount = Unit(Amount, 0.35),
        Color = Color is not null && HexColor().IsMatch(Color) ? Color : "auto",
        Size = Unit(Size, 0.5),
        Speed = Unit(Speed, 0.5),
        Response = Responses.Contains(Response) ? Response : Gentle,
        Opacity = Unit(Opacity, 1.0),
    };

    private static double Unit(double v, double fallback) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : fallback;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
