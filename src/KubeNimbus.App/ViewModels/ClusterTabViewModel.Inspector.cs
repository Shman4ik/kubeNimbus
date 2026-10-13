using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// The inspector dock's tabs: opening a row or an owner into one (a preview replaced in place, a
// permanent tab promoted), the access review, and selecting and closing tabs (UI rules 5 and 7).
public sealed partial class ClusterTabViewModel
{
    private async Task OpenRowAsync(ResourceRowViewModel? row, bool preview, ResourceDescriptor? knownDescriptor = null, ClusterClient? knownClient = null)
    {
        // In fleet mode the row's own cluster owns it — using this tab's client here
        // would open (and later apply/delete) against the wrong cluster.
        if (row is null || (knownDescriptor ?? DescriptorFor(row)) is not { } descriptor)
        {
            return;
        }

        var client = knownClient ?? ClientFor(row);
        if (client is null && !IsDemo)
        {
            return;
        }

        // Events aren't independently editable/useful objects to browse — jumping
        // straight to what the event is about (the same navigation owner-chips use)
        // is the more useful default action here, matching CLAUDE.md's "double-click
        // = default action" rule.
        //
        // In fleet mode the involved object lives on the event's own cluster, so the
        // row's cluster and client go with it — the tab's own client would open (or fail
        // to find) the object on the wrong cluster. An event that names no object falls
        // through and opens the event itself.
        if (descriptor.IsEventKind() && row.Resource.InvolvedObject() is { } involved)
        {
            await OpenOwnerAsync(involved, row.Resource.InvolvedObjectNamespace() ?? row.Namespace, row.ClusterName, client);
            return;
        }

        // Double-click = the default action for the kind (UI rule 2). A node's is its
        // detail pane, not its manifest: conditions, taints and how full it is are what
        // the double-click is for, and the YAML is one context-menu item away as it is
        // for a pod.
        if (WorkloadDetailTabViewModel.Supports(descriptor))
        {
            var workloadKey = WorkloadDetailTabViewModel.KeyFor(row.ClusterName, descriptor, row.Namespace, row.Name);
            if (InspectorTabs.FirstOrDefault(t => t.Key == workloadKey) is { } opened)
            {
                if (!preview) opened.IsPreview = false;
                SelectedInspectorTab = opened;
                return;
            }
            var detail = new WorkloadDetailTabViewModel(client, descriptor, row, AddInspectorTab, async kind =>
            {
                if (ArmRowAction(kind, row, descriptor, client) is { } action && kind == RowActionKind.Scale)
                    await action.LoadCurrentScaleAsync();
            }, (owner, ns) => OpenOwnerAsync(owner, ns, row.ClusterName, client), key =>
            {
                if (InspectorTabs.FirstOrDefault(t => t.Key == key) is not { } existingTab) return false;
                existingTab.IsPreview = false;
                SelectedInspectorTab = existingTab;
                return true;
            }, NamedLogsOpener(row.ClusterName, client), RowRefreshedInPlace) { IsPreview = preview };
            AddInspectorTab(detail, replacePreview: preview);
            return;
        }

        // Services, Ingresses and NetworkPolicies open their own panes for the same reason:
        // "why is traffic not reaching my pods" is answered by what they select and route to,
        // not by their manifest. See docs/engineering/networking-detail.md.
        // Argo CD's kinds the same way (#194): see ClusterTabViewModel.Argo.cs.
        if ((NetworkingDetailFor(descriptor, row, client) ?? ArgoDetailFor(descriptor, row, client)) is { } networking)
        {
            if (InspectorTabs.FirstOrDefault(t => t.Key == networking.Key) is { } openedNetworking)
            {
                if (!preview) openedNetworking.IsPreview = false;
                SelectedInspectorTab = openedNetworking;
                return;
            }

            networking.Tab.Value.IsPreview = preview;
            AddInspectorTab(networking.Tab.Value, replacePreview: preview);
            return;
        }

        var isPod = descriptor is { Kind: "Pod", Group: "" };
        var isNode = NodeActions.IsNodeKind(descriptor);
        var key = (isPod, isNode) switch
        {
            (true, _) => PodDetailTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name),
            (_, true) => NodeDetailTabViewModel.KeyFor(row.ClusterName, row.Name),
            _ => YamlEditorTabViewModel.KeyFor(row.ClusterName, descriptor, row.Namespace, row.Name),
        };
        var existing = InspectorTabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            if (!preview)
            {
                existing.IsPreview = false;
            }

