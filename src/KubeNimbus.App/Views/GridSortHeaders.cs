using Avalonia.Controls;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// Connects a <see cref="DataGrid"/>'s header clicks to a view model's <see cref="IGridSort"/>,
/// the way <c>ClusterTabView</c> connects the resource list to its own sort.
/// </summary>
/// <remarks>
/// <para>
/// Each sortable column carries its id in <c>Tag</c> and <c>CanUserSort="True"</c>, without
/// which Avalonia never raises <see cref="DataGrid.Sorting"/> for a template column
/// (resource-grid-resize-sort.md, rule 3). The grid's own sorting is cancelled: it would
/// order a collection view over the watch's collection, which the view model already keeps
/// in order.
/// </para>
/// <para>
/// The arrow is drawn into the header text, for the reason the resource list gives: Fluent's
/// sort pseudo-classes follow the collection view's sort descriptions, which stay empty here.
/// The pane's view is re-pointed at another pane's view model when the inspector switches
/// tabs, so the headers are redrawn on every data-context change, from that pane's sort.
/// </para>
/// </remarks>
internal sealed class GridSortHeaders
{
    private readonly Dictionary<DataGridColumn, string> _labels = [];
    private IGridSort? _sort;

    private GridSortHeaders(Control view, DataGrid grid, Func<object?, IGridSort?> sortOf)
    {
        foreach (var column in grid.Columns)
        {
            if (column.Tag is string && column.Header is string label)
            {
                _labels[column] = label;
            }
        }

        grid.Sorting += OnSorting;
        view.DataContextChanged += (_, _) => Attach(sortOf(view.DataContext));
        Attach(sortOf(view.DataContext));
    }

    public static GridSortHeaders Track(Control view, DataGrid grid, Func<object?, IGridSort?> sortOf) =>
        new(view, grid, sortOf);

    private void Attach(IGridSort? sort)
    {
        if (ReferenceEquals(_sort, sort))
        {
            return;
        }


        if (_sort is not null)
        {
            _sort.Changed -= OnChanged;
        }

        _sort = sort;
        if (sort is not null)
        {
            sort.Changed += OnChanged;
        }

        Redraw();
    }

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (_sort is { } sort && e.Column.Tag is string id)
        {
            sort.Toggle(id);
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        foreach (var (column, label) in _labels)
        {
            column.Header = _sort is { } sort && column.Tag is string id ? sort.Header(id, label) : label;
        }
    }
}
