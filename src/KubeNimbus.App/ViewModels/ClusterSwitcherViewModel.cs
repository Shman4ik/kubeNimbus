using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>Which bucket a switcher row belongs to — drives the section headers.</summary>
public enum ClusterSwitcherGroup
{
    Open,
    Pinned,
    Recent,
    All,
}

/// <summary>
/// One row in the switcher: either an already-open tab (switch to it) or a
/// kubeconfig context that isn't open yet (connect it in a new tab). The two are
/// deliberately the same list — "where do I want to be" is one question, and
/// making the user first work out whether that cluster is already open is the
/// two-step the old ComboBox+"+" pair forced.
/// </summary>
public sealed partial class ClusterSwitcherItemViewModel : ObservableObject
{
    public ClusterSwitcherItemViewModel(
        ClusterContext context,
        ClusterSwitcherGroup group,
        ClusterEnvironment environment,
        ClusterTabViewModel? openTab,
        bool isPinned,
        bool isCurrent)
    {
        Context = context;
        Group = group;
        Environment = environment;
        OpenTab = openTab;
        IsPinned = isPinned;
        IsCurrent = isCurrent;
    }

    public ClusterContext Context { get; }

    public ClusterSwitcherGroup Group { get; }

    public ClusterEnvironment Environment { get; }

    /// <summary>Non-null when this context already has a tab — selecting it switches rather than connects.</summary>
    public ClusterTabViewModel? OpenTab { get; }

    public bool IsOpen => OpenTab is not null;

    /// <summary>The tab this row is currently showing, so the switcher doesn't offer a no-op as the top hit.</summary>
    public bool IsCurrent { get; }

    [ObservableProperty]
    private bool _isPinned;

    public string Name => Context.Name;

    /// <summary>
    /// Section title, set only on the first row of each group so the whole list can
    /// be one flat ListBox. Grouping via nested ListBoxes would give each section its
    /// own selection, and they'd clear each other's the moment one was bound to a
    /// shared SelectedItem; a flat list also keeps arrow-key scroll-into-view working
    /// for free.
    /// </summary>
    public string? SectionHeader { get; set; }

    public bool HasSectionHeader => SectionHeader is not null;

    public string? EnvironmentLabel => Environment.Label();

    public bool HasEnvironmentLabel => EnvironmentLabel is not null;

    /// <summary>
    /// The context's provenance line. Which kubeconfig a context came from is the
    /// thing that disambiguates same-named contexts across merged files, and the
    /// default namespace is what people actually check before running anything.
    /// </summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (IsCurrent)
            {
                parts.Add("current");
            }
            else if (IsOpen)
            {
                parts.Add("open");
            }

            if (!string.IsNullOrWhiteSpace(Context.ClusterName) && Context.ClusterName != Context.Name)
            {
                parts.Add(Context.ClusterName);
            }

            if (!string.IsNullOrWhiteSpace(Context.Namespace))
            {
                parts.Add($"ns: {Context.Namespace}");
            }

            parts.Add(Path.GetFileName(Context.KubeconfigPath));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// Subsequence ("fuzzy") match, the thing every serious context switcher has
    /// and a ComboBox does not: "ppr" finds "payments-prod". Returns a rank so
    /// better matches sort first — negative means no match.
    /// </summary>
    public int Score(string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        var name = Name;

        // A contiguous hit beats a scattered subsequence, and a prefix beats both.
        var index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index == 0)
        {
            return 1000;
        }

        if (index > 0)
        {
            return 800 - index;
        }

        if (Subsequence(name, query))
        {
            return 500;
        }

        // Last resort: the cluster name and kubeconfig file, so a context whose own
        // name is an opaque ARN is still reachable by the cluster behind it.
        if (Context.ClusterName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Context.KubeconfigPath.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 200;
        }

