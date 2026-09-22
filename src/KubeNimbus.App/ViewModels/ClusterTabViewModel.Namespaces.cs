using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

public sealed partial class ClusterTabViewModel
{
    [ObservableProperty] private string _namespaceFilter = "";
    [ObservableProperty] private NamespaceChoice? _namespaceCandidate;
    public ObservableCollection<NamespaceChoice> FilteredNamespaces { get; } = [];
    public bool NoNamespaceMatches => FilteredNamespaces.Count == 0;
    private string NamespaceHistoryKey => $"{Context.KubeconfigPath}\n{Context.Name}";
    private List<string> _recentNamespaces = [];

    partial void OnNamespaceFilterChanged(string value) => RebuildNamespaceChoices();

    internal void RebuildNamespaceChoices()
    {
        var filter = (NamespaceFilter ?? "").Trim();
        var names = NamespaceOptions.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).ToArray();
        FilteredNamespaces.Clear();
        foreach (var name in names.OrderBy(n => n == AllNamespaces ? -2
                     : _recentNamespaces.IndexOf(n) is var index && index >= 0 ? index : int.MaxValue)
                     .ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
            FilteredNamespaces.Add(new(name, _recentNamespaces.Contains(name) && name != AllNamespaces));
        NamespaceCandidate = FilteredNamespaces.FirstOrDefault(n => n.Name == SelectedNamespace)
            ?? FilteredNamespaces.FirstOrDefault();
        OnPropertyChanged(nameof(NoNamespaceMatches));
    }

    private void RememberNamespace(string value)
    {
        if (string.IsNullOrEmpty(value) || value == AllNamespaces) return;
        _recentNamespaces.Remove(value);
        _recentNamespaces.Insert(0, value);
        if (_recentNamespaces.Count > 5) _recentNamespaces.RemoveRange(5, _recentNamespaces.Count - 5);
        var settings = WorkspaceStore.Load();
        var recent = new Dictionary<string, List<string>>(settings.RecentNamespaces ?? [], StringComparer.Ordinal)
        { [NamespaceHistoryKey] = [.. _recentNamespaces] };
        WorkspaceStore.Save(settings with { RecentNamespaces = recent });
        RebuildNamespaceChoices();
    }
}

public sealed record NamespaceChoice(string Name, bool IsRecent);
