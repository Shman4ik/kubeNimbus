using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// An inspector tab that reads its object off a live list's <see cref="ResourceRowViewModel"/>
/// rather than through a watch of its own: the pod and node panes. The list keeps such a tab
/// current (<c>ClusterTabViewModel.LiveTabs</c>).
/// </summary>
/// <remarks>
/// A list builds new row objects whenever it starts over (a Refresh, a namespace change, coming
/// back to the kind), so a tab that held on to the row it was opened from stopped updating at the
/// first Refresh, and reopening the pod found that stale tab by key. The list hands the tab each
/// new row of the same object instead (<see cref="Rebind"/>), and tells it when the object is gone
/// (<see cref="MarkDeleted"/>), which the pane says the way the Service pane does.
/// </remarks>
internal interface ILiveRowTab
{
    /// <summary>The row the tab reads.</summary>
    ResourceRowViewModel Row { get; }

    /// <summary>Whether the object is gone, as far as the list that holds it knows.</summary>
    bool IsDeleted { get; }

    /// <summary>Whether a list of <paramref name="descriptor"/> holds this tab's object.</summary>
    bool IsOf(ResourceDescriptor descriptor);

    /// <summary>Reads <paramref name="row"/> from now on, and clears <see cref="IsDeleted"/>.</summary>
    void Rebind(ResourceRowViewModel row);

    /// <summary>The object is gone: what the tab shows is its last state, and it says so.</summary>
    void MarkDeleted();
}
