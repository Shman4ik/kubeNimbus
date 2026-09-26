using System.Collections.Specialized;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// "Show me this object in the explorer" — how the Applications page hands a linked
/// resource (a Service, a ConfigMap, an HPA) to the Resources mode: the kind is selected in
/// the sidebar, the namespace is set, and the row is selected as soon as the watch delivers
/// it. Nothing new is opened in the dock; the reader lands on the list with the object
/// highlighted, which is where every other gesture on it already lives.
/// </summary>
public sealed partial class ClusterTabViewModel
{
    private string? _pendingRevealKey;

    /// <summary>
    /// Selects <paramref name="kind"/> in <paramref name="namespace"/> and then the row named
    /// <paramref name="name"/>. Returns false, with the tab's status saying why, when this
    /// cluster's catalog has no such kind — never a click that silently does nothing.
    /// </summary>
    public bool RevealInResources(string group, string kind, string? @namespace, string name)
    {
        var target = SidebarSections
            .SelectMany(s => s.Kinds)
            .FirstOrDefault(k => !k.IsRecentEntry && !k.IsArgoDashboard && !k.IsHelmReleases
                && k.Descriptor.Group == group && k.Descriptor.Kind == kind);
        if (target is null)
        {
            Status = $"{kind} is not a kind this cluster serves, so {name} cannot be shown in Resources.";
            return false;
        }

        _pendingRevealKey = $"{(target.Descriptor.Namespaced ? @namespace : null)}/{name}";

        if (target.Descriptor.Namespaced && !string.IsNullOrEmpty(@namespace))
        {
            // Under narrow RBAC the namespace list may not hold it; the object's own
            // namespace is still the right answer, the same call ApplyInitialView makes.
            if (!NamespaceOptions.Contains(@namespace))
            {
                NamespaceOptions.Add(@namespace);
            }

            SelectedNamespace = @namespace;
        }

        RowFilter = "";
        if (SelectedKind == target)
        {
            TrySelectPendingReveal();
        }
        else
        {
            SelectKind(target);
        }

        return true;
    }

    private void OnRowsChangedForReveal(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_pendingRevealKey is not null)
        {
            TrySelectPendingReveal();
        }
    }

    private void TrySelectPendingReveal()
    {
        if (_pendingRevealKey is not { } key)
        {
            return;
        }

        if (VisibleRows.FirstOrDefault(r => $"{r.Resource.Namespace}/{r.Resource.Name}" == key) is { } row)
        {
            _pendingRevealKey = null;
            SelectedRow = row;
        }
    }
}
