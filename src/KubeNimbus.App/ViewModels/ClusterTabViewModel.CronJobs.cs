using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// FEAT-8: a CronJob's run-now, suspend and resume, on the shared confirm strip (UI rule
/// 17) like every other mutating action. Kept in its own file because the capability
/// checks, the Job descriptor they depend on and the selected-row notifications are one
/// piece, and the main file is already the longest in the app.
/// </summary>
public sealed partial class ClusterTabViewModel
{
    /// <summary>Each cluster's <c>batch</c> Job descriptor, by cluster name ("" outside fleet mode).</summary>
    private readonly Dictionary<string, ResourceDescriptor> _jobDescriptors = new(StringComparer.Ordinal);

    /// <summary>The row whose object changes the CronJob and node menu items are watching.</summary>
    private ResourceRowViewModel? _watchedSelectedRow;

    /// <summary>Remembers a cluster's Job descriptor — what a run-now creates its Job through.</summary>
    private void RecordJobDescriptor(string clusterName, IReadOnlyList<ResourceDescriptor> catalog)
    {
        if ((catalog.FirstOrDefault(d => d is { Group: "batch", Kind: "Job", Version: "v1" })
            ?? catalog.FirstOrDefault(d => d is { Group: "batch", Kind: "Job" })) is { } jobs)
        {
            _jobDescriptors[clusterName] = jobs;
        }
    }

    private ResourceDescriptor? JobDescriptorFor(ResourceRowViewModel row) =>
        _jobDescriptors.TryGetValue(row.ClusterName, out var jobs)
            ? jobs
            : _jobDescriptors.GetValueOrDefault("");

    /// <summary>
    /// Run now is offered on an object with a Job template, on a cluster whose discovery
    /// lists a creatable Job kind — the object's evidence and the server's, never a list of
    /// kinds, so a CronJob-shaped CRD would qualify on the same terms.
    /// </summary>
    public bool CanTriggerSelectedRow =>
        SelectedRow is { } row && CronJobActions.SupportsTrigger(row.Resource, JobDescriptorFor(row));

    /// <summary>
    /// Suspend and resume share one menu slot, like cordon and uncordon (UI rule 11): which
    /// of the two is offered comes from the CronJob's own <c>spec.suspend</c>.
    /// </summary>
    public bool CanSuspendSelectedRow =>
        SelectedRow is { } row && DescriptorFor(row) is { } descriptor
        && CronJobActions.SupportsSuspend(descriptor, row.Resource)
        && !CronJobActions.IsSuspended(row.Resource);

    /// <inheritdoc cref="CanSuspendSelectedRow"/>
    public bool CanResumeSelectedRow =>
        SelectedRow is { } row && DescriptorFor(row) is { } descriptor
        && CronJobActions.SupportsSuspend(descriptor, row.Resource)
        && CronJobActions.IsSuspended(row.Resource);

    [RelayCommand(CanExecute = nameof(CanTriggerSelectedRow))]
    private void TriggerSelected() => ArmRowAction(RowActionKind.Trigger);

    [RelayCommand(CanExecute = nameof(CanSuspendSelectedRow))]
    private void SuspendSelected() => ArmRowAction(RowActionKind.Suspend);

    [RelayCommand(CanExecute = nameof(CanResumeSelectedRow))]
    private void ResumeSelected() => ArmRowAction(RowActionKind.Resume);

    /// <summary>
    /// Wires a freshly armed run-now's follow-up: "Open Job" opens the created Job's
    /// detail — its pods, live — against the row's own cluster.
    /// </summary>
    private void ConfigureCronJobAction(RowActionViewModel action, ResourceRowViewModel row, ClusterClient? client)
    {
        if (action.Kind != RowActionKind.Trigger || JobDescriptorFor(row) is not { } jobs)
        {
            return;
        }

        action.OpenJob = job => OpenRowAsync(new ResourceRowViewModel(job, row.ClusterName), preview: false, jobs, client);
    }

    /// <summary>
    /// The state-dependent menu items read the selected object, and the watch changes that
    /// object in place — a suspend that has just gone through, a node just cordoned — which
    /// no <c>SelectedRow</c> change reports. Without this the menu would keep offering
    /// Suspend on a CronJob the strip has just said is suspended.
    /// </summary>
    partial void OnSelectedRowChanged(ResourceRowViewModel? value)
    {
        if (_watchedSelectedRow is not null)
        {
            _watchedSelectedRow.PropertyChanged -= OnSelectedRowObjectChanged;
        }

        _watchedSelectedRow = value;
        if (value is not null)
        {
            value.PropertyChanged += OnSelectedRowObjectChanged;
        }

        NotifyStateDependentRowActions();
    }

    private void OnSelectedRowObjectChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResourceRowViewModel.Resource))
        {
            NotifyStateDependentRowActions();
        }
    }

    private void NotifyStateDependentRowActions()
    {
        OnPropertyChanged(nameof(CanTriggerSelectedRow));
        OnPropertyChanged(nameof(CanSuspendSelectedRow));
        OnPropertyChanged(nameof(CanResumeSelectedRow));
        OnPropertyChanged(nameof(CanCordonSelectedRow));
        OnPropertyChanged(nameof(CanUncordonSelectedRow));
        TriggerSelectedCommand.NotifyCanExecuteChanged();
        SuspendSelectedCommand.NotifyCanExecuteChanged();
        ResumeSelectedCommand.NotifyCanExecuteChanged();
        CordonSelectedCommand.NotifyCanExecuteChanged();
        UncordonSelectedCommand.NotifyCanExecuteChanged();

        // A claim that binds while it is selected gains its volume link the same way.
        NotifyStorageBinding();
    }
}
