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
public abstract partial class InspectorTabViewModelBase : ObservableObject
{
    protected InspectorTabViewModelBase(string title, bool isDemo = false)
    {
        _title = title;
        IsDemo = isDemo;
    }

    /// <summary>
    /// True when this tab belongs to the built-in demo cluster, which has no
    /// <c>ClusterClient</c> behind it at all — every derived tab derives this from
    /// <c>client is null</c>, which is what makes "a demo tab never touches the
    /// network" an invariant the compiler helps hold rather than a rule to remember.
    ///
    /// Panes whose whole function needs a live cluster (exec, port-forward, apply,
    /// delete) use it to render an explicit "not available in the demo cluster" state
    /// and to disable their commands. Never a spinner that hangs, never a blank pane,
    /// never a silent no-op (UI rule 9).
    /// </summary>
    public bool IsDemo { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isPreview;

    /// <summary>True while this tab is the one shown in the dock — drives the active-tab
    /// highlight in the dock header. Kept in sync by <c>ClusterTabViewModel</c> whenever
    /// <c>SelectedInspectorTab</c> changes.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// Mirror of the shell's global "Advanced view" switch, pushed down by
    /// <c>ClusterTabViewModel</c> as a tab is opened and again whenever the switch
    /// moves. It lives on the base class rather than on each tab that happens to
    /// need it today because the alternative — a bespoke property per tab kind and
    /// a bespoke assignment per creation site — is exactly how a tab ends up
    /// shipping with the gate permanently open, which is what the YAML editor's
    /// force-apply button did before this existed.
    ///
    /// Bind it two-way to nothing: it is pushed, never edited from a tab.
    /// </summary>
    [ObservableProperty]
    private bool _isAdvancedView;

    /// <summary>Stable identity used to find-and-reuse an already-open tab for the same object.</summary>
    public abstract string Key { get; }

    /// <summary>Cancels watches/sessions this tab owns (log follow, exec, port-forward). Called on close.</summary>
    public virtual Task OnClosingAsync() => Task.CompletedTask;
}
