using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>One sidebar section header (Workloads/Network/Config/Storage/Cluster/CRDs) with its resource kinds.</summary>
public sealed partial class SidebarSectionViewModel : ObservableObject
{
    public SidebarSectionViewModel(string title)
    {
        Title = title;
        IconKey = SidebarGrouping.IconKeyFor(title);

        // The remembered set wins over the built-in default, so someone who does live
        // in CRDs stops re-opening that section every session. An empty set means
        // "nobody has said" — not "expand nothing" — so it falls back rather than
        // collapsing all six, which is why this tests Count instead of Contains alone.
        var remembered = App.LoadSettings().ExpandedSidebarSections;
        _isExpanded = remembered.Count > 0
            ? remembered.Contains(title, StringComparer.Ordinal)
            : SidebarGrouping.IsExpandedByDefault(title);

        // KindCount is computed, so it raises nothing by itself — and the Recent
        // section is rebuilt in place on every kind selection. Without this the
        // badge latches whatever the count was when the section was constructed.
        Kinds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(KindCount));
    }

    public string Title { get; }

    public string IconKey { get; }

    public ObservableCollection<SidebarKindViewModel> Kinds { get; } = [];

    /// <summary>Config, Cluster and CRDs each dwarf the sections you actually browse —
    /// they start collapsed so a fresh connection doesn't open on a wall of kinds.
    /// See <see cref="SidebarGrouping.IsExpandedByDefault"/> for the counts behind that.
    /// Once the user collapses or expands anything, their set is remembered instead.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// Write-back for the remembered expansion set, set by <see cref="ClusterTabViewModel"/>
    /// as it builds the sidebar. Null while the sidebar is being (re)built, so a
    /// rebuild that sets every section's state in turn does not write six times and
    /// record a half-built set.
    /// </summary>
    public Action? ExpansionChanged { get; set; }

    /// <summary>True while a sidebar filter is active and this section has a match —
    /// force-expands the section without touching the user's own collapse choice.</summary>
    [ObservableProperty]
    private bool _isForceExpanded;

    /// <summary>False when a sidebar filter is active and no kind in this section matches.</summary>
    [ObservableProperty]
    private bool _hasVisibleKinds = true;

    public int KindCount => Kinds.Count;

    /// <summary>
    /// Whether the header carries its kind-count badge. Advanced-view only, and
    /// pushed down from <see cref="ClusterTabViewModel"/> rather than read from a
    /// global: the badge answers "how much is hiding in here?", which is a question
    /// you only ask once you're deliberately spelunking the catalog. Defaults to
    /// false so a section built outside a tab (the screenshot harness) matches the
    /// app's own default rather than the advanced layout.
    /// </summary>
    [ObservableProperty]
    private bool _showKindCount;

    public bool ShowKinds => IsExpanded || IsForceExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowKinds));

        // Records the whole set, not this one section: the stored list is read as
        // "these are expanded, everything else is not", so writing one entry in
        // isolation would leave the others ambiguous. ExpansionChanged is set by the
        // tab that owns the section list — a section still knows nothing about its
        // siblings, same arrangement as AdvancedViewChanged on the tab.
        ExpansionChanged?.Invoke();
    }

    partial void OnIsForceExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowKinds));

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>One browsable resource kind in the sidebar, built from discovery — never hardcoded.</summary>
public sealed partial class SidebarKindViewModel(ResourceDescriptor descriptor, string iconKey) : ObservableObject
{
    public ResourceDescriptor Descriptor { get; } = descriptor;

    public string IconKey { get; } = iconKey;

    public string DisplayName { get; } = Pluralize(descriptor);

