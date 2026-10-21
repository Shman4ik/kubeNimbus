using KubeNimbus.App.Demo;
using KubeNimbus.Core;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The actions an application offers, built once for both places that offer them: the
/// application's page (its Restart and Sync buttons, the palette) and the list's row menu.
/// Each returns the <see cref="RowActionViewModel"/> the caller arms on its own strip, so the
/// question, the result line and the RBAC refusal are the same code wherever the click was.
/// </summary>
internal static class ApplicationActions
{
    /// <summary>The kinds an application's Argo CD menu offers.</summary>
    internal static bool IsArgoKind(RowActionKind kind) =>
        kind is RowActionKind.ArgoSync or RowActionKind.ArgoSyncPrune or RowActionKind.ArgoRefresh or RowActionKind.ArgoTerminate;

    /// <summary>
    /// The Application kind from discovery; on the demo cluster, from its catalog, so the
    /// strip can say in place that nothing was sent (demo rule 5).
    /// </summary>
    internal static ResourceDescriptor? ArgoDescriptor(ApplicationsViewModel list) =>
        list.Tab.DescriptorOf(ArgoCd.Group, "Application")
        ?? (list.IsDemo ? ArgoCd.ApplicationDescriptor(DemoData.BuildCatalog()) : null);

    internal static bool CanSync(ApplicationsViewModel list, ApplicationEntry entry) =>
        entry.Argo is not null && ArgoDescriptor(list) is { } d && ArgoCd.SupportsSync(d);

    /// <summary>Only while Argo reports the sync Running (#193).</summary>
    internal static bool CanTerminate(ApplicationsViewModel list, ApplicationEntry entry) =>
        CanSync(list, entry) && entry.Argo is { CanTerminate: true };

    /// <summary>
    /// An Argo CD action on the entry's Application, or null when it offers none: not an
    /// Argo app, no Application kind, or Terminate while nothing is running.
    /// </summary>
    internal static RowActionViewModel? Argo(ApplicationsViewModel list, ApplicationEntry entry, RowActionKind kind)
    {
        if (!IsArgoKind(kind)
            || !CanSync(list, entry)
            || (kind == RowActionKind.ArgoTerminate && !CanTerminate(list, entry))
            || entry.Argo is not { } argo
            || ArgoDescriptor(list) is not { } d)
        {
            return null;
        }

        return new RowActionViewModel(
            kind, list.Tab.Client, d, argo.Namespace, argo.Name,
            list.Tab.ContextNameFor(""), environment: list.Tab.Environment);
    }

    /// <summary>The workload Restart acts on and Edit YAML opens: the first that is not a Job, else the first.</summary>
    internal static DynamicResource? PrimaryWorkload(ApplicationEntry entry) =>
        entry.Input.Workloads.FirstOrDefault(w => w.Kind != "Job" && w.Kind != "CronJob")
        ?? entry.Input.Workloads.FirstOrDefault();

    internal static bool CanRestart(ApplicationsViewModel list, ApplicationEntry entry) =>
        PrimaryWorkload(entry) is { } w && WorkloadActions.SupportsRestart(DescriptorFor(list, w), w)
        && (list.Tab.Client is not null || list.IsDemo);

    internal static RowActionViewModel? Restart(ApplicationsViewModel list, ApplicationEntry entry) =>
        CanRestart(list, entry) && PrimaryWorkload(entry) is { } w
            ? new RowActionViewModel(
                RowActionKind.Restart, list.Tab.Client, DescriptorFor(list, w), w.Namespace, w.Name,
                list.Tab.ContextNameFor(""), environment: list.Tab.Environment)
            : null;

    /// <summary>A workload's kind from discovery, or one built from the object when discovery has not named it.</summary>
    internal static ResourceDescriptor DescriptorFor(ApplicationsViewModel list, DynamicResource resource)
    {
        var group = GroupOf(resource);
        return list.Tab.DescriptorOf(group, resource.Kind)
            ?? new ResourceDescriptor(group, VersionOf(resource), resource.Kind, resource.Kind.ToLowerInvariant() + "s",
                resource.Kind.ToLowerInvariant(), true, [], []);
    }

    internal static string GroupOf(DynamicResource resource)
    {
        var slash = resource.ApiVersion.IndexOf('/');
        return slash < 0 ? "" : resource.ApiVersion[..slash];
    }

    private static string VersionOf(DynamicResource resource)
    {
        var slash = resource.ApiVersion.IndexOf('/');
        return slash < 0 ? resource.ApiVersion : resource.ApiVersion[(slash + 1)..];
    }
}