        return -1;
    }

    private static bool Subsequence(string text, string query)
    {
        var q = 0;
        foreach (var c in text)
        {
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(query[q]) && ++q == query.Length)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The cluster switcher popup (Ctrl/Cmd+P). Replaces the old top-bar ComboBox.
///
/// The ComboBox had three problems the research on every comparable tool points
/// at: it doesn't search (kubectx+fzf, kubeswitch's search index and k9s's <c>:ctx</c>
/// all exist because scrolling a context list stops working somewhere around a
/// dozen entries, and real estates run to hundreds — FreeLens has a bug report
/// about its cluster list silently capping at 63); it truncates the long
/// auto-generated names managed Kubernetes hands out, which is exactly where the
/// distinguishing part lives; and it was never actually a *switcher* — it only
/// chose what the "+" button would open, so switching to an already-open cluster
/// was a different gesture entirely.
///
/// This is one flat, ranked, fuzzy-searchable list over both open tabs and
/// unopened contexts, grouped Open / Pinned / Recent / All.
/// </summary>
public sealed partial class ClusterSwitcherViewModel(Func<IEnumerable<ClusterSwitcherItemViewModel>> itemSource)
    : ObservableObject
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

    /// <summary>
    /// Every visible row, flat and in render order. Section headers ride on the
    /// first row of each group (<see cref="ClusterSwitcherItemViewModel.SectionHeader"/>)
    /// rather than being separate items, so this doubles as the keyboard order.
    /// </summary>
    public ObservableCollection<ClusterSwitcherItemViewModel> Ordered { get; } = [];

    [ObservableProperty]
    private ClusterSwitcherItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Set by the shell; invoked when a row is chosen.</summary>
    public Action<ClusterSwitcherItemViewModel>? Activate { get; set; }

    partial void OnQueryChanged(string value) => Refresh();

    public void Open()
    {
        IsOpen = true;
        Query = "";
        Refresh();
    }

    public void Close() => IsOpen = false;

    public void Refresh()
    {
        Ordered.Clear();

        var query = Query.Trim();
        var scored = itemSource()
            .Select(item => (item, score: item.Score(query)))
            .Where(x => x.score >= 0)
            .ToList();

        // While searching, grouping just gets in the way of "type three letters,
        // press Enter" — collapse to one ranked list. With an empty query the
        // groups are the whole value, since they encode what you'd reach for.
        if (query.Length > 0)
        {
            var ranked = scored.OrderByDescending(x => x.score).ThenBy(x => x.item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.item).Take(50).ToList();
            AddSection("Results", ranked);
        }
        else
        {
            foreach (var (group, title) in (ReadOnlySpan<(ClusterSwitcherGroup, string)>)
                     [
                         (ClusterSwitcherGroup.Open, "Open"),
                         (ClusterSwitcherGroup.Pinned, "Pinned"),
                         (ClusterSwitcherGroup.Recent, "Recent"),
                         (ClusterSwitcherGroup.All, "All contexts"),
                     ])
            {
                AddSection(title, scored.Where(x => x.item.Group == group).Select(x => x.item).ToList());
            }
        }

        IsEmpty = Ordered.Count == 0;

        // Never land on the tab you're already looking at — the first Enter should
        // go somewhere. Only matters with an empty query, where "Open" leads.
        SelectedItem = Ordered.FirstOrDefault(i => !i.IsCurrent) ?? Ordered.FirstOrDefault();
    }

    private void AddSection(string title, IReadOnlyList<ClusterSwitcherItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        items[0].SectionHeader = title;
        foreach (var item in items)
        {
            Ordered.Add(item);
        }
    }

    public void ActivateSelected()
    {
        if (SelectedItem is { } item)
        {
            ActivateItem(item);
        }
    }

    /// <summary>
    /// Opens (or switches to) a specific row. The mouse path goes through here rather
    /// than <see cref="ActivateSelected"/> so the row that opens is the one that was
    /// clicked, not whatever selection happened to be current.
    /// </summary>
    public void ActivateItem(ClusterSwitcherItemViewModel item)
    {
        SelectedItem = item;
        Close();
        Activate?.Invoke(item);
    }

    public void MoveSelection(int delta)
    {
        if (Ordered.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? 0 : Ordered.IndexOf(SelectedItem);
        SelectedItem = Ordered[Math.Clamp(index + delta, 0, Ordered.Count - 1)];
    }
}
