using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One tab in a cluster tab's inspector strip (pod detail / YAML editor / exec
/// / port-forward). <see cref="IsPreview"/> tabs are VS Code-style "quick peek"
/// tabs — Space replaces the current preview tab in place, while double-click
/// (or editing) promotes a tab to permanent, matching the UI rules in
/// CLAUDE.md ("Space = quick-peek" + "opening a resource never overwrites an
/// active editor tab" — the latter only ever applies to promoted tabs).
/// </summary>
public abstract partial class InspectorTabViewModelBase(string title) : ObservableObject
{
    [ObservableProperty]
    private string _title = title;

    [ObservableProperty]
    private bool _isPreview;

    /// <summary>True while this tab is the one shown in the dock — drives the active-tab
    /// highlight in the dock header. Kept in sync by <c>ClusterTabViewModel</c> whenever
    /// <c>SelectedInspectorTab</c> changes.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Stable identity used to find-and-reuse an already-open tab for the same object.</summary>
    public abstract string Key { get; }

    /// <summary>Cancels watches/sessions this tab owns (log follow, exec, port-forward). Called on close.</summary>
    public virtual Task OnClosingAsync() => Task.CompletedTask;
}
