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

    /// <summary>Back from the page: focus goes to the row it was opened from, so arrows and Enter keep working.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationsViewModel.IsPageOpen) && _subscribed is { IsPageOpen: false })
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

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only a double-click on a row opens it; the empty space below the last row is not
        // "the selected row".
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null)
        {
            return;
        }

        if (Vm is { } vm && vm.OpenSelectedCommand.CanExecute(null))
        {
            vm.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

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
