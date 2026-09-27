using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// True once the cluster's namespaces have been listed. Until then — and for good
    /// when RBAC refuses <c>list namespaces</c>, which is the expected case on a shared
    /// cluster — the picker cannot know every namespace the user may read, so it offers
    /// the name being typed as a choice of its own and keeps the recent namespaces in
    /// its list. Without that, a user granted one namespace and no context namespace had
    /// no control that could narrow the query at all.
    /// </summary>
    private bool _namespacesListed;

    /// <summary>The search box says it takes a name only when it does.</summary>
    public string NamespaceSearchPlaceholder =>
        !_namespacesListed && !IsDemo ? "Filter, or type a namespace…" : "Filter namespaces…";

    internal void MarkNamespacesListed()
    {
        _namespacesListed = true;
        OnPropertyChanged(nameof(NamespaceSearchPlaceholder));
    }

    partial void OnNamespaceFilterChanged(string value) => RebuildNamespaceChoices();

    internal void RebuildNamespaceChoices()
    {
        var filter = (NamespaceFilter ?? "").Trim();
        var canType = !_namespacesListed && !IsDemo;
        IEnumerable<string> source = canType ? NamespaceOptions.Concat(_recentNamespaces) : NamespaceOptions;
        var names = source.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).ToArray();
        FilteredNamespaces.Clear();

        // First, so Enter takes it: a typed name is the most specific thing on screen.
        if (canType && IsNamespaceName(filter) && !names.Contains(filter, StringComparer.Ordinal))
            FilteredNamespaces.Add(new(filter, IsRecent: false, IsTyped: true));

        foreach (var name in names.OrderBy(n => n == AllNamespaces ? -2
                     : _recentNamespaces.IndexOf(n) is var index && index >= 0 ? index : int.MaxValue)
                     .ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
            FilteredNamespaces.Add(new(name, _recentNamespaces.Contains(name) && name != AllNamespaces));
        NamespaceCandidate = FilteredNamespaces.FirstOrDefault(n => n.IsTyped)
            ?? FilteredNamespaces.FirstOrDefault(n => n.Name == SelectedNamespace)
            ?? FilteredNamespaces.FirstOrDefault();
        OnPropertyChanged(nameof(NoNamespaceMatches));
    }

    /// <summary>
    /// Opens the picker's choice. A typed or recent namespace the cluster never listed
    /// joins the options first, so the selection is always one the picker can show.
    /// </summary>
    internal void ChooseNamespace(NamespaceChoice choice)
    {
        if (!NamespaceOptions.Contains(choice.Name))
        {
            NamespaceOptions.Add(choice.Name);
        }

        SelectedNamespace = choice.Name;
    }

    // RFC 1123 label, which is what the API server accepts as a namespace name. Offering
    // anything else would open a list that can only fail.
    private static bool IsNamespaceName(string value) =>
        value.Length is > 0 and <= 63 && NamespaceNamePattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex NamespaceNamePattern();

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

/// <param name="IsTyped">
/// The name in the search box, offered because the cluster's namespaces could not be
/// listed — the picker says so rather than presenting it as a known namespace.
/// </param>
public sealed record NamespaceChoice(string Name, bool IsRecent, bool IsTyped = false);
