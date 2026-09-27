using System.Collections;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;

namespace KubeNimbus.App.Views;

/// <summary>
/// Keeps an inspector pane's <see cref="DataGrid"/> selection and its view model's
/// selected-row property in step — in place of a two-way <c>SelectedItem</c> binding, which
/// loses the selection whenever the pane's view is re-pointed (ENG-43).
/// </summary>
/// <remarks>
/// <para>
/// What goes wrong with the binding: switching the inspector to another tab changes the
/// pane view's <c>DataContext</c>, the grid's <c>ItemsSource</c> binding re-evaluates, and
/// the grid clears its selection on the way — writing that null back through the two-way
/// binding into the view model it was <em>leaving</em>. On the way back the pane opened
/// with nothing selected, so L, S and Enter had nothing to act on. <c>ListBox</c> does not
/// do this (a <c>SelectingItemsControl</c> defers selection while its data context is
/// updating); Avalonia's <c>DataGrid</c> is not one.
/// </para>
/// <para>
/// So the grid-to-view-model half ignores a selection change that is an artefact of the
/// source changing: one that removes rows the current view model does not have and adds
/// none, or one that arrives while the data context is some other pane's. A deselection
/// someone made with the pointer removes a row the view model <em>does</em> have, and is
/// passed on. The view-model-to-grid half re-applies the view model's selection after
/// every data-context change, once the bindings have caught up.
/// </para>
/// </remarks>
internal sealed class GridSelectionSync<TViewModel, TItem>
    where TViewModel : class, INotifyPropertyChanged
    where TItem : class
{
    private readonly DataGrid _grid;
    private readonly Func<TViewModel, IList> _items;
    private readonly Func<TViewModel, TItem?> _get;
    private readonly Action<TViewModel, TItem?> _set;
    private readonly string _property;
    private TViewModel? _viewModel;

    private GridSelectionSync(
        Control view, DataGrid grid, Func<TViewModel, IList> items,
        Func<TViewModel, TItem?> get, Action<TViewModel, TItem?> set, string property)
    {
        _grid = grid;
        _items = items;
        _get = get;
        _set = set;
        _property = property;

        grid.SelectionChanged += OnGridSelectionChanged;
        view.DataContextChanged += (_, _) => Attach(view.DataContext as TViewModel);
        Attach(view.DataContext as TViewModel);
    }

    public static GridSelectionSync<TViewModel, TItem> Track(
        Control view, DataGrid grid, Func<TViewModel, IList> items,
        Func<TViewModel, TItem?> get, Action<TViewModel, TItem?> set, string property) =>
        new(view, grid, items, get, set, property);

    private void Attach(TViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelChanged;

        // After the bindings: the grid's ItemsSource follows the same data-context change,
        // and a selection set before it lands is cleared by it.
        Dispatcher.UIThread.Post(PushToGrid, DispatcherPriority.Loaded);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == _property)
        {
            PushToGrid();
        }
    }

    private void PushToGrid()
    {
        if (_viewModel is not { } viewModel || !ReferenceEquals(_grid.ItemsSource, _items(viewModel)))
        {
            return;
        }

        var selected = _get(viewModel);
        if (!ReferenceEquals(_grid.SelectedItem, selected))
        {
            _grid.SelectedItem = selected;
        }
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not { } viewModel || !ReferenceEquals(_grid.ItemsSource, _items(viewModel)))
        {
            return;
        }

        var items = _items(viewModel);
        if (e.AddedItems.Count == 0 && e.RemovedItems.Cast<object>().Any(removed => !items.Contains(removed)))
        {
            // The source changed under the grid (or the row was removed, which the view
            // model has already answered for itself): not a choice anyone made.
            return;
        }

        var selected = _grid.SelectedItem as TItem;
        if (!ReferenceEquals(_get(viewModel), selected))
        {
            _set(viewModel, selected);
        }
    }
}
