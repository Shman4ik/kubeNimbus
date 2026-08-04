using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace KubeNimbus.App;

/// <summary>
/// Keeps a secondary window's OS caption in step with the app's theme.
///
/// <para>
/// The main window has no need for this — its caption <i>is</i> the command bar
/// (UI rule 12), so there is no OS-painted title bar left to disagree with. Every
/// other window still gets one, and Windows paints it from the OS's own dark-mode
/// and accent settings: open Preferences while the app is in Light and Windows is in
/// Dark and the title bar is black above a white page. That is exactly the "the two
/// apps don't look like one family" complaint, one window deeper.
/// </para>
///
/// <para>
/// Call <see cref="Attach"/> once from the window's constructor. Everything here is
/// cosmetic and best-effort: an unsupported Windows build, a window that has not
/// opened yet, or a missing resource all degrade to the OS default rather than
/// failing window construction.
/// </para>
///
/// <para>
/// pgNimbus has its own copy of this (plus a native-icon half kubeNimbus does not
/// need, since it sets one icon everywhere). The caption-colour half is identical in
/// both and is listed on DESIGN.md's cross-port list to be unified into Nimbus.Ui.
/// </para>
/// </summary>
public static class ThemedWindowChrome
{
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        Apply(window);

        // The Win32 handle does not exist until the window opens, and
        // ActualThemeVariant is not final at construction time; the Opened hook is
        // what actually lands the colour. The variant hook covers the in-app theme
        // toggle and an OS theme flip while the window is up.
        window.Opened += (_, _) => Apply(window);
        window.ActualThemeVariantChanged += (_, _) => Apply(window);
    }

    private const int DwmwaCaptionColor = 35; // DWMWA_CAPTION_COLOR, Windows 11+
    private const int DwmwaTextColor = 36;    // DWMWA_TEXT_COLOR,    Windows 11+

    private static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        if (window.TryGetPlatformHandle() is not { } handle)
        {
            return; // not opened yet; the Opened hook re-applies
        }

        var dark = window.ActualThemeVariant == ThemeVariant.Dark;

        // The same brush the shell base is painted with, resolved for the window's
        // actual theme, so the caption and the page below it read as one surface.
        var caption = window.TryFindResource(
                "SystemControlBackgroundChromeMediumLowBrush", window.ActualThemeVariant, out var resource)
            && resource is ISolidColorBrush brush
            ? brush.Color
            : dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);

        var text = dark ? Color.FromRgb(0xF0, 0xF0, 0xF0) : Color.FromRgb(0x1B, 0x1B, 0x1B);

        var captionRef = ToColorRef(caption);
        var textRef = ToColorRef(text);
        _ = DwmSetWindowAttribute(handle.Handle, DwmwaCaptionColor, ref captionRef, sizeof(uint));
        _ = DwmSetWindowAttribute(handle.Handle, DwmwaTextColor, ref textRef, sizeof(uint));
    }

    /// <summary>Win32 COLORREF is 0x00BBGGRR — the byte order is reversed from a Color.</summary>
    private static uint ToColorRef(Color c) => (uint)(c.B << 16 | c.G << 8 | c.R);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
