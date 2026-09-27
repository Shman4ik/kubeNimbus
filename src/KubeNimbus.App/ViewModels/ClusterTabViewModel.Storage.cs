using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// FEAT-47: a PersistentVolumeClaim opens the volume it is bound to, and a PersistentVolume
/// the claim that holds it — the "open the object it names" path owner chips, events and
/// env references already use, offered on the row's menu and in the palette.
/// </summary>
public sealed partial class ClusterTabViewModel
{
    /// <summary>The selected row's storage binding, or null when it is not a bound PV or PVC.</summary>
    private BoundObject? SelectedBinding =>
        SelectedRow is { } row && DescriptorFor(row) is { } descriptor
            ? StorageBinding.For(descriptor, row.Resource)
            : null;

    /// <summary>"Open volume pvc-…" / "Open claim payments/data-0" — the menu item names what it opens.</summary>
    public string? BoundObjectLabel => SelectedBinding is { } bound ? $"Open {bound.Description}" : null;

    public bool CanOpenBoundObject => SelectedBinding is not null;

    [RelayCommand(CanExecute = nameof(CanOpenBoundObject))]
    private Task OpenBoundObjectAsync()
    {
        if (SelectedRow is not { } row || SelectedBinding is not { } bound)
        {
            return Task.CompletedTask;
        }

        // The row's own cluster and client, as for every other navigation out of a row:
        // in a fleet list the claim and its volume live on the cluster the row came from.
        return OpenOwnerAsync(
            new OwnerRef("v1", bound.Kind, bound.Name, Uid: null, Controller: false),
            bound.Namespace, row.ClusterName, ClientFor(row));
    }

    private void NotifyStorageBinding()
    {
        OnPropertyChanged(nameof(BoundObjectLabel));
        OnPropertyChanged(nameof(CanOpenBoundObject));
        OpenBoundObjectCommand.NotifyCanExecuteChanged();
    }
}
