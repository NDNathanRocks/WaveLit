using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Wavelit.Core.Create;
using Wavelit.Core.Create.Shaders;
using Wavelit.Core.Desktop;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.ViewModels;

/// <summary>The Studio page's live preview and lab (implemented by the page's WebView2).</summary>
public interface IStudioSurface : IShaderLab
{
    Task ShowAsync(ShaderKind kind, string code, bool usesPointer, object? layout, ShaderProgram? background, IReadOnlyList<double>? parameters);
    void ClearPreview();
    void SetParams(IReadOnlyList<double> values);
    double BudgetMs { get; set; }
}

/// <summary>One slider an AI shader offers (its "// @params" line).</summary>
public sealed partial class ParamSlider(ShaderParam param, double value, Action changed) : ObservableObject
{
    public ShaderParam Param { get; } = param;
    public string Label => Param.Label;
    public double Min => Param.Min;
    public double Max => Param.Max;
    public double Step => (Param.Max - Param.Min) / 100;

    [ObservableProperty] private double _value = value;

    partial void OnValueChanged(double value) => changed();
}

/// <summary>What the user is making. The Studio opens on the choice; each path shows only what it needs.</summary>
public enum StudioMode { Choose, Wallpaper, Overlay, Picture }

/// <summary>Two ways to an overlay: build one from choices (no key needed), or describe it to an AI.</summary>
public enum OverlayWay { Design, Describe }

public sealed record StudioModeCard(StudioMode Mode, string Title, string Blurb, string Footnote, Wpf.Ui.Controls.SymbolRegular Icon);

public sealed record StudioAuthorOption(string Id, string Label, string Description)
{
    public override string ToString() => Label;
}

/// <summary>A past creation in the history strip.</summary>
public sealed class StudioHistoryItem(StudioEntry entry)
{
    public StudioEntry Entry { get; } = entry;
    public ImageSource? Thumbnail { get; } = Load(entry.ThumbnailPath);
    public string Tooltip => $"{Entry.Title}: {(Entry.IsDesigned ? Entry.Design?.Describe() ?? "Overlay Designer" : Entry.Prompt)}";
    public string KindLabel => Entry.Kind == ShaderKind.Overlay ? "Overlay" : "Wallpaper";
    public override string ToString() => Entry.Title;

