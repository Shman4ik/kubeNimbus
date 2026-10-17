using Avalonia.Controls;
using Avalonia.Threading;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.Screenshot;

/// <summary>
/// Closes a captured window the way the app closes a cluster tab, not only the way it closes a
/// window. The app has one window and ends with it, so closing a window was never asked to
/// release anything; a cluster tab is released by <c>CloseTabAsync</c>, which disposes it and
/// closes its inspector tabs. The harness closed only the window, and an inspector that
/// subscribes to a static event — the YAML editor's <c>PreviewPreferenceChanged</c> — then kept
/// its view, and through it the whole window, alive for the rest of the run. Every window ever
/// captured stayed in memory, and every font reset before a capture
/// (<c>App.ApplyFonts</c>) re-resolved resources through all of them: measured on an M-series
/// Mac, the reset grew from nothing to 1.7 s a capture over 300 captures and was 230 s of a
/// 287 s run.
/// </summary>
internal static class HarnessTeardown
{
    /// <summary>
    /// How many closed windows may still be alive when the run ends. The last capture's window
    /// can be held by work the dispatcher has not run yet; measured, one or two are. A leak keeps
    /// one per capture, hundreds in a full run and dozens in a shard.
    /// </summary>
    private const int AliveAllowance = 8;

    private static readonly List<WeakReference<Window>> Closed = [];

    internal static void Close(Window window)
    {
        var shell = window.DataContext as MainWindowViewModel;
        window.Close();
        Closed.Add(new WeakReference<Window>(window));
        if (shell is null)
        {
            return;
        }

        foreach (var tab in shell.Tabs.ToList())
        {
            var disposing = tab.DisposeAsync().AsTask();
            for (var i = 0; i < 500 && !disposing.IsCompleted; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }
        }

        shell.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The resources <c>NimbusFonts.Apply</c> writes, as the defaults left them.</summary>
    private static (object? Key, object? Value)[]? _defaultFaces;

    private static readonly string[] FaceKeys = ["ContentControlThemeFontFamily", "KeyCapFont", "InterfaceLetterSpacing", "MonoFont"];

    /// <summary>
    /// Puts the shipped faces back when a scenario changed one (<c>App.ApplyFonts</c> from the
    /// defaults), and leaves the resources alone when none did. Compared by value, so a face
    /// that is equal but a new object (a boxed letter spacing) is not a change.
    /// </summary>
    internal static void ResetFonts()
    {
        var resources = Avalonia.Application.Current!.Resources;
        if (_defaultFaces is not null
            && _defaultFaces.All(f => resources.TryGetValue(f.Key!, out var value) && Equals(value, f.Value)))
        {
            return;
        }

        KubeNimbus.App.App.ApplyFonts(KubeNimbus.App.App.LoadSettings());
        _defaultFaces = [.. FaceKeys.Select(k => ((object?)k, resources.TryGetValue(k, out var v) ? v : null))];
    }

    /// <summary>
    /// Fails the run when the closed windows are not being collected, so a new static
    /// subscription that roots a view shows up as a red smoke test rather than as a run that is
    /// slower every week.
    /// </summary>
    internal static void ThrowIfWindowsLeaked()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var alive = Closed.Count(w => w.TryGetTarget(out _));
        Console.WriteLine($"Closed windows still alive at the end of the run: {alive} of {Closed.Count}.");
        if (alive > AliveAllowance)
        {
            throw new InvalidOperationException(
                $"{alive} of {Closed.Count} closed windows are still alive after a full collection. Something "
                + "outlives the window it was built for — usually a static event a view model subscribes to "
                + "and unsubscribes from only when its tab closes (HarnessTeardown).");
        }
    }
}
