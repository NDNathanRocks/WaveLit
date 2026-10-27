using System.IO;
using Microsoft.Web.WebView2.Core;
using Wavelit.Core.Storage;

namespace Wavelit.App.Wallpaper;

/// <summary>
/// One WebView2 setup for the wallpapers and the dashboard's Studio preview. The Studio uses its own profile folder:
/// its browser can't disturb the wallpapers' (a crashing AI shader stays in the Studio), it exits when the page
/// closes, and a second environment on the wallpapers' folder was refused with 0x8007139F on this PC.
/// </summary>
internal static class WebViewSetup
{
    // - autoplay: unmuted video may start without a click.
    // - HardwareMediaKeyHandling/MediaSessionService: media keys and the Windows media flyout
    //   must not pick up the wallpaper.
    // - CalculateNativeWinOcclusion: Chromium thinks a window under the desktop icons is
    //   hidden and stops painting it; Wavelit does its own occlusion handling.
    private const string BrowserArguments = "--autoplay-policy=no-user-gesture-required " +
        "--disable-features=HardwareMediaKeyHandling,MediaSessionService,CalculateNativeWinOcclusion";

    /// <summary>Creates an environment for the calling (STA) thread; its WebViews must live on that thread.</summary>
    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(AppPaths paths, bool studio = false)
        => CoreWebView2Environment.CreateAsync(null, studio ? Path.Combine(paths.Root, "WebView2Studio") : paths.WebView2, new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = BrowserArguments,
        });
}
