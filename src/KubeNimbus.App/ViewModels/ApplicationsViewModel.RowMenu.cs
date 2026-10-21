using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The list's row menu (2026-10-21, the owner's ask): Sync, Sync with prune…, Refresh and
/// Terminate sync… on an Argo CD app, Restart… on its workload, Show in Resources and Copy
/// name, without opening the page first. A right-click selects the row it lands on, and every
/// item acts on the selected row, as the Resources grid's menu does. The actions are the ones
/// the page offers, built by <see cref="ApplicationActions"/>, and they report on the list's
/// own strip above the rows: the page's strip is not on screen while the list is.
/// </summary>
public sealed partial class ApplicationsViewModel
{
    /// <summary>The list's action strip (UI rule 17): Restart's or Sync with prune's confirm, or a Sync's result line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingAction))]
    private RowActionViewModel? _pendingAction;

    public bool HasPendingAction => PendingAction is not null;

    /// <summary>The menu's Argo CD items are there only for an app Argo CD tracks, as the page's Sync button is.</summary>
    public bool SelectedIsArgo => SelectedRow is { IsArgo: true };

    private bool CanSyncSelected => SelectedRow is { } row && ApplicationActions.CanSync(this, row.Entry);

    private bool CanTerminateSelected => SelectedRow is { } row && ApplicationActions.CanTerminate(this, row.Entry);

    private bool CanRestartSelected => SelectedRow is { } row && ApplicationActions.CanRestart(this, row.Entry);

    private bool CanRevealSelected => SelectedRow is { } row && RevealTarget(row.Entry) is not null;

    [RelayCommand(CanExecute = nameof(CanSyncSelected))]
    private void SyncSelected() => ArmArgoOnSelectedRow(RowActionKind.ArgoSync);

    [RelayCommand(CanExecute = nameof(CanSyncSelected))]
    private void SyncSelectedWithPrune() => ArmArgoOnSelectedRow(RowActionKind.ArgoSyncPrune);

    [RelayCommand(CanExecute = nameof(CanSyncSelected))]
    private void RefreshSelected() => ArmArgoOnSelectedRow(RowActionKind.ArgoRefresh);

    [RelayCommand(CanExecute = nameof(CanTerminateSelected))]
    private void TerminateSelectedSync() => ArmArgoOnSelectedRow(RowActionKind.ArgoTerminate);

    [RelayCommand(CanExecute = nameof(CanRestartSelected))]
    private void RestartSelected()
    {
        if (SelectedRow is { } row && ApplicationActions.Restart(this, row.Entry) is { } action)
        {
            ArmOnList(action);
        }
    }

    /// <summary>
    /// The app's main workload in the Resources explorer; for an Argo CD app with no workload
    /// yet (a first sync that has not run), its Application object.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevealSelected))]
    private void ShowSelectedInResources()
    {
        if (SelectedRow is { } row
            && RevealTarget(row.Entry) is { } target
            && Tab.RevealInResources(target.Group, target.Kind, target.Namespace, target.Name))
        {
            SwitchToResources?.Invoke();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private async Task CopySelectedNameAsync()
    {
        if (SelectedRow is not { } row
            || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(row.Name);
    }

    private static (string Group, string Kind, string? Namespace, string Name)? RevealTarget(ApplicationEntry entry)
    {
        if (ApplicationActions.PrimaryWorkload(entry) is { } w)
        {
            return (ApplicationActions.GroupOf(w), w.Kind, w.Namespace, w.Name);
        }

        return entry.Argo is { } argo ? (ArgoCd.Group, "Application", argo.Namespace, argo.Name) : null;
    }

    /// <summary>An Argo CD action on the selected row, on the list's strip: the menu's items, and the palette's while the list shows.</summary>
    internal void ArmArgoOnSelectedRow(RowActionKind kind)
    {
        if (SelectedRow is { } row && ApplicationActions.Argo(this, row.Entry, kind) is { } action)
        {
            ArmOnList(action);
        }
    }

    private void ArmOnList(RowActionViewModel action)
    {
        if (PendingAction is { IsBusy: true })
        {
            return;
        }

        action.Dismissed = () =>
        {
            if (ReferenceEquals(PendingAction, action))
            {
                PendingAction = null;
            }
        };
        PendingAction = action;
        if (RowActionViewModel.FiresOnClick(action.Kind))
        {
            action.RunNow();
        }
    }

    /// <summary>
    /// The page has its own strip, so a finished result on the list's would be stale by the
    /// time the list comes back. One still in flight stays: its answer is still coming.
    /// </summary>
    private void DismissListActionForPage()
    {
        if (PendingAction is { IsBusy: false })
        {
            PendingAction = null;
        }
    }

    /// <summary>
    /// Re-evaluates the menu after a selection or a rebuild: Terminate follows Argo's phase,
    /// which the watch moves while the same row stays selected.
    /// </summary>
    private void NotifyRowMenu()
    {
        OnPropertyChanged(nameof(SelectedIsArgo));
        SyncSelectedCommand.NotifyCanExecuteChanged();
        SyncSelectedWithPruneCommand.NotifyCanExecuteChanged();
        RefreshSelectedCommand.NotifyCanExecuteChanged();
        TerminateSelectedSyncCommand.NotifyCanExecuteChanged();
        RestartSelectedCommand.NotifyCanExecuteChanged();
        ShowSelectedInResourcesCommand.NotifyCanExecuteChanged();
        CopySelectedNameCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRowChanged(ApplicationRowViewModel? value) => NotifyRowMenu();
}