    /// <summary>
    /// True for the synthetic Helm entry, which switches the content area to the
    /// release browser instead of starting a watch (Helm releases aren't an API
    /// kind — see <see cref="SidebarGrouping.HelmReleaseDescriptor"/>).
    /// </summary>
    public bool IsHelmReleases => ReferenceEquals(Descriptor, SidebarGrouping.HelmReleaseDescriptor);

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>False when a sidebar filter is active and this kind doesn't match it.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>
    /// API group shown beside the name, set only when another kind in the same
    /// section has the same <c>Kind</c> — "Backup" from velero.io and from
    /// postgresql.cnpg.io are different resources and must not render as two
    /// identical rows. Empty (and hidden) for unambiguous kinds, so the common
    /// case stays clean.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupLabel))]
    private string _groupLabel = "";

    public bool HasGroupLabel => GroupLabel.Length > 0;

    /// <summary>
    /// True for the copy of a kind that sits in the Recent section. The real sections
    /// hold the canonical instances; selecting a recent entry must not re-record it and
    /// churn the list it was clicked from.
    /// </summary>
    public bool IsRecentEntry { get; init; }

    /// <summary>
    /// Matches the sidebar filter against the display name, the API group and the
    /// server's short names. Group matters because two sections routinely show
    /// same-named kinds (Backup from velero.io and from postgresql.cnpg.io) and the
    /// group is the only thing that tells them apart — filtering by the label the row
    /// already displays has to work. Short names cover kubectl muscle memory: "svc",
    /// "po", "deploy".
    /// </summary>
    public bool Matches(string query) =>
        DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Descriptor.Group.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Descriptor.ShortNames.Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The sidebar label. The <b>server</b> decides whether a Kind is already plural,
    /// not a suffix rule: discovery hands back the plural for every kind, so a Kind
    /// whose plural is itself lowercased ("Endpoints" → <c>endpoints</c>) needs no
    /// suffix. The naive rule rendered that one "Endpointses", and would do the same
    /// to any CRD Kind that is already plural — which nothing in a hardcoded list of
    /// exceptions could ever cover, since the kinds come from the cluster.
    /// </summary>
    /// <summary>
    /// The row's label: the server's own plural, re-cased to the Kind's shape.
    ///
    /// It reads <see cref="ResourceDescriptor.Plural"/> rather than guessing, because
    /// guessing is wrong for a whole class of kinds and was: the previous version used
    /// the plural only to test equality with the Kind and then appended "s" (or "es"
    /// after s/x), which renders <c>NetworkPolicy</c> as "NetworkPolicys". Every Kind
    /// ending consonant+y is affected, and on a CRD-heavy cluster that is a lot of
    /// them — Policy, Registry, Gateway, Repository. The server has already answered
    /// this question correctly (<c>networkpolicies</c>), so the only job here is
    /// casing: discovery reports the plural lowercased, and the sidebar renders Kind
    /// casing.
    ///
    /// Re-casing is done by walking the Kind and the plural together while they agree
    /// case-insensitively, which restores the Kind's own capitalisation over the part
    /// they share and leaves the server's suffix alone — "NetworkPolicy" +
    /// "networkpolicies" gives "NetworkPolicies". A plural that shares no prefix with
    /// the Kind (nothing in Kubernetes does this, but a CRD may) falls back to the
    /// server's string as sent, which is still the truth about that resource.
    /// </summary>
    private static string Pluralize(ResourceDescriptor descriptor)
    {
        var kind = descriptor.Kind;
        var plural = descriptor.Plural;

        // Descriptors built by hand — the well-known statics, fixtures — may carry no
        // plural at all. Nothing to re-case against, so keep the Kind as written.
        if (string.IsNullOrEmpty(plural))
        {
            return kind;
        }

        if (string.Equals(plural, kind, StringComparison.OrdinalIgnoreCase))
        {
            return kind;
        }

        var shared = 0;
        while (shared < kind.Length
               && shared < plural.Length
               && char.ToLowerInvariant(kind[shared]) == char.ToLowerInvariant(plural[shared]))
        {
            shared++;
        }

        return shared == 0 ? plural : string.Concat(kind.AsSpan(0, shared), plural.AsSpan(shared));
    }
}
