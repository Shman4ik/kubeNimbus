using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The double-click routes for the networking kinds: a Service, an Ingress and a
/// NetworkPolicy open their own detail panes rather than their YAML (UI rule 2's "default
/// action" — the manifest is one context-menu item away, as it is for a pod).
/// </summary>
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// The pane for <paramref name="row"/> when its kind has one, or null. The key comes
    /// first and the tab is built lazily, so reopening a pane that is already open starts no
    /// watch it would immediately throw away. Matched on group <em>and</em> kind, so a CRD
    /// that happens to be called <c>Service</c> keeps its YAML.
    /// </summary>
    private (string Key, Lazy<InspectorTabViewModelBase> Tab)? NetworkingDetailFor(
        ResourceDescriptor descriptor, ResourceRowViewModel row, ClusterClient? client)
    {
        Task OpenObject(OwnerRef owner, string? namespaceHint) =>
            OpenOwnerAsync(owner, namespaceHint, row.ClusterName, client);

        return descriptor switch
        {
            { Group: "", Kind: "Service" } => (
                ServiceDetailTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name),
                new Lazy<InspectorTabViewModelBase>(() => new ServiceDetailTabViewModel(
                    client, row, OpenObject, row.ClusterName, NamedLogsOpener(row.ClusterName, client)))),

            { Group: "networking.k8s.io", Kind: "Ingress" } => (
                IngressDetailTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name),
                new Lazy<InspectorTabViewModelBase>(() => new IngressDetailTabViewModel(
                    client, row, OpenObject, row.ClusterName))),

            { Group: "networking.k8s.io", Kind: "NetworkPolicy" } => (
                NetworkPolicyDetailTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name),
                new Lazy<InspectorTabViewModelBase>(() => new NetworkPolicyDetailTabViewModel(
                    client, row, OpenObject, row.ClusterName, NamedLogsOpener(row.ClusterName, client)))),

            _ => null,
        };
    }
}
