using Avalonia.Controls;
using Avalonia.Platform;

namespace KubeNimbus.App;

/// <summary>
/// Loads the app icon through <see cref="AssetLoader"/> instead of XAML's
/// <c>Icon="/Assets/app.ico"</c>.
///
/// <para>
/// <b>This is not a style preference — the XAML form does not survive NativeAOT.</b>
/// A published binary died immediately on every RID with
/// <c>FileNotFoundException: The resource /Assets/app.ico could not be found</c>, out
/// of <c>StandardAssetLoader.OpenAndGetAssembly</c> by way of
/// <c>IconTypeConverter.CreateIconFromPath</c>, before the main window ever appeared.
/// The resource is present in the published assembly; what does not survive is the
/// converter's resolution of a <i>relative</i> path, which needs a base URI that the
/// trimmed/AOT build no longer supplies. Fully qualifying the URI in the XAML
/// attribute was tried and does not help — the converter is the problem, so the fix is
/// to not go through it.
/// </para>
///
/// <para>
/// Loading the stream by absolute <c>avares://</c> URI in code skips the converter
/// entirely, and is the same path pgNimbus has always used for its own window icons —
/// which is why that app's AOT binaries start and this one's did not.
/// </para>
///
/// <para>
/// The authority is the <b>assembly name</b>, <c>kubeNimbus</c>, not the project name:
/// renaming <c>&lt;AssemblyName&gt;</c> breaks this at runtime, not at build time. It
/// is the same trap the csproj and App.axaml already document.
/// </para>
/// </summary>
public static class WindowIcons
{
    private const string AppIconUri = "avares://kubeNimbus/Assets/app.ico";

    // Built once and shared: every window wants the same icon, and decoding an .ico per
    // window is wasted work. Lazy so a missing resource fails where it is used rather
    // than in a static constructor, which would take the whole app down.
    private static readonly Lazy<WindowIcon?> App = new(Load);

    /// <summary>
    /// Puts the app icon on a window. Silently does nothing if the resource cannot be
    /// read — an icon is cosmetic, and the entire reason this type exists is that
    /// letting icon loading throw took the app down before it drew a single frame.
    /// </summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (App.Value is { } icon)
        {
            window.Icon = icon;
        }
    }

    private static WindowIcon? Load()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AppIconUri));
            return new WindowIcon(stream);
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or ArgumentException)
        {
            return null;
        }
    }
}
