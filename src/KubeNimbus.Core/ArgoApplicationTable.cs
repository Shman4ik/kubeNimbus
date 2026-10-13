namespace KubeNimbus.Core;

/// <summary>
/// The Argo CD dashboard's live table: the Applications one list+watch has delivered, keyed
/// by <c>namespace/name</c>, folded from <see cref="ClusterClient.WatchResourceAsync"/>'s
/// events (#303). The dashboard used to read Applications once when it opened, so a sync
/// started elsewhere, a health change or a new Application showed only after a reload — the
/// one list in the app that was not a watch (hard rule 2).
/// </summary>
/// <remarks>
/// An Application nobody changed keeps the same <see cref="ArgoApplication"/> instance across
/// events, so the view can tell which rows a batch touched by reference and replace only those.
/// </remarks>
public sealed class ArgoApplicationTable
{
    private readonly Dictionary<string, ArgoApplication> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// The list that followed the last Reset is complete. A Reset (the first list, or a relist
    /// after 410 Gone) clears it; Synced sets it. Until it is set an empty table is "still
    /// loading", never "no Applications" (UI rule 18).
    /// </summary>
    public bool IsSynced { get; private set; }

    public int Count => _byKey.Count;

    public IReadOnlyCollection<ArgoApplication> Applications => _byKey.Values;

    /// <summary>Folds one event in. An object that cannot be read as an Application is skipped.</summary>
    public void Apply(ResourceEvent<DynamicResource> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        switch (change.Type)
        {
            case ResourceEventType.Reset:
                _byKey.Clear();
                IsSynced = false;
                return;
            case ResourceEventType.Synced:
                IsSynced = true;
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