    internal static ImageSource? Load(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 384;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Studio (plan §19, reshaped in round 9): choose what to make (a live wallpaper, an overlay or a picture), then
/// one focused path. Overlays can be built in the Designer from choices alone (no key), or described to an AI.
/// Music is an option on either shader path, not a separate place. Nothing is applied until the user says so.
/// </summary>
public sealed partial class StudioViewModel : ObservableObject, IDisposable
{
    public const string PasteAuthorId = "paste";

    private readonly ShaderStudioServices _services;
    private readonly Random _random = new();
    private readonly DispatcherTimer _designTimer;
    private IStudioSurface? _surface;
    private CancellationTokenSource? _cts;
    private string? _keptDesignOverlayId;   // the overlay the last "Keep" created, replaced by the next Keep
    private bool _loadingEntry;
    private bool _disposed;

    [ObservableProperty] private StudioMode _mode = StudioMode.Choose;
    [ObservableProperty] private OverlayWay _overlayWay = OverlayWay.Design;
    [ObservableProperty] private bool _musicOn;
    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private Choice<Interactivity> _selectedInteractivity;
    [ObservableProperty] private Choice<string> _selectedPalette;
    [ObservableProperty] private Choice<MotionSpeed> _selectedSpeed;
    [ObservableProperty] private StudioAuthorOption _selectedAuthor;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _sendBackText;
    [ObservableProperty] private StudioEntry? _current;
    [ObservableProperty] private string _refineNote = "";
    [ObservableProperty] private bool _showCode;
    [ObservableProperty] private bool _bothScreens;
    [ObservableProperty] private string _pastedReply = "";
    [ObservableProperty] private bool _showOptions;
    // Recipe (plan §21.4): a fill-in sentence; free writing is the default.
    [ObservableProperty] private bool _useRecipe;
    [ObservableProperty] private string _recipeSubject = "";
    [ObservableProperty] private string _recipeMotion = "drifts";
    [ObservableProperty] private string _recipeMood = "calm";
    [ObservableProperty] private string _recipeLight = "";
    [ObservableProperty] private string _recipeDetail = "balanced";
    [ObservableProperty] private string _recipeAvoid = "";
    // Music (plan §20.4).
    [ObservableProperty] private Choice<string> _selectedReactsTo;
    [ObservableProperty] private Choice<string> _selectedReactsBy;
    // Overlay Designer.
    [ObservableProperty] private Choice<string> _designShape;
    [ObservableProperty] private Choice<string> _designMotion;
    [ObservableProperty] private Choice<string> _designCursor;
    [ObservableProperty] private double _designDensity = 50;
    [ObservableProperty] private double _designSize = 50;
    [ObservableProperty] private double _designSpeed = 50;
    [ObservableProperty] private double _designTwinkle = 30;
    [ObservableProperty] private double _designWind = 50;
    [ObservableProperty] private double _designGlow = 30;
    [ObservableProperty] private bool _isDesignLive;
    // Report (plan §21.6).
    [ObservableProperty] private bool _isReporting;
    [ObservableProperty] private Choice<string> _reportReason;
    [ObservableProperty] private string _reportNote = "";
    [ObservableProperty] private bool _reportIncludeDetails;
    [ObservableProperty] private string? _lastRunCost;

    public StudioViewModel(DashboardContext context, CreateViewModel picture, ShaderStudioServices services)
    {
        Context = context;
        Picture = picture;
        _services = services;
        _selectedReactsTo = ReactsToChoices[3];
        _selectedReactsBy = ReactsByChoices[0];
        _reportReason = ReportReasons[0];
        _selectedInteractivity = Interactivities[1];
        _selectedPalette = Palettes[0];
        _selectedSpeed = Speeds[1];
        _designShape = DesignShapes[0];
        _designMotion = DesignMotions[0];
        _designCursor = DesignCursors[0];
        // Best available by default: Claude with a key, then Cloudflare, then any chat AI.
        _selectedAuthor = services.Claude.IsConfigured ? Authors[0] : services.Cloudflare.IsConfigured ? Authors[1] : Authors[2];
        foreach (var e in services.History.All) History.Add(new StudioHistoryItem(e));
        _designTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, (sender, args) => { ((DispatcherTimer)sender!).Stop(); _ = ShowDesignAsync(); }, Dispatcher.CurrentDispatcher);
        _designTimer.Stop();
    }

    public DashboardContext Context { get; }
    public CreateViewModel Picture { get; }
    public ObservableCollection<StudioHistoryItem> History { get; } = [];
    /// <summary>The current result's sliders (empty when it declares none).</summary>
    public ObservableCollection<ParamSlider> Sliders { get; } = [];

    public IReadOnlyList<StudioModeCard> ModeCards { get; } =
    [
        new(StudioMode.Wallpaper, "Live wallpaper", "An animated scene written as a shader, for every monitor at once.", "Describe it, or let the AI surprise you.", Wpf.Ui.Controls.SymbolRegular.Sparkle24),
        new(StudioMode.Overlay, "Overlay", "Petals, snow, embers or anything else drifting over the wallpaper you already have.", "Build it from choices, no key needed, or describe it.", Wpf.Ui.Controls.SymbolRegular.Layer24),
        new(StudioMode.Picture, "Picture", "A still image shaped exactly like your screens, made on this PC or in the cloud.", "Then bring it to life with a motion effect.", Wpf.Ui.Controls.SymbolRegular.Image24),
    ];

    public static IReadOnlyList<string> RecipeSubjects => ShaderRecipe.Subjects;
    public static IReadOnlyList<string> RecipeMotions => ShaderRecipe.Motions;
    public static IReadOnlyList<string> RecipeMoods => ShaderRecipe.Moods;
    public static IReadOnlyList<string> RecipeLights => ShaderRecipe.Lights;
    public static IReadOnlyList<string> RecipeDetails => ShaderRecipe.Details;
    public static IReadOnlyList<Choice<string>> ReactsToChoices { get; } =
        [new("the bass", "bass"), new("the beat", "beat"), new("the melody", "melody"), new("everything", "all")];
    public static IReadOnlyList<Choice<string>> ReactsByChoices { get; } =
        [new("pulsing", "pulsing"), new("shaking", "shaking"), new("glowing", "glowing"), new("gathering into patterns", "gathering into patterns")];
    public static IReadOnlyList<Choice<string>> ReportReasons { get; } = ContentReport.Reasons.Select(r => new Choice<string>(r.Label, r.Id)).ToList();

    public static IReadOnlyList<Choice<string>> DesignShapes { get; } = OverlayDesign.Shapes.Select(s => new Choice<string>(s.Label, s.Id)).ToList();
    public static IReadOnlyList<Choice<string>> DesignMotions { get; } = OverlayDesign.Motions.Select(m => new Choice<string>(m.Label, m.Id)).ToList();
    public static IReadOnlyList<Choice<string>> DesignCursors { get; } = OverlayDesign.Cursors.Select(c => new Choice<string>(c.Label, c.Id)).ToList();

    public static IReadOnlyList<Choice<Interactivity>> Interactivities { get; } =
    [
        new("No cursor", Interactivity.None),
        new("Reacts to the cursor", Interactivity.Reacts),
        new("Playful", Interactivity.Playful),
    ];

    public static IReadOnlyList<Choice<string>> Palettes { get; } = ShaderPalettes.All.Select(p => new Choice<string>(p.Label, p.Id)).ToList();

    public static IReadOnlyList<Choice<MotionSpeed>> Speeds { get; } =
    [
        new("Still", MotionSpeed.Still),
        new("Calm", MotionSpeed.Calm),
        new("Lively", MotionSpeed.Lively),
    ];

    public IReadOnlyList<StudioAuthorOption> Authors { get; } =
    [
        new(ClaudeShaderAuthor.AuthorId, "Claude (your key, best)", "Anthropic's Claude writes it with your own API key: the best results, about 5 to 25 US cents per creation."),
        new(CloudflareShaderAuthor.AuthorId, "Cloudflare (free, simpler)", "A free open model on your Cloudflare account's daily allowance. Simpler results."),
        new(PasteAuthorId, "Any AI chat (free)", "Copy a ready-made prompt into ChatGPT, Claude.ai, Gemini or any chat AI, then paste its answer back."),
    ];

    public static IReadOnlyList<string> RefineIdeas { get; } = ["slower", "calmer", "more colourful", "darker", "fewer particles", "bigger shapes"];

    private static readonly IReadOnlyList<string> WallpaperIdeas =
    [
        "slow ink blooming in deep teal water",
        "neon rain running down a city window at night",
        "a calm gradient that breathes like a sleeping ocean",
        "soft glass prisms catching warm light",
        "fireflies over tall grass at dusk",
        "silky aurora ribbons over dark mountains",
    ];

    private static readonly IReadOnlyList<string> AudioIdeas =
    [
        "stardust that buzzes with the treble and gathers into patterns on held notes",
        "a circular spectrum ring that glows magenta to cyan and pulses with the bass",
        "slow ink that blooms on every kick drum",
        "an aurora that sways with the melody and brightens on the beat",
        "a field of neon grass that ripples with the music",
        "soft bokeh lights that breathe with the volume",
    ];
    private static readonly IReadOnlyList<string> OverlayIdeas =
    [
        "petals that scatter from the cursor",
        "tiny glowing pollen drifting upward",
        "golden dust that swirls around the cursor",
        "soft snow with a few big flakes up close",
        "slow light rays sweeping across the screen",
        "bubbles that wobble up and pop near the cursor",
    ];
    private static readonly IReadOnlyList<string> OverlayAudioIdeas =
    [
        "sparks that burst upward on every kick",
        "petals that fall faster with the beat",
        "soft bokeh that breathes with the volume",
        "stardust that buzzes with the treble",
    ];

    // ---------------------------------------------------------------- mode

    public bool IsChoosing => Mode == StudioMode.Choose;
    public bool IsPicture => Mode == StudioMode.Picture;
    public bool IsShaderTab => Mode is StudioMode.Wallpaper or StudioMode.Overlay;
    public bool IsWallpaperMode => Mode == StudioMode.Wallpaper;
    public bool IsOverlayMode => Mode == StudioMode.Overlay;
    public bool IsDesigning => IsOverlayMode && OverlayWay == OverlayWay.Design;
    public bool IsDescribing => IsShaderTab && !IsDesigning;
    /// <summary>The shader reacts to music (an option on both shader paths; the old "Music" tab).</summary>
    public bool IsAudioTab => IsShaderTab && MusicOn;
    public ShaderKind Kind => Mode == StudioMode.Overlay ? ShaderKind.Overlay : ShaderKind.Wallpaper;
    public string ModeTitle => Mode switch
    {
        StudioMode.Wallpaper => "Live wallpaper",
        StudioMode.Overlay => "Overlay",
        StudioMode.Picture => "Picture",
        _ => "Studio",
    };
    public IReadOnlyList<string> Ideas => Kind == ShaderKind.Overlay ? MusicOn ? OverlayAudioIdeas : OverlayIdeas : MusicOn ? AudioIdeas : WallpaperIdeas;
    public string Placeholder => Kind == ShaderKind.Overlay
        ? MusicOn ? "Describe a layer that moves to the music, e.g. sparks that burst upward on every kick"
                  : "Describe a layer for over any wallpaper, e.g. warm embers drifting up, scattering from the cursor"
        : MusicOn ? "Describe a music visualizer, e.g. stardust that buzzes with the treble and ripples on every kick"
                  : "Describe a live wallpaper, e.g. a slow lava lamp in sunset colours that follows the cursor";

    /// <summary>The recipe as plain English (what the AI is asked for).</summary>
    public string RecipeSentence => Recipe.Sentence(SelectedPalette.Label, SelectedInteractivity.Value)
        + (MusicOn ? $" It reacts to {SelectedReactsTo.Label} by {SelectedReactsBy.Label}." : "");

    private ShaderRecipe Recipe => new(RecipeSubject, RecipeMotion, RecipeMood, RecipeLight, RecipeDetail, RecipeAvoid);

    /// <summary>Free writing: which of what / how it moves / mood the prompt still lacks (keywords, no AI).</summary>
    public string? PromptHint
    {
        get
        {
            if (UseRecipe || Prompt.Trim().Length < 3) return null;
            string p = " " + Prompt.ToLowerInvariant() + " ";
            bool moves = ShaderRecipe.Motions.Concat(["drift", "swirl", "flow", "pulse", "fall", "bloom", "move", "sway", "wave", "rise", "spin", "float", "ripple", "breath", "burst", "scatter"]).Any(w => p.Contains(w));
            bool mood = ShaderRecipe.Moods.Concat(["soft", "warm", "cold", "dark", "bright", "gentle", "slow", "neon", "dreamy", "cozy", "eerie", "serene", "golden", "glowing"]).Any(w => p.Contains(w));
            var missing = new List<string>();
            if (Prompt.Trim().Split(' ').Length < 3) missing.Add("what it is");
            if (!moves) missing.Add("how it moves");
            if (!mood) missing.Add("the mood");
            return missing.Count == 0 ? null : "Tip: good prompts name what it is, how it moves and the mood. Add " + string.Join(" and ", missing) + ".";
        }
    }

    public bool HasSliders => Sliders.Count > 0;
    /// <summary>An AI result: labelled "Made with AI" and reportable. Designer results are the user's own work.</summary>
    public bool IsAiResult => Current is { IsDesigned: false };
    public bool CanRefineResult => HasResult && Current is { IsDesigned: false };

    public bool IsPaste => SelectedAuthor.Id == PasteAuthorId;
    public bool NeedsSetup => SelectedAuthor.Id switch
    {
        ClaudeShaderAuthor.AuthorId => !_services.Claude.IsConfigured,
        CloudflareShaderAuthor.AuthorId => !_services.Cloudflare.IsConfigured,
        _ => false,
    };
    public string SetupText => SelectedAuthor.Id == ClaudeShaderAuthor.AuthorId
        ? "Add your Anthropic API key in Settings → Studio to use Claude."
        : "Add your free Cloudflare account ID and API token in Settings → Studio.";
    /// <summary>One line under the Create button: who writes it, so the choice is visible without opening Options.</summary>
    public string AuthorLine => IsPaste ? "Written by any AI chat (copy and paste, free)" : $"Written by {SelectedAuthor.Label}";

    public bool HasResult => Current is not null;
    public bool IsOverlayResult => Current?.Kind == ShaderKind.Overlay;
    public bool IsWallpaperResult => Current?.Kind == ShaderKind.Wallpaper;
    public bool HasSeveralScreens => Context.Snapshot.Monitors.Count > 1;
    public bool HasHistory => History.Count > 0;
    public string SpeedBadge => Current is null ? "" : Current.FrameMs switch
    {
        < 0.1 => "Very light · under 0.1 ms",
        < 1.0 => $"Very light · {Current.FrameMs:0.0} ms",
        < 2.5 => $"Light · {Current.FrameMs:0.0} ms",
        _ => $"Moderate · {Current.FrameMs:0.0} ms",
    };

    /// <summary>The paste-ready prompt for any chat AI (with the current result when refining).</summary>
    public string ChatPrompt => ShaderPrompt.ChatPrompt(BuildRequest(refine: Current is { IsDesigned: false } && RefineNote.Trim().Length > 0));

    private static readonly string[] ModeProperties =
    [
        nameof(IsChoosing), nameof(IsPicture), nameof(IsShaderTab), nameof(IsWallpaperMode), nameof(IsOverlayMode), nameof(IsDesigning),
        nameof(IsDescribing), nameof(IsAudioTab), nameof(Kind), nameof(ModeTitle), nameof(Ideas), nameof(Placeholder), nameof(ChatPrompt), nameof(RecipeSentence),
    ];

    partial void OnModeChanged(StudioMode value)
    {
        foreach (var name in ModeProperties) OnPropertyChanged(name);
        SyncRecipePrompt();
        Message = null;
        if (value == StudioMode.Picture) Picture.Activate();
        if (IsDesigning) RequestDesign();
        else if (!_loadingEntry && IsShaderTab && Current is { } c && c.Kind != Kind) Current = null;   // a wallpaper result doesn't belong on the overlay path
        else _ = ShowCurrentAsync();
    }

    partial void OnOverlayWayChanged(OverlayWay value)
    {
        OnPropertyChanged(nameof(IsDesigning));
        OnPropertyChanged(nameof(IsDescribing));
        Message = null;
        if (IsDesigning) RequestDesign();
        else { IsDesignLive = false; _ = ShowCurrentAsync(); }
    }

    partial void OnMusicOnChanged(bool value)
    {
        foreach (var name in new[] { nameof(IsAudioTab), nameof(Ideas), nameof(Placeholder), nameof(ChatPrompt), nameof(RecipeSentence) }) OnPropertyChanged(name);
        SyncRecipePrompt();
        if (IsDesigning) RequestDesign();
    }

    partial void OnSelectedAuthorChanged(StudioAuthorOption value)
    {
        foreach (var name in new[] { nameof(IsPaste), nameof(NeedsSetup), nameof(SetupText), nameof(AuthorLine) }) OnPropertyChanged(name);
        Message = null;
        SendBackText = null;
    }

    partial void OnPromptChanged(string value) { CreateCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ChatPrompt)); OnPropertyChanged(nameof(PromptHint)); }
    partial void OnSelectedInteractivityChanged(Choice<Interactivity> value) { OnPropertyChanged(nameof(ChatPrompt)); SyncRecipePrompt(); }
    partial void OnSelectedPaletteChanged(Choice<string> value) { OnPropertyChanged(nameof(ChatPrompt)); SyncRecipePrompt(); }
    partial void OnUseRecipeChanged(bool value) { SyncRecipePrompt(); OnPropertyChanged(nameof(PromptHint)); }
    partial void OnRecipeSubjectChanged(string value) => SyncRecipePrompt();
    partial void OnRecipeMotionChanged(string value) => SyncRecipePrompt();
    partial void OnRecipeMoodChanged(string value) => SyncRecipePrompt();
    partial void OnRecipeLightChanged(string value) => SyncRecipePrompt();
    partial void OnRecipeDetailChanged(string value) => SyncRecipePrompt();
    partial void OnRecipeAvoidChanged(string value) => SyncRecipePrompt();
    partial void OnSelectedReactsToChanged(Choice<string> value) { SyncRecipePrompt(); OnPropertyChanged(nameof(ChatPrompt)); }
    partial void OnSelectedReactsByChanged(Choice<string> value) { SyncRecipePrompt(); OnPropertyChanged(nameof(ChatPrompt)); }

    // In recipe mode the sentence is the idea (the slots also go to the AI as labelled lines).
    private void SyncRecipePrompt()
    {
        OnPropertyChanged(nameof(RecipeSentence));
        if (UseRecipe) Prompt = Recipe.IsEmpty ? "" : RecipeSentence;
    }
    partial void OnSelectedSpeedChanged(Choice<MotionSpeed> value) => OnPropertyChanged(nameof(ChatPrompt));
    partial void OnRefineNoteChanged(string value) { RefineCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ChatPrompt)); }
    partial void OnIsBusyChanged(bool value)
    {
        CreateCommand.NotifyCanExecuteChanged();
        RefineCommand.NotifyCanExecuteChanged();
        CheckPastedCommand.NotifyCanExecuteChanged();
        KeepDesignCommand.NotifyCanExecuteChanged();
    }
    partial void OnBothScreensChanged(bool value) { if (IsDesignLive) RequestDesign(); else _ = ShowCurrentAsync(); }

    partial void OnCurrentChanged(StudioEntry? value)
    {
        Sliders.Clear();
        if (value is not null)
        {
            var declared = ShaderParams.Parse(value.Code);
            var values = ShaderParams.Clamp(declared, value.Params);
            for (int i = 0; i < declared.Count; i++) Sliders.Add(new ParamSlider(declared[i], values[i], OnSliderChanged));
        }
        IsReporting = false;
        foreach (var name in new[] { nameof(HasResult), nameof(IsOverlayResult), nameof(IsWallpaperResult), nameof(SpeedBadge), nameof(ChatPrompt), nameof(HasSliders), nameof(IsAiResult), nameof(CanRefineResult) })
            OnPropertyChanged(name);
        RefineCommand.NotifyCanExecuteChanged();
        if (value is not null) IsDesignLive = false;
        _ = ShowCurrentAsync();
    }

    public AppPaths Paths => _services.Paths;

    public void ShowPreviewProblem(string message) => Message = message;

    /// <summary>Called by the page when it appears.</summary>
    public void Activate()
    {
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(HasSeveralScreens));
        if (IsPicture) Picture.Activate();
    }

    /// <summary>The page's WebView2 (null when the page is not shown).</summary>
    public void Attach(IStudioSurface? surface)
    {
        // The window disposes view models before its pages unload.
        if (_disposed) return;
        _surface = surface;
        if (surface is null) _cts?.Cancel();   // the lab is gone; nothing can be checked
        else if (IsDesigning && Current is null) RequestDesign();
        else _ = ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        if (_surface is not { } surface || IsDesignLive) return;
        try
        {
            if (Current is not { } c)
            {
                surface.ClearPreview();
                return;
            }
            surface.BudgetMs = ShaderStudio.BudgetFor(Context.Settings.Current);
            await surface.ShowAsync(c.Kind, c.Code, c.UsesPointer, BothScreens && HasSeveralScreens ? ExtendLayout() : null,
                c.Kind == ShaderKind.Overlay ? Controls.StudioPreview.OverlayBackground : null, SliderValues());
        }
        catch (Exception ex) when (IsPreviewFailure(ex) || ex is OperationCanceledException)
        {
            Log.Warning(ex, "Studio preview failed to start");
            Message = "The preview couldn't start. Set it as your wallpaper to see it, or restart Wavelit.";
        }
    }

    /// <summary>The user's real Extend canvas, for the two-screen preview (canvas units, as the wallpaper uses).</summary>
    private object? ExtendLayout()
    {
        var monitors = Context.Snapshot.Monitors;
        if (monitors.Count < 2) return null;
        var canvas = CanvasLayout.Extend(monitors, Context.Settings.Current.Canvas);
        var whole = canvas.Whole;
        return new
        {
            canvas = new { width = whole.CanvasWidth, height = whole.CanvasHeight },
            slices = canvas.Slices.Values.Select(v => new { x = v.Viewport.Left, y = v.Viewport.Top, width = v.Viewport.Width, height = v.Viewport.Height }).ToList(),
            focus = new { x = whole.FocusX, y = whole.FocusY, size = whole.FocusSize },
        };
    }

    // ---------------------------------------------------------------- Overlay Designer

    /// <summary>The design as chosen right now.</summary>
    public OverlayDesign Design => new(DesignShape.Value, DesignMotion.Value, DesignCursor.Value,
        DesignDensity / 100, DesignSize / 100, DesignSpeed / 100, DesignTwinkle / 100, DesignWind / 100, DesignGlow / 100, MusicOn);

    public string DesignTitle => Design.Title;
    public string DesignDescription => Design.Describe();

    partial void OnDesignShapeChanged(Choice<string> value) => RequestDesign();
    partial void OnDesignMotionChanged(Choice<string> value) => RequestDesign();
    partial void OnDesignCursorChanged(Choice<string> value) => RequestDesign();
    partial void OnDesignDensityChanged(double value) => RequestDesign();
    partial void OnDesignSizeChanged(double value) => RequestDesign();
    partial void OnDesignSpeedChanged(double value) => RequestDesign();
    partial void OnDesignTwinkleChanged(double value) => RequestDesign();
    partial void OnDesignWindChanged(double value) => RequestDesign();
    partial void OnDesignGlowChanged(double value) => RequestDesign();

    // Slider drags arrive many times a second; the preview is rebuilt once they settle.
    private void RequestDesign()
    {
        if (!IsDesigning || _disposed || _loadingEntry) return;
        OnPropertyChanged(nameof(DesignTitle));
        OnPropertyChanged(nameof(DesignDescription));
        _designTimer.Stop();
        _designTimer.Start();
    }

    private async Task ShowDesignAsync()
    {
        if (_surface is not { } surface || !IsDesigning || _disposed) return;
        var design = Design;
        try
        {
            // A new design replaces whatever result was showing; the design is the preview now.
            if (Current is not null) { Current = null; }
            IsDesignLive = true;
            await surface.ShowAsync(ShaderKind.Overlay, design.ToGlsl(), design.UsesPointer, BothScreens && HasSeveralScreens ? ExtendLayout() : null,
                Controls.StudioPreview.OverlayBackground, null);
        }
        catch (Exception ex) when (IsPreviewFailure(ex) || ex is OperationCanceledException)
        {
            Log.Warning(ex, "Studio designer preview failed");
            Message = "The preview couldn't start. Keep the overlay to see it on your desktop, or restart Wavelit.";
        }
    }

    [RelayCommand]
    private void SurpriseDesign()
    {
        DesignShape = DesignShapes[_random.Next(DesignShapes.Count)];
        DesignMotion = DesignMotions[_random.Next(DesignMotions.Count)];
        DesignCursor = DesignCursors[_random.Next(DesignCursors.Count - 1)];
        DesignDensity = _random.Next(25, 80);
        DesignSize = _random.Next(25, 80);
        DesignSpeed = _random.Next(20, 80);
        DesignTwinkle = _random.Next(0, 70);
        DesignWind = _random.Next(10, 90);
        DesignGlow = _random.Next(0, 70);
    }

    private bool CanKeepDesign() => !IsBusy;

    /// <summary>Checks the design in the lab like any shader (compile, time, blank test, thumbnail), keeps it and turns it on.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepDesign))]
    private async Task KeepDesign()
    {
        if (_surface is null) { Message = "The Studio preview isn't ready yet. Try again in a moment."; return; }
        var design = Design;
        string code = design.ToGlsl();
        IsBusy = true;
        Stage = "Checking it runs smoothly…";
        Message = null;
        try
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _surface.BudgetMs = ShaderStudio.BudgetFor(Context.Settings.Current);
            var check = await new ShaderStudio(_surface, () => _surface?.BudgetMs ?? ShaderStudio.DefaultBudgetMs).CheckAsync(code, ShaderKind.Overlay, _cts.Token);
            if (!check.Passed)
            {
                Message = check.Failure == CheckFailure.TooSlow
                    ? "That's too heavy for this PC's graphics card. Try fewer particles or less glow."
                    : "That design couldn't be shown: " + check.Problem;
                return;
            }
            var request = new ShaderRequest(ShaderKind.Overlay, design.Describe(), design.UsesPointer ? Interactivity.Reacts : Interactivity.None);
            var draft = new ShaderDraft(design.Title, design.Describe(), code, "nature", design.UsesPointer);
            // Keep replaces the overlay the last Keep made (tweaking shouldn't fill the gallery with near-duplicates).
            string? previous = _keptDesignOverlayId;
            Keep(draft, check.Report!, request, OverlayDesign.Author, parentId: null, design);
            var (id, overlay) = GeneratedShaders.CreateOverlay(_services.Paths, CurrentWithParams!);
            string? replacedThumb = null;
            Context.Settings.Update(s =>
            {
                var custom = new Dictionary<string, CustomOverlay>(s.CustomOverlays);
                if (previous is not null && previous != id && s.Interactive.Style == previous && custom.Remove(previous, out var old))
                    replacedThumb = old.ThumbnailPath;
                custom[id] = overlay;
                return s with { CustomOverlays = custom, Interactive = s.Interactive with { Style = id } };
            });
            if (replacedThumb is not null && replacedThumb != overlay.ThumbnailPath
                && Path.GetFullPath(replacedThumb).StartsWith(_services.Paths.Creations + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(replacedThumb); } catch (IOException) { }
            }
            _keptDesignOverlayId = id;
            Context.Notify("Overlay on", $"“{overlay.Title}” is over your wallpaper. Keep tweaking here, or fine-tune it on the Overlays page.", NoticeLevel.Success);
        }
        catch (OperationCanceledException)
        {
            Message = "Stopped.";
        }
        catch (Exception ex) when (IsPreviewFailure(ex))
        {
            Log.Warning(ex, "Studio: the preview couldn't check the design");
            Message = PreviewDown;
        }
        finally
        {
            IsBusy = false;
            Stage = "";
        }
    }

    private void LoadDesign(OverlayDesign d)
    {
        d = d.Normalize();
        DesignShape = DesignShapes.First(c => c.Value == d.Shape);
        DesignMotion = DesignMotions.First(c => c.Value == d.Motion);
        DesignCursor = DesignCursors.First(c => c.Value == d.Cursor);
        (DesignDensity, DesignSize, DesignSpeed, DesignTwinkle, DesignWind, DesignGlow) =
            (d.Density * 100, d.Size * 100, d.Speed * 100, d.Twinkle * 100, d.Wind * 100, d.Glow * 100);
        MusicOn = d.Music;
    }

    // ---------------------------------------------------------------- describe (AI)

    private ShaderRequest BuildRequest(bool refine) => new(
        Kind, Prompt.Trim(), SelectedInteractivity.Value, SelectedPalette.Value, SelectedSpeed.Value,
        refine ? Current?.Code : null, refine ? RefineNote.Trim() : null,
        UseRecipe && !Recipe.IsEmpty ? Recipe : null,
        MusicOn ? new MusicReaction(SelectedReactsTo.Value, SelectedReactsBy.Value) : null);

    private double[]? SliderValues() => Sliders.Count == 0 ? null : Sliders.Select(s => s.Value).ToArray();

    // A slider moved: the preview follows at once; anything already applied from this result follows too.
    private void OnSliderChanged()
    {
        if (Current is not { } c || SliderValues() is not { } values) return;
        _surface?.SetParams(values);
        string itemId = GeneratedShaders.CreateItem(_services.Paths, c, copyThumbnail: false).Id;
        var settings = Context.Settings.Current;
        bool isWallpaper = Context.Library.Find(itemId) is not null;
        string overlayId = GeneratedShaders.OverlayId(c);
        bool isOverlay = settings.CustomOverlays.ContainsKey(overlayId);
        if (!isWallpaper && !isOverlay) return;
        Context.Settings.Update(s => s with
        {
            ParamsFor = isWallpaper ? new Dictionary<string, double[]>(s.ParamsFor) { [itemId] = values } : s.ParamsFor,
            CustomOverlays = isOverlay && s.CustomOverlays.TryGetValue(overlayId, out var o)
                ? new Dictionary<string, CustomOverlay>(s.CustomOverlays) { [overlayId] = o with { Params = values } }
                : s.CustomOverlays,
        });
    }

    /// <summary>The current result with the sliders as they are now.</summary>
    private StudioEntry? CurrentWithParams => Current is { } c ? c with { Params = SliderValues() } : null;

    private IShaderAuthor? Author => SelectedAuthor.Id switch
    {
        ClaudeShaderAuthor.AuthorId => _services.Claude,
        CloudflareShaderAuthor.AuthorId => _services.Cloudflare,
        _ => null,
    };

    [RelayCommand]
    private void Choose(StudioMode mode) => Mode = mode;

    [RelayCommand]
    private void Back()
    {
        _cts?.Cancel();
        Mode = StudioMode.Choose;
    }

    [RelayCommand]
    private void ChooseOverlayWay(OverlayWay way) => OverlayWay = way;

    [RelayCommand]
    private void UseIdea(string idea) => Prompt = idea;

    [RelayCommand]
    private void SurpriseMe()
    {
        if (UseRecipe)
        {
            var r = ShaderRecipe.Surprise(_random);
            (RecipeSubject, RecipeMotion, RecipeMood, RecipeLight, RecipeDetail) = (r.Subject!, r.Motion!, r.Mood!, r.Light!, r.Detail!);
        }
        else Prompt = Ideas[_random.Next(Ideas.Count)];
        SelectedPalette = Palettes[_random.Next(Palettes.Count)];
        SelectedInteractivity = Interactivities[_random.Next(1, Interactivities.Count)];
        SelectedSpeed = Speeds[_random.Next(1, Speeds.Count)];
    }

    [RelayCommand]
    private void UseRefineIdea(string idea) => RefineNote = idea;

    [RelayCommand]
    private void OpenSettings() => Context.RequestNavigation("Settings");

    private bool CanCreate() => !IsBusy && Prompt.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task Create() => RunAsync(BuildRequest(refine: false), parentId: null);

    private bool CanRefine() => !IsBusy && Current is { IsDesigned: false } && RefineNote.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanRefine))]
    private Task Refine()
    {
        var c = Current!;
        // Refine what's showing, with its own options and idea.
        var request = c.Request with { Previous = c.Code, RefineNote = RefineNote.Trim() };
        return RunAsync(request, c.Id);
    }

    private async Task RunAsync(ShaderRequest request, string? parentId)
    {
        if (IsPaste)
        {
            Message = "Copy the prompt below into your AI chat, then paste its answer underneath.";
            return;
        }
        if (NeedsSetup || Author is not { } author) return;
        if (_surface is null)
        {
            Message = "The Studio preview isn't ready yet. Try again in a moment.";
            return;
        }
        if (_services.Guard.IsBlocked(request.Prompt) || _services.Guard.IsBlocked(request.RefineNote))
        {
            Message = "That prompt isn't allowed. Wavelit only makes wallpapers that are safe for work.";
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Message = null;
        SendBackText = null;
        Stage = "Starting…";
        var progress = new Progress<StudioProgress>(p => Stage = p.Stage);
        try
        {
            _surface.BudgetMs = ShaderStudio.BudgetFor(Context.Settings.Current);
            var outcome = await new ShaderStudio(_surface, () => _surface?.BudgetMs ?? ShaderStudio.DefaultBudgetMs).CreateAsync(author, request, progress, _cts.Token);
            if (outcome.Usage is { } u)
            {
                Log.Information("Studio: {Model}, {Rounds} fix round(s), tokens in {In} (cache read {Read}, write {Write}), out {Out}",
                    author.Model, outcome.FixRounds, u.InputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.OutputTokens);
                LastRunCost = $"Last run: {(u.InputTokens + u.CacheReadTokens + u.CacheWriteTokens) / 1000.0:0.#}k tokens in, {u.OutputTokens / 1000.0:0.#}k out"
                    + (outcome.FixRounds > 0 ? $", {outcome.FixRounds} fix round{(outcome.FixRounds == 1 ? "" : "s")}" : "") + ". Your Anthropic console shows the exact cost.";
            }
            if (!outcome.Succeeded)
            {
                Message = outcome.Problem;
                return;
            }
            Keep(outcome.Draft!, outcome.Report!, request, author.Model, parentId, design: null);
        }
        catch (OperationCanceledException)
        {
            Message = "Stopped.";
        }
        catch (GenerationException ex)
        {
            Message = ex.Message;
        }
        catch (ShaderAnswerException ex)
        {
            Message = "The AI's answer couldn't be used: " + ex.Message;
        }
        catch (Exception ex) when (IsPreviewFailure(ex))
        {
            Log.Warning(ex, "Studio: the preview couldn't check the shader");
            Message = PreviewDown;
        }
        finally
        {
            IsBusy = false;
            Stage = "";
        }
    }

    private bool CanCheckPasted() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheckPasted))]
    private async Task CheckPasted()
    {
        if (_surface is null) return;
        Message = null;
        SendBackText = null;
        ShaderDraft draft;
        try
        {
            draft = ShaderResult.ParseText(PastedReply);
        }
        catch (ShaderAnswerException ex)
        {
            Message = ex.Message;
            return;
        }
        bool refine = Current is { IsDesigned: false } && RefineNote.Trim().Length > 0;
        var request = BuildRequest(refine);
        IsBusy = true;
        Stage = "Checking it runs smoothly…";
        try
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _surface.BudgetMs = ShaderStudio.BudgetFor(Context.Settings.Current);
            var check = await new ShaderStudio(_surface, () => _surface?.BudgetMs ?? ShaderStudio.DefaultBudgetMs).CheckAsync(draft.Code, request.Kind, _cts.Token);
            if (!check.Passed)
            {
                Message = check.Failure == CheckFailure.Compile ? "That code has an error. Send this back to your AI chat:" : "That shader didn't pass the check. Send this back to your AI chat:";
                SendBackText = ShaderPrompt.ChatFixMessage(check.Problem!);
                return;
            }
            string title = draft.Title == "Untitled" ? TitleFrom(request.Prompt) : draft.Title;
            Keep(draft with { Title = title }, check.Report!, request, "pasted", refine ? Current!.Id : null, design: null);
            PastedReply = "";
        }
        catch (OperationCanceledException)
        {
            Message = "Stopped.";
        }
        catch (Exception ex) when (IsPreviewFailure(ex))
        {
            Log.Warning(ex, "Studio: the preview couldn't check the shader");
            Message = PreviewDown;
        }
        finally
        {
            IsBusy = false;
            Stage = "";
        }
    }

    private const string PreviewDown = "The Studio preview couldn't start, so the shader can't be checked yet. Try again in a moment.";

    private static bool IsPreviewFailure(Exception ex) => ex is TimeoutException or InvalidOperationException
        or System.Runtime.InteropServices.COMException or Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException;

    private static string TitleFrom(string prompt)
    {
        var words = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4).ToArray();
        if (words.Length == 0) return "My shader";
        string t = string.Join(' ', words);
        return char.ToUpperInvariant(t[0]) + t[1..];
    }

    private void Keep(ShaderDraft draft, LabReport report, ShaderRequest request, string model, string? parentId, OverlayDesign? design)
    {
        var entry = _services.History.Add(new StudioEntry
        {
            Id = StudioHistory.NewId(),
            Kind = request.Kind,
            Prompt = request.Prompt,
            Interactivity = request.Interactivity,
            Palette = request.Palette,
            Speed = request.Speed,
            Title = draft.Title,
            Description = draft.Description,
            Code = draft.Code,
            Mood = draft.Mood,
            UsesPointer = draft.UsesPointer,
            Model = model,
            FrameMs = report.FrameMs,
            ParentId = parentId,
            Recipe = request.Recipe,
            Music = request.Music,
            Design = design,
        }, report.Thumbnail);
        History.Insert(0, new StudioHistoryItem(entry));
        while (History.Count > StudioHistory.Capacity) History.RemoveAt(History.Count - 1);
        OnPropertyChanged(nameof(HasHistory));
        RefineNote = "";
        ShowCode = false;
        IsDesignLive = false;
        Current = entry;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenHistory(StudioHistoryItem? item)
    {
        if (item is null || IsBusy) return;
        var e = item.Entry;
        _loadingEntry = true;
        try
        {
            Mode = e.Kind == ShaderKind.Overlay ? StudioMode.Overlay : StudioMode.Wallpaper;
            if (e.Design is { } d) { OverlayWay = OverlayWay.Design; LoadDesign(d); }
            else
            {
                OverlayWay = OverlayWay.Describe;
                LoadRecipe(e);
                Prompt = e.Prompt;
                SelectedInteractivity = Interactivities.FirstOrDefault(i => i.Value == e.Interactivity) ?? Interactivities[0];
                SelectedPalette = Palettes.FirstOrDefault(p => p.Value == e.Palette) ?? Palettes[0];
                SelectedSpeed = Speeds.FirstOrDefault(s => s.Value == e.Speed) ?? Speeds[1];
            }
        }
        finally
        {
            _loadingEntry = false;
        }
        _designTimer.Stop();
        Message = null;
        ShowCode = false;
        IsDesignLive = false;
        Current = e;
    }

    [RelayCommand]
    private void RemoveHistory(StudioHistoryItem? item)
    {
        if (item is null) return;
        _services.History.Remove(item.Entry.Id);
        History.Remove(item);
        OnPropertyChanged(nameof(HasHistory));
        if (Current?.Id == item.Entry.Id) Current = null;
    }

    /// <summary>Loads a past creation's idea and options (or design) to make a variation, without opening it.</summary>
    [RelayCommand]
    private void Remix(StudioHistoryItem? item)
    {
        if (item is null || IsBusy) return;
        var e = item.Entry;
        _loadingEntry = true;
        try
        {
            Mode = e.Kind == ShaderKind.Overlay ? StudioMode.Overlay : StudioMode.Wallpaper;
            if (e.Design is { } d) { OverlayWay = OverlayWay.Design; LoadDesign(d); }
            else
            {
                OverlayWay = OverlayWay.Describe;
                SelectedInteractivity = Interactivities.FirstOrDefault(i => i.Value == e.Interactivity) ?? Interactivities[0];
                SelectedPalette = Palettes.FirstOrDefault(p => p.Value == e.Palette) ?? Palettes[0];
                SelectedSpeed = Speeds.FirstOrDefault(s => s.Value == e.Speed) ?? Speeds[1];
                LoadRecipe(e);
                if (!UseRecipe) Prompt = e.Prompt;
            }
        }
        finally
        {
            _loadingEntry = false;
        }
        if (IsDesigning) RequestDesign();
        Message = e.Design is null ? "Loaded “" + e.Title + "”. Change anything, then Create for a new variation." : null;
    }

    private void LoadRecipe(StudioEntry e)
    {
        MusicOn = e.Music is not null;
        if (e.Music is { } m)
        {
            SelectedReactsTo = ReactsToChoices.FirstOrDefault(c => c.Value == m.ReactsTo) ?? ReactsToChoices[3];
            SelectedReactsBy = ReactsByChoices.FirstOrDefault(c => c.Value == m.ReactsBy) ?? ReactsByChoices[0];
        }
        if (e.Recipe is { } r)
        {
            UseRecipe = true;
            (RecipeSubject, RecipeMotion, RecipeMood, RecipeLight, RecipeDetail, RecipeAvoid) =
                (r.Subject ?? "", r.Motion ?? "", r.Mood ?? "", r.Light ?? "", r.Detail ?? "", r.Avoid ?? "");
        }
        else
        {
            UseRecipe = false;
            Prompt = e.Prompt;
        }
    }

    [RelayCommand]
    private void StartReport()
    {
        ReportNote = "";
        ReportIncludeDetails = false;
        ReportReason = ReportReasons[0];
        IsReporting = Current is { IsDesigned: false };
    }

    /// <summary>Opens a pre-filled GitHub issue in the browser; Wavelit itself sends nothing.</summary>
    [RelayCommand]
    private void SendReport()
    {
        if (Current is not { } c) return;
        string? details = ReportIncludeDetails ? $"Prompt: {c.Prompt}\n\n{c.Code}" : null;
        string url = ContentReport.IssueUrl(ReportReason.Value, ReportNote, c.Title, GeneratedShaders.MadeWith(c.Model) ?? "ai-chat", details,
            typeof(StudioViewModel).Assembly.GetName().Version?.ToString() ?? "dev");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            Context.Notify("Report opened", "Your browser shows the report. Nothing is sent until you submit it there. Thank you.", NoticeLevel.Success);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "Couldn't open the report page");
            Context.Notify("Couldn't open your browser", ContentReport.IssuesUrl, NoticeLevel.Warning);
        }
        IsReporting = false;
    }

    [RelayCommand]
    private void CancelReport() => IsReporting = false;

    [RelayCommand]
    private void SetAsWallpaper()
    {
        if (CurrentWithParams is not { Kind: ShaderKind.Wallpaper } c) return;
        var item = Context.Library.Upsert(GeneratedShaders.CreateItem(_services.Paths, c));
        if (c.Params is { } chosen)
            Context.Settings.Update(s => s with { ParamsFor = new Dictionary<string, double[]>(s.ParamsFor) { [item.Id] = chosen } });
        Context.Actions.Assign(item.Id, Context.TargetMonitorId);
        Context.Notify("Wallpaper set", $"“{item.Title}” is now your wallpaper. It's saved in your Library.", NoticeLevel.Success);
    }

    [RelayCommand]
    private void SaveToLibrary()
    {
        if (CurrentWithParams is not { Kind: ShaderKind.Wallpaper } c) return;
        var item = Context.Library.Upsert(GeneratedShaders.CreateItem(_services.Paths, c));
        if (c.Params is { } chosen)
            Context.Settings.Update(s => s with { ParamsFor = new Dictionary<string, double[]>(s.ParamsFor) { [item.Id] = chosen } });
        Context.Notify("Saved", $"“{item.Title}” is in your Library.", NoticeLevel.Success);
    }

    [RelayCommand]
    private void UseAsOverlay()
    {
        if (CurrentWithParams is not { Kind: ShaderKind.Overlay } c) return;
        var (id, overlay) = GeneratedShaders.CreateOverlay(_services.Paths, c);
        Context.Settings.Update(s => s with
        {
            CustomOverlays = new Dictionary<string, CustomOverlay>(s.CustomOverlays) { [id] = overlay },
            Interactive = s.Interactive with { Style = id },
        });
        Context.Notify("Overlay on", $"“{overlay.Title}” is over your wallpaper. Fine-tune or switch it on the Overlays page.", NoticeLevel.Success);
    }

    [RelayCommand]
    private void CopyChatPrompt() => CopyText(ChatPrompt, "Prompt copied", "Paste it into your AI chat, then paste the answer below.");

    [RelayCommand]
    private void CopySendBack()
    {
        if (SendBackText is { } t) CopyText(t, "Copied", "Paste it into your AI chat, then paste its new answer below.");
    }

    [RelayCommand]
    private void CopyCode()
    {
        if (Current is { } c) CopyText(c.Code, "Code copied", "The shader's GLSL is on the clipboard.");
    }

    private void CopyText(string text, string title, string message)
    {
        try
        {
            Clipboard.SetText(text);
            Context.Notify(title, message, NoticeLevel.Success);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Context.Notify("Couldn't copy", "Another app is using the clipboard. Try again.", NoticeLevel.Warning);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _designTimer.Stop();
        _surface = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Picture.Dispose();
    }
}

/// <summary>What the Studio needs from the app (one per process).</summary>
public sealed class ShaderStudioServices(ClaudeShaderAuthor claude, CloudflareShaderAuthor cloudflare, StudioHistory history, ContentGuard guard, AppPaths paths)
{
    public ClaudeShaderAuthor Claude { get; } = claude;
    public CloudflareShaderAuthor Cloudflare { get; } = cloudflare;
    public StudioHistory History { get; } = history;
    public ContentGuard Guard { get; } = guard;
    public AppPaths Paths { get; } = paths;
}
