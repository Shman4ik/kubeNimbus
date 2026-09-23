using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>Which part of the palette a row belongs to — see <see cref="CommandPaletteViewModel.LogsPrefix"/>.</summary>
public enum PaletteScope
{
    General,
    Logs,
}

/// <summary>
/// One palette row. <paramref name="Execute"/> is null for a <em>note</em>: a row that
/// states something (the pods are still loading, the list was refused, a cap was hit)
/// rather than doing something. Notes render disabled, are never selected, and Enter on
/// one does nothing — a palette that only ever shows what it can run has no way to say
/// why the thing you typed is not there.
/// </summary>
public sealed record PaletteItem(string Title, string Subtitle, string IconKey, Action? Execute)
{
    /// <summary>
    /// What the query is matched against, when that should not be the title and subtitle.
    /// The log rows use it to match on what identifies an object (name, namespace,
    /// cluster) and not on its status — the list search's own rule (UI rule 13), for the
    /// same reason: "Running" would otherwise match most of a healthy namespace.
    /// </summary>
    public string? SearchText { get; init; }

    public PaletteScope Scope { get; init; } = PaletteScope.General;

    /// <summary>
    /// For the one row whose action is to narrow the palette rather than leave it (the
    /// "find logs…" entry, which switches the query to the logs prefix). Everything else
    /// closes the palette when it runs.
    /// </summary>
    public bool KeepsPaletteOpen { get; init; }

    public bool IsActionable => Execute is not null;

    public bool IsNote => Execute is null;

    /// <summary>A stated, non-actionable row.</summary>
    public static PaletteItem Note(
        string title, string subtitle, PaletteScope scope = PaletteScope.General, string iconKey = "AlertCircleIconGeometry") =>
        new(title, subtitle, iconKey, null) { Scope = scope };
}

/// <summary>
/// Ctrl/Cmd+K palette: filters a caller-supplied action list by substring match on
/// title/subtitle (or an item's own <see cref="PaletteItem.SearchText"/>).
///
/// <para>
/// <b>The source is re-read, not captured.</b> It is a function the shell evaluates on
/// every keystroke and on <see cref="Refresh"/>, so rows that arrive while the palette is
/// open — the log rows, listed from the cluster when it opens — join the list without the
/// query being touched and without the selection jumping back to the top. The source
/// itself never waits: anything that needs the network is started by <see cref="Opening"/>
/// and shows whatever it has so far, so typing is never blocked on a round trip.
/// </para>
/// </summary>
public sealed partial class CommandPaletteViewModel(Func<IEnumerable<PaletteItem>> itemSource) : ObservableObject
{
    /// <summary>
    /// The query prefix that narrows the palette to logs — what Ctrl/Cmd+Shift+L opens it
    /// with. A prefix in the query rather than a mode flag, so it is visible, editable and
    /// dismissable with Backspace, the way VS Code's <c>&gt;</c> and <c>@</c> are.
    /// </summary>
    public const string LogsPrefix = "logs ";

    /// <summary>How many rows are drawn. More than this is a "keep typing" note, not a longer list.</summary>
    public const int MaxRows = 50;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

    public ObservableCollection<PaletteItem> FilteredItems { get; } = [];

    [ObservableProperty]
    private PaletteItem? _selectedItem;

    /// <summary>
    /// Runs before the first render of every open. The shell starts the one-shot log
    /// listing here; it returns at once and the rows arrive through <see cref="Refresh"/>.
    /// </summary>
    public Action? Opening { get; set; }

    // A keystroke starts the selection over at the top match, as it always has: what
    // Enter runs should be the best answer to what was just typed.
    partial void OnQueryChanged(string value) => Rebuild(keepSelection: false);

    /// <summary>
    /// A note can be highlighted by nothing: the list's own selection, a click and the
    /// arrow keys all land here, and a selected note would make Enter silently do nothing.
    /// </summary>
    partial void OnSelectedItemChanged(PaletteItem? value)
    {
        if (value is { IsNote: true })
        {
            SelectedItem = FilteredItems.FirstOrDefault(i => i.IsActionable);
        }
    }

    public void Open() => Open("");

    /// <summary>Opens with a query already typed — <see cref="LogsPrefix"/> for the logs gesture.</summary>
    public void Open(string query)
    {
        IsOpen = true;
        Opening?.Invoke();

        // Assigning an unchanged query raises nothing, so the rebuild is explicit.
        Query = query;
        Rebuild(keepSelection: false);
    }

    public void Close() => IsOpen = false;

    /// <summary>
    /// Re-reads the source against the current query. Public so rows that arrive while the
    /// palette is open can join it: the highlighted row is kept when it is still there, so
    /// a row landing above it does not move Enter's target out from under the reader.
    /// </summary>
    public void Refresh() => Rebuild(keepSelection: true);

    private void Rebuild(bool keepSelection)
    {
        var previous = keepSelection ? SelectedItem : null;

        var raw = Query;
        var logsOnly = raw.StartsWith(LogsPrefix, StringComparison.OrdinalIgnoreCase);
        var q = (logsOnly ? raw[LogsPrefix.Length..] : raw).Trim();

        var matches = new List<PaletteItem>();
        var notes = new List<PaletteItem>();
        foreach (var item in itemSource())
        {
            if (logsOnly && item.Scope != PaletteScope.Logs)
            {
                continue;
            }

            if (item.IsNote)
            {
                notes.Add(item);
            }
            else if (q.Length == 0 || Matches(item, q))
            {
                matches.Add(item);
            }
        }

        // Notes speak when they are the answer to the search: always under the logs
        // prefix (that is the question they are about), and otherwise only when nothing
        // else matched — "checkout" finding nothing while the pods are still loading has
        // to say so, but a settled "no pods here" does not belong under every search for
        // "Preferences".
        var showNotes = logsOnly || (q.Length > 0 && matches.Count == 0);

        FilteredItems.Clear();
        if (showNotes)
        {
            foreach (var note in notes)
            {
                FilteredItems.Add(note);
            }
        }

        foreach (var item in matches.Take(MaxRows))
        {
            FilteredItems.Add(item);
        }

        if (matches.Count > MaxRows)
        {
            FilteredItems.Add(PaletteItem.Note(
                $"{matches.Count - MaxRows:N0} more match",
                "Keep typing to narrow the list",
                iconKey: "MagnifyIconGeometry"));
        }

        SelectedItem = (previous is null
                ? null
                : FilteredItems.FirstOrDefault(i => i.IsActionable && i.Title == previous.Title && i.Subtitle == previous.Subtitle))
            ?? FilteredItems.FirstOrDefault(i => i.IsActionable);
    }

    private static bool Matches(PaletteItem item, string q) =>
        item.SearchText is { } search
            ? search.Contains(q, StringComparison.OrdinalIgnoreCase)
            : item.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
              || item.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase);

    public void ExecuteSelected()
    {
        if (SelectedItem is { } item)
        {
            Execute(item);
        }
    }

    /// <summary>Runs a row — a click lands here with the row it was on. Notes do nothing.</summary>
    public void Execute(PaletteItem item)
    {
        if (item.Execute is not { } run)
        {
            return;
        }

        run();
        if (!item.KeepsPaletteOpen)
        {
            Close();
        }
    }

    public void MoveSelection(int delta)
    {
        var actionable = FilteredItems.Where(i => i.IsActionable).ToList();
        if (actionable.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? 0 : actionable.IndexOf(SelectedItem);
        index = Math.Clamp(index + delta, 0, actionable.Count - 1);
        SelectedItem = actionable[index];
    }
}
