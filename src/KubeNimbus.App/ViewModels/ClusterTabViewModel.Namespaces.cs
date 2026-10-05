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

    // ------------------------------------------------------- several namespaces

    /// <summary>
    /// The namespaces chosen beside <see cref="SelectedNamespace"/>, which stays the first of
    /// them. Kept apart so every reader of <c>SelectedNamespace</c> that needs exactly one —
    /// the access review, a terminal's namespace, the palette's "Namespace:" rows — still
    /// gets one, and only the readers that list objects look at the whole set.
    /// </summary>
    private IReadOnlyList<string> _additionalNamespaces = [];

    // Set while SetNamespaces moves SelectedNamespace, so the change does not clear the
    // others it is setting at the same time.
    private bool _settingNamespaces;

    // Set while the picker's box toggles a namespace: the picker stays open, so its rows are
    // updated in place rather than rebuilt (a rebuild would move the recent namespaces to
    // the top under the pointer and take the keyboard's row away).
    private bool _togglingNamespace;

    /// <summary>Every chosen namespace, the first being <see cref="SelectedNamespace"/>; empty for All namespaces.</summary>
    public IReadOnlyList<string> SelectedNamespaces =>
        SelectedNamespace == AllNamespaces ? [] : [SelectedNamespace, .. _additionalNamespaces];

    /// <summary>
    /// What the picker's button, the loading line and the empty states call the selection:
    /// All namespaces, a name, two names, or "3 namespaces" (the names are in the tooltip).
    /// </summary>
    public string NamespaceDisplay => SelectedNamespaces switch
    {
        [] => AllNamespaces,
        [var one] => one,
        [var first, var second] => $"{first}, {second}",
        var many => $"{many.Count} namespaces",
    };

    public string NamespaceButtonTip => SelectedNamespaces.Count > 2
        ? $"Namespaces: {string.Join(", ", SelectedNamespaces)}. Click to change."
        : $"Choose a namespace: type to filter, click a row for it alone, its box or {Hotkeys.PrimaryLabel}+click to add it";

    /// <summary>
    /// Narrows the list to exactly these namespaces (none: all of them). The first in
    /// order becomes <see cref="SelectedNamespace"/>, so a list of one is the same state a
    /// single choice has always been, watch and all.
    /// </summary>
    public void SetNamespaces(IEnumerable<string> namespaces)
    {
        var wanted = namespaces
            .Where(ns => ns.Length > 0 && ns != AllNamespaces)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.SequenceEqual(SelectedNamespaces, StringComparer.Ordinal))
        {
            return;
        }

        foreach (var ns in wanted.Where(ns => !NamespaceOptions.Contains(ns)))
        {
            NamespaceOptions.Add(ns);
        }

        var primary = wanted.Count == 0 ? AllNamespaces : wanted[0];
        _settingNamespaces = true;
        try
        {
            _additionalNamespaces = [.. wanted.Skip(1)];
            if (primary != SelectedNamespace)
            {
                SelectedNamespace = primary;
                return;
            }
        }
        finally
        {
            _settingNamespaces = false;
        }

        // The first namespace is unchanged and only the others moved: nothing raised
        // SelectedNamespace's change, so the list is re-read here.
        OnNamespacesChanged();
    }

    /// <summary>The picker's box, Space or a Ctrl/Cmd+click: adds or removes a namespace and keeps the picker open.</summary>
    internal void ToggleNamespace(NamespaceChoice choice)
    {
        if (choice.Name == AllNamespaces)
        {
            SetNamespaces([]);
            return;
        }

        if (!NamespaceOptions.Contains(choice.Name))
        {
            NamespaceOptions.Add(choice.Name);
        }

        var chosen = SelectedNamespaces.ToList();
        if (!chosen.Remove(choice.Name))
        {
            chosen.Add(choice.Name);
        }

        _togglingNamespace = true;
        try
        {
            SetNamespaces(chosen);
        }
        finally
        {
            _togglingNamespace = false;
        }
    }

    /// <summary>The rows' checks follow the selection in place, so the row the keyboard is on keeps its focus.</summary>
    private void UpdateNamespaceChecks()
    {
        var chosen = SelectedNamespaces;
        foreach (var choice in FilteredNamespaces)
        {
            choice.IsChecked = choice.Name == AllNamespaces ? chosen.Count == 0 : chosen.Contains(choice.Name);
        }
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
        UpdateNamespaceChecks();
        NamespaceCandidate = FilteredNamespaces.FirstOrDefault(n => n.IsTyped)
            ?? FilteredNamespaces.FirstOrDefault(n => n.Name == SelectedNamespace)
            ?? FilteredNamespaces.FirstOrDefault();
        OnPropertyChanged(nameof(NoNamespaceMatches));
    }

    /// <summary>
    /// Opens the picker's choice, on its own: a click on a row, or Enter. A typed or recent
    /// namespace the cluster never listed joins the options first, so the selection is
    /// always one the picker can show.
    /// </summary>
    internal void ChooseNamespace(NamespaceChoice choice)
    {
        if (!NamespaceOptions.Contains(choice.Name))
        {
            NamespaceOptions.Add(choice.Name);
        }

        SetNamespaces(choice.Name == AllNamespaces ? [] : [choice.Name]);
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
        if (!_togglingNamespace)
        {
            RebuildNamespaceChoices();
        }
    }
}

/// <summary>
/// One row of the Resources namespace picker. <see cref="IsChecked"/> is written by the tab
/// only: the row draws a check rather than hosting a CheckBox, so a click on the box and a
/// click on the row cannot both act on it (UI rule 8b's double flip, by another route).
/// </summary>
/// <param name="IsTyped">
/// The name in the search box, offered because the cluster's namespaces could not be
/// listed — the picker says so rather than presenting it as a known namespace.
/// </param>
public sealed partial class NamespaceChoice(string Name, bool IsRecent, bool IsTyped = false) : ObservableObject, IEquatable<NamespaceChoice>
{
    public string Name { get; } = Name;

    public bool IsRecent { get; } = IsRecent;

    public bool IsTyped { get; } = IsTyped;

    /// <summary>The All namespaces row, which clears the selection and has no box.</summary>
    public bool IsAll => Name == ClusterTabViewModel.AllNamespaces;

    [ObservableProperty]
    private bool _isChecked;

    // Value equality on what the row is, as the record it used to be: the tests and the
    // candidate lookup compare rows by name and kind, not by instance.
    public bool Equals(NamespaceChoice? other) =>
        other is not null && Name == other.Name && IsRecent == other.IsRecent && IsTyped == other.IsTyped;

    public override bool Equals(object? obj) => Equals(obj as NamespaceChoice);

    public override int GetHashCode() => HashCode.Combine(Name, IsRecent, IsTyped);

    /// <summary>The row's accessible name, which says whether it is chosen: the check is drawn, not a control a reader can query.</summary>
    public override string ToString() =>
        (IsTyped ? $"Open {Name} by name" : IsRecent ? $"{Name}, recent" : Name) + (IsChecked && !IsAll ? ", chosen" : "");
}
