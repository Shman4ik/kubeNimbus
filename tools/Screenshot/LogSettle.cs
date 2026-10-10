using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.Screenshot;

/// <summary>
/// ENG-10: waits, before a capture, until every log stream on screen has stopped changing
/// — the demo cluster's replayed streams have finished, and the fixture tabs' streams
/// against the offline client have failed. Both run on real timers, so capturing at
/// whatever moment the builder returned used to decide how many lines a pane held and
/// whether its Follow button had flipped yet, and two runs of one commit came out
/// different (cluster-tab-demo-pod-detail by up to 2 426 pixels).
///
/// <para>
/// A finished stream is fully deterministic for one stream, and since ENG-53 for a pane that
/// merges several too (workload logs, the Applications page's merged view): the merge sorts
/// only within a flush tick by design, so the demo now replays every stream of a pane in the
/// order its lines were logged, and which tick a line lands in no longer decides its place.
/// That is the pane's own doing (<c>WorkloadLogsTabViewModel.RunDemoReplayAsync</c>); waiting
/// for every source to end is all this needs to do.
/// </para>
/// </summary>
internal static class LogSettle
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    internal static void Run(Window window)
    {
        if (window.DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        var deadline = DateTime.UtcNow + Budget;
        while (!IsSettled(shell) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsSettled(MainWindowViewModel shell)
    {
        foreach (var tab in shell.Tabs)
        {
            foreach (var pane in tab.InspectorTabs)
            {
                if (!IsSettled(pane))
                {
                    return false;
                }
            }

            if (tab.Applications.Page?.Logs is { } embedded && !IsSettled(embedded))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSettled(InspectorTabViewModelBase pane) => pane switch
    {
        PodDetailTabViewModel detail => !detail.IsFollowingLogs,
        WorkloadLogsTabViewModel logs => logs.Sources.All(s => s.State is not (LogSourceState.Starting or LogSourceState.Streaming)),
        _ => true,
    };
}
