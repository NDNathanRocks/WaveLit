using Wavelit.Core.Storage;

namespace Wavelit.Core.Desktop;

/// <summary>Reads and writes the taskbar's documented per-user settings.</summary>
public interface ITaskbarBackend
{
    /// <summary>TaskbarAl: 0 = left, 1 = centre, null = never set (Windows 11's default, centre).</summary>
    int? Alignment { get; set; }
    bool AutoHide { get; set; }
}

/// <summary>The user's own taskbar values from before Wavelit changed anything (persisted, so a crash can't lose them).</summary>
public sealed record TaskbarOriginal(int? Alignment, bool AutoHide);

/// <summary>
/// Applies <see cref="TaskbarSettings"/> and guarantees the way back (plan §22.1): the user's values are saved
/// the first time Wavelit changes anything, restored when a feature is turned off, and restored on quit unless
/// "Keep after quit". A crash leaves the saved values on disk; the next start or quit puts them back.
/// </summary>
public sealed class TaskbarController(ITaskbarBackend backend, string originalPath)
{
    public TaskbarOriginal? Original => JsonFile.Read<TaskbarOriginal>(originalPath);

    public void Apply(TaskbarSettings desired)
    {
        desired = desired.Normalize();
        var original = Original;
        if (original is null)
        {
            if (!desired.ChangesAnything) return;          // nothing changed, nothing to remember
            original = new TaskbarOriginal(backend.Alignment, backend.AutoHide);
            JsonFile.Write(originalPath, original);
        }

        int? alignment = desired.Align switch
        {
            TaskbarSettings.Center => 1,
            TaskbarSettings.Left => 0,
            _ => original.Alignment,
        };
        if (backend.Alignment != alignment) backend.Alignment = alignment;
        bool autoHide = desired.AutoHide || original.AutoHide;
        if (backend.AutoHide != autoHide) backend.AutoHide = autoHide;

        // Everything is back to the user's own values: forget them, so later changes they make stay theirs.
        if (!desired.ChangesAnything) Forget();
    }

    /// <summary>Puts the user's values back (on quit, unless they chose to keep Wavelit's).</summary>
    public void Restore()
    {
        var original = Original;
        if (original is null) return;
        if (backend.Alignment != original.Alignment) backend.Alignment = original.Alignment;
        if (backend.AutoHide != original.AutoHide) backend.AutoHide = original.AutoHide;
        Forget();
    }

    private void Forget()
    {
        try
        {
            File.Delete(originalPath);
        }
        catch (IOException) { }
    }
}
