using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>What a view needs of a <see cref="GridSort{T}"/>, without knowing its row type.</summary>
public interface IGridSort
{
    string? Column { get; }

    bool Descending { get; }

    event EventHandler? Changed;

    void Toggle(string column);

    string Header(string column, string label);
}

/// <summary>
/// A header-click sort for an inspector pane's live grid — workload detail's pods, node
/// detail's pods, the Helm and Argo CD lists — kept in the order it names while the watch
/// behind the grid adds, changes and removes rows.
/// </summary>
/// <remarks>
/// <para>
/// The resource list's rules, at a smaller scale (resource-grid-resize-sort.md): a click
/// sorts ascending, a second descending, a third returns to <paramref name="defaultOrder"/>;
/// the comparer for a column compares what the column means; and the sort is
/// <b>maintained</b> — <see cref="IndexFor"/> places a new row, <see cref="Reposition"/>
/// moves a changed one, and does nothing while it is still between its neighbours, so a
/// status refresh moves nothing under the pointer.
/// </para>
/// <para>
/// Unlike the resource list there is no separate visible collection: these grids show
/// every row they hold, and their default order is a real order (by name or key, which is
/// how the API server lists them), not arrival order, so a cleared sort has somewhere to
/// return to without a second list. The selection is put back after every move, because a
/// DataGrid may drop the selection of a row that is moved under it.
/// </para>
/// </remarks>
public sealed partial class GridSort<T>(
    ObservableCollection<T> items,
    IComparer<T> defaultOrder,
    Func<string, bool, IComparer<T>> comparerFor,
    Func<T?>? getSelected = null,
    Action<T?>? setSelected = null) : ObservableObject, IGridSort where T : class
{
    /// <summary>The column the rows are sorted by, or null for the default order.</summary>
    [ObservableProperty]
    private string? _column;

    [ObservableProperty]
    private bool _descending;

    /// <summary>Raised after every change of column or direction, for the view to redraw its arrow.</summary>
    public event EventHandler? Changed;

    public IComparer<T> Comparer => Column is { } column ? comparerFor(column, Descending) : defaultOrder;

    /// <summary>A header click: ascending, descending, then the default order.</summary>
    public void Toggle(string column)
    {
        if (Column != column)
        {
            Column = column;
            Descending = false;
        }
        else if (!Descending)
        {
            Descending = true;
        }
        else
        {
            Column = null;
            Descending = false;
        }

        Resort();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where a new row goes: after every row that sorts before it or level with it.</summary>
    public int IndexFor(T item)
    {
        var comparer = Comparer;
        int low = 0, high = items.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (comparer.Compare(items[middle], item) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Adds a row where the order puts it.</summary>
    public void Insert(T item) => items.Insert(IndexFor(item), item);

    /// <summary>After a row changed in place: moves it only if it is no longer between its neighbours.</summary>
    public void Reposition(T item)
    {
        var index = items.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        var comparer = Comparer;
        var fitsBefore = index == 0 || comparer.Compare(items[index - 1], item) <= 0;
        var fitsAfter = index == items.Count - 1 || comparer.Compare(item, items[index + 1]) <= 0;
        if (fitsBefore && fitsAfter)
        {
            return;
        }

        var selected = getSelected?.Invoke();
        items.RemoveAt(index);
        items.Insert(IndexFor(item), item);
        Restore(selected);
    }

    /// <summary>Orders every row again — a header click.</summary>
    public void Resort()
    {
        var selected = getSelected?.Invoke();
        var target = items.ToList();

        // Stable, so rows the comparer calls equal keep their places.
        var ordered = target.Select((item, index) => (item, index))
            .OrderBy(p => p.item, Comparer)
            .ThenBy(p => p.index)
            .Select(p => p.item)
            .ToList();
        ApplicationsViewModel.Sync(items, ordered);
        Restore(selected);
    }

    private void Restore(T? selected)
    {
        if (selected is not null && setSelected is not null && !ReferenceEquals(getSelected?.Invoke(), selected))
        {
            setSelected(selected);
        }
    }

    /// <summary>The header text with the arrow the resource list draws.</summary>
    public string Header(string column, string label) =>
        Column == column ? label + (Descending ? " ↓" : " ↑") : label;
}
