using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// New YAML… (FEAT-12, the paste-or-type slice). Reached from the ☰ menu and the palette; no
// always-visible control (UI rule 1). Local-file open and a template library are not here.
public sealed partial class ClusterTabViewModel
{
    private int _newYamlCount;

    /// <summary>
    /// Only with an API server to apply to: not on the demo cluster (there is nothing to
    /// create on), and not before the tab has connected.
    /// </summary>
    public bool CanCreateFromYaml => Client is not null && !IsDemo;

    /// <summary>
    /// The namespace a new namespaced object without one goes to: the list's own namespace
    /// when it shows one (the first, when it shows several), else the context's, else
    /// <c>default</c> — kubectl's order.
    /// </summary>
    public string NewObjectNamespace =>
        SelectedNamespaces is [var first, ..] ? first
        : Context.Namespace is { Length: > 0 } contextNamespace ? contextNamespace
        : "default";

    /// <summary>
    /// Opens an empty editor in the dock. Always a new tab, never a preview and never one
    /// that replaces another editor (UI rule 5): each has its own key.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateFromYaml))]
    private void NewYaml()
    {
        if (Client is not { } client)
        {
            return;
        }

        _newYamlCount++;
        var editor = new YamlEditorTabViewModel(
            client,
            async (group, kind, cancellationToken) => (await client.GetResourceCatalogAsync(cancellationToken))
                .FirstOrDefault(d => d.Group == group && d.Kind == kind),
            NewObjectNamespace,
            _newYamlCount)
        {
            IsPreview = false,
        };

        AddInspectorTab(editor, replacePreview: false);
    }
}