            SelectedInspectorTab = existing;
            return;
        }

        InspectorTabViewModelBase tab = (isPod, isNode) switch
        {
            (true, _) => new PodDetailTabViewModel(
                client, row, AddInspectorTab,
                // Bound to this row's cluster so owner navigation stays on it.
                (owner, namespaceHint) => OpenOwnerAsync(owner, namespaceHint, row.ClusterName, client),
                row.ClusterName),
            (_, true) => new NodeDetailTabViewModel(
                client, row, PodDescriptorFor(row),
                (owner, namespaceHint) => OpenOwnerAsync(owner, namespaceHint, row.ClusterName, client),
                row.ClusterName,
                NamedLogsOpener(row.ClusterName, client)),
            _ => new YamlEditorTabViewModel(
                client, descriptor, row.Namespace, row.Name, row.Resource.ToYaml(), row.ClusterName),
        };

        tab.IsPreview = preview;
        AddInspectorTab(tab, replacePreview: preview);
    }

    private void AddInspectorTab(InspectorTabViewModelBase tab) => AddInspectorTab(tab, replacePreview: tab.IsPreview);

    private void AddInspectorTab(InspectorTabViewModelBase tab, bool replacePreview)
    {
        // The YAML editor's own Delete has to name the cluster and follow the production
        // rule just as the strip does. Stamped here, the one way any editor enters the dock
        // (the list, workload detail, owner navigation), from the editor's own cluster.
        if (tab is YamlEditorTabViewModel editor)
        {
            editor.SetCluster(ContextNameFor(editor.ClusterName), EnvironmentOf(editor.ClusterName));
        }

        if (replacePreview)
        {
            var previousPreview = InspectorTabs.FirstOrDefault(t => t.IsPreview);
            if (previousPreview is not null)
            {
                _ = CloseInspectorTabAsync(previousPreview);
            }
        }

        InspectorTabs.Add(tab);
        SelectedInspectorTab = tab;
    }

    /// <summary>
    /// Resolves an ownerReference (pod → replicaset → deployment, etc.) and opens its
    /// YAML. <paramref name="clusterName"/> keeps the whole chain on the cluster the
    /// starting object came from when navigating out of an aggregated fleet row —
    /// an owner chain that hopped clusters mid-way would be nonsense.
    /// </summary>
    private async Task OpenOwnerAsync(OwnerRef owner, string? namespaceHint, string clusterName = "", ClusterClient? knownClient = null)
    {
        if (IsDemo)
        {
            OpenDemoOwner(owner, namespaceHint);
            return;
        }

        if ((knownClient ?? ClientForCluster(clusterName)) is not { } client)
        {
            return;
        }

        var resolved = await client.ResolveOwnerAsync(owner, namespaceHint);
        if (resolved is null)
        {
            ConnectionWarning = $"{owner.Kind}/{owner.Name} could not be resolved — deleted, replaced, or not a name an object can have.";
            return;
        }

        // The descriptor of the object that came back, not of the reference: ResolveOwnerAsync
        // has checked they agree, and the inspector must never bind one kind's descriptor to
        // another kind's object (it opened whatever a rewritten path returned, before).
        var catalog = await client.GetResourceCatalogAsync();
        var descriptor = catalog.FirstOrDefault(d =>
            d.ApiVersion == resolved.ApiVersion && d.Kind == resolved.Kind);
        if (descriptor is null)
        {
            return;
        }

        await OpenRowAsync(new ResourceRowViewModel(resolved, clusterName), preview: false, descriptor, client);
    }

    /// <summary>
    /// Opens the access-review tab. With no subject it answers "what may I do in
    /// this namespace?" straight from the API server; with one (a selected
    /// ServiceAccount) it also traces where that subject's access comes from.
    /// </summary>
    [RelayCommand]
    private void OpenAccessReview(SubjectRef? subject) => ShowAccessReview(subject);

    /// <summary>
    /// Opens the access review straight onto "Who can do X?" — the cluster-wide direction,
    /// which has no row to start from (the answer is a set of subjects, not a property of
    /// the selected object), so the palette is where it belongs.
    /// </summary>
    [RelayCommand]
    private void OpenWhoCan()
    {
        if (ShowAccessReview(null) is { } tab)
        {
            tab.SelectedTabIndex = RbacTabViewModel.WhoCanTabIndex;
        }
    }

    private RbacTabViewModel? ShowAccessReview(SubjectRef? subject)
    {
        if (Client is null)
        {
            return null;
        }

        var @namespace = SelectedNamespace == AllNamespaces ? "default" : SelectedNamespace;
        var key = subject is null
            ? $"rbac:{@namespace}"
            : $"rbac:{subject.Kind}/{subject.Namespace}/{subject.Name}";

        if (InspectorTabs.FirstOrDefault(t => t.Key == key) is RbacTabViewModel existing)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return existing;
        }

        var tab = new RbacTabViewModel(Client, @namespace, subject);
        AddInspectorTab(tab, replacePreview: false);
        return tab;
    }

    /// <summary>
    /// The selected row as an RBAC subject, when it is one — only ServiceAccounts
    /// exist as objects (Users and Groups are just strings in a binding), so
    /// that's the one kind that can seed a subject review from the list.
    /// </summary>
    public SubjectRef? SelectedRowAsSubject =>
        SelectedKind?.Descriptor is { Group: "", Kind: "ServiceAccount" } && SelectedRow is { } row
            ? new SubjectRef("ServiceAccount", row.Name, row.Namespace)
            : null;

    [RelayCommand]
    private void SelectInspectorTab(InspectorTabViewModelBase tab) => SelectedInspectorTab = tab;

    [RelayCommand]
    private async Task CloseInspectorTabAsync(InspectorTabViewModelBase tab)
    {
        await tab.OnClosingAsync();
        var index = InspectorTabs.IndexOf(tab);
        InspectorTabs.Remove(tab);
        if (SelectedInspectorTab == tab)
        {
            SelectedInspectorTab = InspectorTabs.Count == 0
                ? null
                : InspectorTabs[Math.Min(index, InspectorTabs.Count - 1)];
        }

        if (InspectorTabs.Count == 0)
        {
            IsInspectorMaximized = false;
        }
    }
}
