using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Views;

/// <summary>
/// The Applications list's keys, which are the resource list's own (UI rule 13 and the
/// row keys): arrows move, Enter opens, <c>/</c> and Ctrl/Cmd+F go to the search box, and
/// Esc in the box clears it, then hands focus back to the rows.
/// </summary>
public partial class ApplicationsView : UserControl
{
    public ApplicationsView()
    {
        InitializeComponent();

        // Tunnel: ListBox consumes Enter and some letters on its own class handler before a
        // bubble handler would see them.
        AppList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Subscribe(DataContext as ApplicationsViewModel);
    }

    private ApplicationsViewModel? _subscribed;

    private void Subscribe(ApplicationsViewModel? vm)
    {
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnVmPropertyChanged;
        }

        _subscribed = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    /// <summary>
    /// Into the page: focus goes to the page, so Esc works at once. Back from it: focus goes
    /// to the row it was opened from, so arrows and Enter keep working.
    /// </summary>
    /// <remarks>
    /// The page's own <c>Focus()</c> on attach is not enough, and the harness cannot show why:
    /// it opens with Enter. A double-click opens the page from inside the second press, and
    /// the ListBoxItem under the pointer takes focus *after* that handler returns — so focus
    /// ended on a row the page had just hidden, Esc went to the hidden list, and the page
    /// never saw it. Posting at Background runs after the press and after layout has made the
    /// page visible.
    /// </remarks>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationsViewModel.IsPageOpen) && _subscribed is { IsPageOpen: true })
        {
            Dispatcher.UIThread.Post(() => FocusPage(), DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(ApplicationsViewModel.IsPageOpen) && _subscribed is { IsPageOpen: false })
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_subscribed?.SelectedRow is { } row)
                {
                    AppList.ScrollIntoView(row);
                }

                FocusList();
            }, DispatcherPriority.Background);
        }
    }

    private ApplicationsViewModel? Vm => DataContext as ApplicationsViewModel;

    /// <summary>Ctrl/Cmd+F, from the window. The page has no search box of its own list, so it is a no-op there.</summary>
    public void FocusFilter()
    {
        if (Vm is { IsListVisible: true })
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
        }
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (vm.IsFiltering)
            {
                vm.ClearFilterCommand.Execute(null);
            }
            else
            {
                FocusList();
            }

            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            vm.SelectedRow ??= vm.VisibleRows.FirstOrDefault();
            FocusList();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (vm.OpenSelectedCommand.CanExecute(null))
            {
                vm.OpenSelectedCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (CommandBindings.Matches(CommandId.FilterListFromRows, e))
        {
            FocusFilter();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsFiltering)
        {
            vm.ClearFilterCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// One click opens the row's page while <c>OpenApplicationsOnSingleClick</c> is on (the
    /// default), read at the click so a change on the preferences page applies to the next
    /// one. A click with a modifier only selects, as it would anywhere else.
    /// </summary>
    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && App.LoadSettings().OpenApplicationsOnSingleClick)
        {
            OpenTappedRow(e);
        }
    }

    /// <summary>
    /// Double-click opens whatever the preference says. With one click already opening, the
    /// second press normally lands on the page; the guard in <see cref="OpenTappedRow"/>
    /// covers the one where it still reaches the list.
    /// </summary>
    private void OnListDoubleTapped(object? sender, TappedEventArgs e) => OpenTappedRow(e);

    private void OpenTappedRow(TappedEventArgs e)
    {
        // Only a click on a row opens it; the empty space below the last row is not "the
        // selected row". The row comes from the item under the pointer, not the selection,
        // so a click opens what it landed on even before the ListBox has selected it.
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: ApplicationRowViewModel row }
            || Vm is not { IsPageOpen: false } vm)
        {
            return;
        }

        vm.Open(row);
        e.Handled = true;
    }

    internal bool FocusPage() =>
        _subscribed is { IsPageOpen: true }
        && PageHost.GetVisualDescendants().OfType<ApplicationPageView>().FirstOrDefault() is { } page
        && page.Focus();

    internal void FocusList()
    {
        if (AppList.SelectedItem is { } selected && AppList.ContainerFromItem(selected) is { } container)
        {
            container.Focus();
        }
        else
        {
            AppList.Focus();
        }
    }
}
