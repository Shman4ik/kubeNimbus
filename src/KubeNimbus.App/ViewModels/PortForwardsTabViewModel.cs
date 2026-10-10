using CommunityToolkit.Mvvm.Input;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The one place every running port-forward is listed (FEAT-7): its cluster, what it
/// reaches, the local address it listens on and whether its last connection failed, with
/// Open (its own pane again) and Stop on each row. Opened from the status bar's forward
/// count or the palette's "Port-forwards" entry, into the selected cluster tab's dock —
/// the list itself is the window's, so it shows every cluster's forwards.
/// </summary>
public sealed partial class PortForwardsTabViewModel : InspectorTabViewModelBase
{
    public const string TabKey = "portforwards";

    public PortForwardsTabViewModel(PortForwardRegistry registry)
        : base("Port-forwards")
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
    }

    public PortForwardRegistry Registry { get; }

    public override string Key => TabKey;

    /// <summary>"No port-forward is running" is a state, and gets a sentence (UI rule 9).</summary>
    public const string EmptyText =
        "No port-forward is running. Start one from a pod (F on its row) or from a Service's pane; "
        + "a forward keeps running when its tab is closed, and is listed here until it is stopped.";

    [RelayCommand]
    private void Open(PortForwardTabViewModel? forward)
    {
        if (forward is not null)
        {
            Registry.Reopen(forward);
        }
    }

    /// <summary>Stops on the click: a stopped forward is started again from its pane (UI rule 17).</summary>
    [RelayCommand]
    private Task StopAsync(PortForwardTabViewModel? forward) =>
        forward is null ? Task.CompletedTask : forward.StopForwardingAsync("Stopped from the port-forwards list.");
}
