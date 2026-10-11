using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The main window a view model reaches for its clipboard or storage provider. In the app it
/// is the desktop lifetime's <c>MainWindow</c>. The screenshot harness runs under a lifetime it
/// cannot replace once the application is initialised, so it sets <see cref="Override"/> for the
/// length of a check that reads what a command copied (<c>ClipboardChecks.LogMenuCopy</c>, #288).
/// </summary>
internal static class MainWindowAccess
{
    /// <summary>Harness seam: the window to answer instead of the lifetime's. Null in the app.</summary>
    internal static Window? Override { get; set; }

    public static Window? Current => Override
        ?? (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null);
}
