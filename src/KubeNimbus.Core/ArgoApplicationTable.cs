namespace KubeNimbus.Core;

/// <summary>
/// The Argo CD dashboard's live table: the Applications one list+watch has delivered, keyed
/// by <c>namespace/name</c>, folded from <see cref="ClusterClient.WatchResourceAsync"/>'s
/// events (#303). The dashboard used to read Applications once when it opened, so a sync
/// started elsewhere, a health change or a new Application showed only after a reload — the
/// one list in the app that was not a watch (hard rule 2).
/// </summary>
/// <remarks>
/// <para>
/// An Application nobody changed keeps the same <see cref="ArgoApplication"/> instance across
/// events, so the view can tell which rows a batch touched by reference and replace only those.
/// </para>
/// <para>
/// <b>A relist swaps at Synced</b> (#339). A Reset after the table has synced once (a relist
/// after 410 Gone, or Refresh) keeps the last complete list as <see cref="Applications"/> until
/// the new list's Synced, so a large relist does not show the old rows, then an empty grid, then
/// a growing subset. The first list still shows as it arrives: its first row ends the wait.
/// </para>
/// </remarks>
public sealed class ArgoApplicationTable
{
    private readonly Dictionary<string, ArgoApplication> _byKey = new(StringComparer.Ordinal);

    // The last complete list, shown while a relist is under way; null otherwise.
    private ArgoApplication[]? _previous;

    /// <summary>
    /// The list that followed the last Reset is complete. A Reset (the first list, or a relist
    /// after 410 Gone) clears it; Synced sets it. Until it is set an empty table is "still
    /// loading", never "no Applications" (UI rule 18).
    /// </summary>
    public bool IsSynced { get; private set; }

    /// <summary>A relist is under way and <see cref="Applications"/> is still the list before it.</summary>
    public bool IsRelisting => _previous is not null;

    public int Count => Applications.Count;

    /// <summary>What to show: the live list, or during a relist the last complete one.</summary>
    public IReadOnlyCollection<ArgoApplication> Applications => _previous ?? (IReadOnlyCollection<ArgoApplication>)_byKey.Values;

    /// <summary>Folds one event in. An object that cannot be read as an Application is skipped.</summary>
    public void Apply(ResourceEvent<DynamicResource> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        switch (change.Type)
        {
            case ResourceEventType.Reset:
                if (IsSynced)
                {
                    _previous = [.. _byKey.Values];
                }

                _byKey.Clear();
                IsSynced = false;
                return;
            case ResourceEventType.Synced:
                IsSynced = true;
                _previous = null;
                return;
            case ResourceEventType.Added or ResourceEventType.Modified when change.Resource is { } resource:
                var application = ArgoCd.ReadApplication(resource);
                _byKey[application.Key] = application;
                return;
            case ResourceEventType.Deleted when change.Resource is { } gone:
                _byKey.Remove($"{gone.Namespace}/{gone.Name}");
                return;
        }
    }
}
