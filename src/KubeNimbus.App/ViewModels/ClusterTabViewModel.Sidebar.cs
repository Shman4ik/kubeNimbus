using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App.ViewModels;

// The discovery-built sidebar: building it from the catalog, the Helm and Argo rows, the
// section chrome and its persisted expansion, the Recent section, the one-highlight rule
// (MarkSelectedKind) and the kind filter (UI rule 6).
public sealed partial class ClusterTabViewModel
{
    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        if (Client is null) return;
        try
        {
            await Client.GetResourceCatalogAsync(forceRefresh: true);
            await BuildSidebarAsync();
            ReconcileInitialPods();
        }
        catch (Exception ex) { ConnectionWarning = $"Could not refresh the resource catalog: {ex.Message}"; }
    }
    private async Task BuildSidebarAsync()
    {
        if (Client is null)
        {
            return;
        }

        var catalog = await Client.GetResourceCatalogAsync();
        RecordPodDescriptor("", catalog);
        var sections = new Dictionary<string, SidebarSectionViewModel>(StringComparer.Ordinal);
        foreach (var title in SidebarGrouping.SectionOrder)
        {
            sections[title] = new SidebarSectionViewModel(title);
        }

        foreach (var descriptor in catalog.OrderBy(d => d.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var title = SidebarGrouping.SectionFor(descriptor);
            sections[title].Kinds.Add(new SidebarKindViewModel(descriptor, SidebarGrouping.IconKeyFor(descriptor, title)));
        }

        SidebarSections.Clear();

        foreach (var title in SidebarGrouping.SectionOrder)
        {
            if (sections[title].Kinds.Count > 0)
            {
                SidebarSections.Add(sections[title]);
            }
        }

        SidebarGrouping.LabelAmbiguousKinds(SidebarSections);
        SidebarGrouping.AddArgoDashboard(SidebarSections, catalog);
        await AddHelmSectionIfPresentAsync();
        ApplySidebarChrome();

        // Last, so the Helm row is there to be matched. The entries are rebuilt against
        // this catalog's descriptor instances, never the ones being replaced.
        RestoreRecentKinds();
    }

    /// <summary>
    /// Re-derives the per-section display state that comes from the tab rather than
    /// from discovery — today just the kind-count badge. Called wherever the set of
    /// sections changes (sidebar rebuild, Recent rebuild) and when the advanced view
    /// is toggled, because a freshly constructed section defaults to the plain layout.
    /// </summary>
    internal void ApplySidebarChrome()
    {
        foreach (var section in SidebarSections)
        {
            // The badge stays on the switch. "How much is hiding in here?" is a question
            // about the catalog, so it belongs to the one control that governs the
            // catalog — unlike everything else the switch used to hide, which was in the
            // content area and had nothing to do with sidebar clutter.
            section.ShowKindCount = IsAdvancedView;

            // Wired here rather than at construction because this runs after every
            // rebuild, and a section built during one must not record its state while
            // the list is still half-assembled.
            section.ExpansionChanged = () =>
            {
                PersistExpandedSections();

                // Folding the section that holds the selected kind hands the highlight to
                // its Recent copy, and unfolding it takes it back (#317).
                MarkSelectedKind();
            };
        }

        // The advanced-view gate is per kind now (Nodes and Namespaces survive it), and
        // per-kind visibility is the filter's job, so the gate is derived there.
        ApplySidebarFilter();
    }

    /// <summary>
    /// Remembers which sidebar sections are open, so the choice survives a restart.
    /// Stores the whole set rather than a per-section flag: the list is read as "these
    /// are expanded, everything else is not", and an empty set means "nobody has said"
    /// so the built-in defaults still apply on a fresh install.
    ///
    /// <para>
    /// A filter's force-expansion is deliberately not recorded — <c>IsForceExpanded</c>
    /// is a separate property precisely so that typing in the filter box does not
    /// rewrite what someone chose to have open.
    /// </para>
    /// </summary>
    private void PersistExpandedSections()
    {
        var expanded = SidebarSections
            .Where(s => s.IsExpanded)
            .Select(s => s.Title)
            .ToList();

        App.Update(s => s with { ExpandedSidebarSections = expanded });
    }

    /// <summary>
    /// Adds the Helm section only when the cluster actually stores releases —
    /// a Helm entry on a cluster that has never seen Helm is exactly the kind of
    /// always-visible control the UI rules say to default to "no". The probe is
    /// one field-selected Secret page of one item at connect time (never a full
    /// decode); a release installed later in the session shows up on the next
    /// connect/reconnect.
    /// </summary>
    private async Task AddHelmSectionIfPresentAsync()
    {
        if (Client is null)
        {
            return;
        }

        try
        {
            if (!await Client.HasHelmReleasesAsync())
            {
                return;
            }

            var section = new SidebarSectionViewModel(SidebarGrouping.HelmSection);
            section.Kinds.Add(new SidebarKindViewModel(
                SidebarGrouping.HelmReleaseDescriptor, SidebarGrouping.IconKeyFor(SidebarGrouping.HelmSection)));
            SidebarSections.Add(section);
        }
        catch (Exception)
        {
            // No permission to list Secrets (a perfectly normal RBAC setup) —
            // then there's no Helm browsing to offer, and that's not an error.
        }
    }

    /// <summary>
    /// How many kinds the Recent section keeps. Small on purpose: it's a shortcut back
    /// to what you're working on right now, and a long list is just the sidebar again.
    /// </summary>
    private const int MaxRecentKinds = 5;

    /// <summary>
    /// Most-recent-first, deduplicated by (group, kind). Persisted per cluster in
    /// <c>workspace.json</c> (<see cref="WorkspaceSettings.RecentKinds"/>) and restored on
    /// connect (ENG-5): usage is open, look, close, so a Recent section that started empty
    /// on every launch was empty exactly when it would have saved a scroll.
    /// </summary>
    private readonly List<ResourceDescriptor> _recentKinds = [];

    /// <summary>True once <see cref="RestoreRecentKinds"/> has read the saved list.</summary>
    private bool _recentKindsRestored;

    /// <summary>
    /// Rebuilds Recent from the saved keys against the catalog just built. Keys, not
    /// descriptors, are what is saved, so every entry is this catalog's own instance —
    /// the thing a reconnect used to clear the history over — and a kind the cluster no
    /// longer serves (a CRD uninstalled since) is simply not restored.
    /// </summary>
    private void RestoreRecentKinds()
    {
        _recentKindsRestored = true;
        _recentKinds.Clear();
        var saved = WorkspaceStore.Load().RecentKinds?.GetValueOrDefault(NamespaceHistoryKey) ?? [];
        var catalog = SidebarSections
            .Where(s => s.Title != SidebarGrouping.RecentSection)
            .SelectMany(s => s.Kinds)
            .Where(k => !k.IsRecentEntry)
            .ToList();

        foreach (var key in saved)
        {
            if (catalog.FirstOrDefault(k => GridLayoutStore.KeyFor(k.Descriptor) == key)?.Descriptor is { } descriptor
                && !_recentKinds.Contains(descriptor))
            {
                _recentKinds.Add(descriptor);
            }

            if (_recentKinds.Count == MaxRecentKinds)
            {
                break;
            }
        }

        if (_recentKinds.Count > 0)
        {
            RebuildRecentSection();
        }
    }

    private void SaveRecentKinds()
    {
        var settings = WorkspaceStore.Load();
        var recent = new Dictionary<string, List<string>>(settings.RecentKinds ?? [], StringComparer.Ordinal)
        {
            [NamespaceHistoryKey] = [.. _recentKinds.Select(GridLayoutStore.KeyFor)],
        };
        WorkspaceStore.Save(settings with { RecentKinds = recent });
    }

    /// <summary>
    /// Pushes a kind to the top of the Recent section. Selecting a recent entry itself
    /// is ignored: reordering the list under the pointer that just clicked it makes the
    /// section unusable.
    /// </summary>
    private void RecordRecentKind(SidebarKindViewModel kind)
    {
        if (kind.IsRecentEntry)
        {
            return;
        }

        _recentKinds.RemoveAll(d =>
            string.Equals(d.Group, kind.Descriptor.Group, StringComparison.Ordinal)
            && string.Equals(d.Kind, kind.Descriptor.Kind, StringComparison.Ordinal));
        _recentKinds.Insert(0, kind.Descriptor);
        while (_recentKinds.Count > MaxRecentKinds)
        {
            _recentKinds.RemoveAt(_recentKinds.Count - 1);
        }

        RebuildRecentSection();

        // Not before the saved list has been read: on a real cluster the first Pods list
        // starts before discovery has finished (StartInitialPods), and saving then would
        // overwrite the whole history with that one entry.
        if (_recentKindsRestored)
        {
            SaveRecentKinds();
        }
    }

    /// <summary>
    /// Rebuilds the pinned Recent section from <see cref="_recentKinds"/>. The entries
    /// are second <see cref="SidebarKindViewModel"/> instances over the same descriptors
    /// — including the synthetic Helm one, whose <c>IsHelmReleases</c> check is by
    /// descriptor reference and so keeps working from a copy.
    /// </summary>
    private void RebuildRecentSection()
    {
        var section = SidebarSections.FirstOrDefault(s => s.Title == SidebarGrouping.RecentSection);
        if (section is null)
        {
            section = new SidebarSectionViewModel(SidebarGrouping.RecentSection);
            SidebarSections.Insert(0, section);
        }

        section.Kinds.Clear();
        foreach (var descriptor in _recentKinds)
        {
            var iconKey = ReferenceEquals(descriptor, SidebarGrouping.HelmReleaseDescriptor)
                ? SidebarGrouping.IconKeyFor(SidebarGrouping.HelmSection)
                : SidebarGrouping.IconKeyFor(descriptor, SidebarGrouping.SectionFor(descriptor));

            section.Kinds.Add(new SidebarKindViewModel(descriptor, iconKey)
            {
                IsRecentEntry = true,
                // Same-named kinds from different groups are exactly what this section
                // is most likely to hold two of, so always carry the group here.
                GroupLabel = descriptor.Group.Length > 0 ? descriptor.Group : "core",
            });
        }

        // A rebuild replaces the instances the filter had already classified — and,
        // the first time round, inserts a section that has never seen the tab's
        // display state.
        ApplySidebarChrome();

        // …and the highlight: the new Recent copy of the kind on screen has to light up
        // with its row.
        MarkSelectedKind();
    }

    /// <summary>
    /// Draws exactly one sidebar row as selected (ENG-26, #317): the selected kind's own row
    /// while it is on screen, and its copy in Recent only while the own row is not (its
    /// section folded, or hidden by the filter or the basic view). Derived from
    /// <see cref="SelectedKind"/> rather than set by whoever selects, because several things
    /// select — the sidebar, the Recent section, a restore, discovery replacing the early
    /// Pods entry, the screenshot harness — and a highlight set by only some of them is how
    /// two rows came to be lit at once. It used to light both the row and its Recent copy,
    /// which read as two selections to the eye and to UI Automation (two selected items in
    /// one list), and left the keyboard's way into the sidebar, which enters on the selected
    /// row, with two to choose from. By descriptor reference: a Recent entry holds the
    /// catalog's own instance, and two same-named kinds from different groups are
    /// different instances. Re-derived when a section folds or the filter changes, since
    /// either can move the highlight between the two copies.
    /// </summary>
    private void MarkSelectedKind()
    {
        var selected = SelectedKind?.Descriptor;
        SidebarKindViewModel? own = null;
        SidebarKindViewModel? recent = null;
        var ownShown = false;
        var recentShown = false;
        foreach (var section in SidebarSections)
        {
            foreach (var kind in section.Kinds)
            {
                if (selected is null || !ReferenceEquals(kind.Descriptor, selected))
                {
                    continue;
                }

                var shown = section.IsSectionVisible && section.ShowKinds && kind.IsVisible;
                if (kind.IsRecentEntry)
                {
                    if (recent is null)
                    {
                        recent = kind;
                        recentShown = shown;
                    }
                }
                else if (own is null)
                {
                    own = kind;
                    ownShown = shown;
                }
            }
        }

        // The own row when it is on screen; the Recent copy when only that one is; the own
        // row again when neither is, so a cleared filter or an unfolded section finds it lit.
        var lit = ownShown || !recentShown ? own ?? recent : recent;
        foreach (var section in SidebarSections)
        {
            foreach (var kind in section.Kinds)
            {
                kind.IsSelected = ReferenceEquals(kind, lit);
            }
        }
    }

    /// <summary>The sidebar's own row for a Recent entry's kind, if it still has one.</summary>
    private SidebarKindViewModel? CanonicalKind(SidebarKindViewModel recent) =>
        SidebarSections
            .Where(s => s.Title != SidebarGrouping.RecentSection)
            .SelectMany(s => s.Kinds)
            .FirstOrDefault(k => !k.IsRecentEntry && ReferenceEquals(k.Descriptor, recent.Descriptor));

    partial void OnSidebarFilterChanged(string value) => ApplySidebarFilter();

    [RelayCommand]
    private void ClearSidebarFilter() => SidebarFilter = "";

    /// <summary>
    /// Filters sidebar kinds by substring match on display name, live as the user
    /// types. A section with at least one match force-expands (without touching
    /// the user's own collapse choice, restored once the filter is cleared) so
    /// filtering never hides a result inside a collapsed section.
    /// </summary>
    private void ApplySidebarFilter()
    {
        // The filter TextBox's two-way binding can round-trip null on
        // control (re)creation — same reasoning as SelectedNamespace above.
        var query = (SidebarFilter ?? "").Trim();
        var filtering = query.Length > 0;

        foreach (var section in SidebarSections)
        {
            // The advanced-view gate. A filter is a deliberate search for one thing, so
            // it reaches into what the gate hides: a query that matches a kind and then
            // shows nothing is the "worse than no match" failure the palette's own rules
            // name. That makes the filter an input to the gate, which is why the gate is
            // derived here rather than only on a rebuild or a toggle.
            var gated = !IsAdvancedView && !filtering && SidebarGrouping.IsCuratedSection(section.Title);

            var anyMatch = false;
            var anyInBasicView = false;
            foreach (var kind in section.Kinds)
            {
                var match = !filtering || kind.Matches(query);
                var inBasicView = !gated || SidebarGrouping.IsShownInBasicView(kind.Descriptor);
                kind.IsVisible = match && inBasicView;
                anyMatch |= match;
                anyInBasicView |= inBasicView;
            }

            section.HasVisibleKinds = anyMatch;
            section.IsForceExpanded = filtering && anyMatch;
            section.IsHiddenByBasicView = gated && !anyInBasicView;
        }

        // What is on screen decides which copy of the selected kind is lit (#317).
        MarkSelectedKind();
    }
}
