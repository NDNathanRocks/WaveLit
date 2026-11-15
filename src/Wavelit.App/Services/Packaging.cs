using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace Wavelit.App.Services;

/// <summary>
/// What kind of build this is. The Microsoft Store build runs as an MSIX package: it has an identity, Windows
/// starts it through a StartupTask instead of the Run key, and (without the restricted <c>unvirtualizedResources</c>
/// capability, which the Store rarely grants) its writes to other apps' settings are silently redirected into the
/// package's private copy. Features that change Windows or another app therefore check <see cref="CanChangeSystemSettings"/>.
/// </summary>
internal static class Packaging
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
    private static readonly Lazy<bool> _isPackaged = new(DetectPackaged);
    private static readonly Lazy<bool> _unvirtualized = new(DetectUnvirtualized);

    /// <summary>Running from an MSIX package (the Store build).</summary>
    public static bool IsPackaged => _isPackaged.Value;

    /// <summary>
    /// Writes to the per-user registry and to other apps' AppData reach their real targets: an unpackaged build, or a
    /// package whose manifest declares <c>unvirtualizedResources</c>.
    /// </summary>
    public static bool CanChangeSystemSettings => !IsPackaged || _unvirtualized.Value;

    private static bool DetectPackaged()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
    }

    // The manifest in the package's install folder is read-only and always present; it is the one source of truth
    // for which capabilities Windows granted this build.
    private static bool DetectUnvirtualized()
    {
        try
        {
            int length = 0;
            GetCurrentPackagePath(ref length, null);
            if (length <= 0) return false;
            var buffer = new StringBuilder(length);
            if (GetCurrentPackagePath(ref length, buffer) != 0) return false;
            string manifest = Path.Combine(buffer.ToString(), "AppxManifest.xml");
            if (!File.Exists(manifest)) return false;
            var doc = XDocument.Load(manifest);
            return doc.Descendants().Any(e => e.Name.LocalName == "Capability"
                && string.Equals((string?)e.Attribute("Name"), "unvirtualizedResources", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackagePath(ref int pathLength, StringBuilder? path);
}
