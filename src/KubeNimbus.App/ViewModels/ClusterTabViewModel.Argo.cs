using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The double-click routes for Argo CD's three kinds from the ordinary resource list: an
/// Application opens the same pane the GitOps dashboard opens, an ApplicationSet shows what it
/// generates and from what, and an AppProject shows what its Applications may deploy (#194).
/// The YAML stays one context-menu item away, as it is for a pod.
/// </summary>
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// The pane for <paramref name="row"/> when it is one of Argo CD's kinds, or null. Built
    /// lazily behind its key, like <see cref="NetworkingDetailFor"/>, so reopening an open
    /// pane reads nothing. Matched on group and kind, so somebody else's <c>Application</c>
    /// CRD keeps its YAML.
    /// </summary>
    private (string Key, Lazy<InspectorTabViewModelBase> Tab)? ArgoDetailFor(
        ResourceDescriptor descriptor, ResourceRowViewModel row, ClusterClient? client)
    {
        Task OpenObject(OwnerRef owner, string? namespaceHint) =>
            OpenOwnerAsync(owner, namespaceHint, row.ClusterName, client);

        if (ArgoCd.IsApplicationKind(descriptor))
        {
            return (
                ArgoApplicationTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name),
                new Lazy<InspectorTabViewModelBase>(() => new ArgoApplicationTabViewModel(
                    client, descriptor, ArgoCd.ReadApplication(row.Resource), OpenObject, row.ClusterName,
                    NamedLogsOpener(row.ClusterName, client))));
        }

        if (ArgoCd.IsApplicationSetKind(descriptor))
        {
            return (
                ArgoApplicationSetTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name),
                new Lazy<InspectorTabViewModelBase>(() => new ArgoApplicationSetTabViewModel(
                    client, descriptor, ArgoCd.ReadApplicationSet(row.Resource), OpenObject, row.ClusterName,
                    ApplicationDescriptorFor(row.ClusterName, client))));
        }

        if (ArgoCd.IsProjectKind(descriptor))
        {
            return (
                ArgoProjectTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name),
                new Lazy<InspectorTabViewModelBase>(() => new ArgoProjectTabViewModel(
                    client, descriptor, ArgoCd.ReadProject(row.Resource), row.ClusterName)));
        }

        return null;
    }

    /// <summary>
    /// The Application kind on the row's own cluster, for an ApplicationSet pane to read its
    /// generated Applications through. This tab's sidebar catalog for its own cluster; the
    /// member's discovery (cached) for a fleet row, resolved when the pane asks.
    /// </summary>
    private Func<CancellationToken, Task<ResourceDescriptor?>> ApplicationDescriptorFor(string clusterName, ClusterClient? client) =>
        async ct =>
        {
            if (clusterName.Length == 0 && ArgoApplicationDescriptor() is { } own)
            {
                return own;
            }

            return client is null
                ? null
                : ArgoCd.ApplicationDescriptor(await client.GetResourceCatalogAsync(ct));
        };
}
