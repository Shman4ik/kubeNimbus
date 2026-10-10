using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The cluster tab's half of the port-forward manager (FEAT-7) and the Service forward
/// (FEAT-29): every forward pane this tab opens is registered with the window's
/// <see cref="PortForwardRegistry"/>, a closed forward pane can be shown again here, and a
/// Service is forwarded from its pane or the palette.
/// </summary>
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// The window's running forwards, set by the shell when it creates the tab. Null in
    /// view-model tests and screenshot fixtures that build a tab alone, where a forward
    /// stops with its pane as it always did.
    /// </summary>
    public PortForwardRegistry? PortForwards { get; set; }

    /// <summary>
    /// Stamps a forward pane with the tab that opened it, the cluster it runs on and the
    /// window's registry — the one way a forward pane enters the dock.
    /// </summary>
    private PortForwardTabViewModel AdoptForward(PortForwardTabViewModel pane, string clusterName)
    {
        pane.Owner = this;
        pane.Registry = PortForwards;
        pane.ClusterLabel = ContextNameFor(clusterName);
        return pane;
    }

    /// <summary>A forward's pane again: selected if it is still docked, added back if its tab was closed.</summary>
    internal void ShowForward(PortForwardTabViewModel pane)
    {
        if (InspectorTabs.Contains(pane))
        {
            SelectedInspectorTab = pane;
            return;
        }

        AddInspectorTab(pane, replacePreview: false);
    }

    /// <summary>The forwards list, in this tab's dock; reused when it is already open.</summary>
    [RelayCommand]
    private void OpenPortForwards()
    {
        if (PortForwards is null)
        {
            return;
        }

        if (InspectorTabs.FirstOrDefault(t => t.Key == PortForwardsTabViewModel.TabKey) is { } existing)
        {
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(new PortForwardsTabViewModel(PortForwards), replacePreview: false);
    }

    /// <summary>
    /// A forward pane for one Service. Opened by the Service pane's Port-forward button and
    /// the palette; the demo cluster gets the pane's own "not available" state.
    /// </summary>
    internal void PortForwardService(
        string @namespace, string serviceName, IReadOnlyList<ServicePortInfo> ports, string clusterName, ClusterClient? client)
    {
        if (client is null && !IsDemo)
        {
            return;
        }

        AddInspectorTab(AdoptForward(PortForwardTabViewModel.ForService(client, @namespace, serviceName, ports), clusterName));
    }

    /// <summary>True when the selected row is a core/v1 Service — what the palette's Service forward applies to.</summary>
    public bool IsServiceRowSelected =>
        SelectedKind?.Descriptor is { Group: "", Kind: "Service" } && SelectedRow is not null;

    [RelayCommand]
    private void PortForwardSelectedService()
    {
        if (!IsServiceRowSelected || SelectedRow is not { } row)
        {
            return;
        }

        PortForwardService(row.Namespace, row.Name, ServiceBackends.Ports(row.Resource), row.ClusterName, ClientFor(row));
    }
}
