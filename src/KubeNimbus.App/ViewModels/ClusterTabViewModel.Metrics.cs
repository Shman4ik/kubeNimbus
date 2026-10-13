namespace KubeNimbus.App.ViewModels;

// Usage for the list's CPU/Memory columns. The poller, the refusal it remembers and the last
// poll it keeps are ListMetricsViewModel (ENG-13, #361); this is the half that is the tab's:
// owning one, and applying a poll's numbers to the rows (metrics.md).
public sealed partial class ClusterTabViewModel : IListMetricsHost
{
    /// <summary>The list's usage poller and its status-bar notice.</summary>
    public ListMetricsViewModel Metrics { get; }

    bool IListMetricsHost.IsCapacityActive => _capacityActive;

    void IListMetricsHost.HideUsageColumns() => AreMetricsVisible = false;

    void IListMetricsHost.OnMetricsApiGone()
    {
        AreMetricsVisible = false;
        SetCapacityUsage(CapacityUsageState.NoMetricsApi);
    }

    void IListMetricsHost.ApplyUsage(
        Dictionary<string, (long? Cpu, long? Memory)> byKey, CancellationToken token, string? refused) =>
        ApplyUsage(byKey, token, refused);

    /// <summary>Pushes one poll's samples onto the matching rows; rows with no sample fall back to "—".</summary>
    /// <param name="refused">
    /// The server's sentence when this poll's node-usage read was refused (403). The poll's
    /// empty result is then not a measurement, and the capacity strip says "usage not
    /// permitted" rather than "no node has reported usage yet" (0.7.0 QA finding M-4).
    /// </param>
    internal void ApplyUsage(
        Dictionary<string, (long? Cpu, long? Memory)> byKey, CancellationToken token = default, string? refused = null)
    {
        // A poll that finished after its watch was replaced belongs to a list no longer on screen.
        if (token.IsCancellationRequested)
        {
            return;
        }

        Metrics.KeepLastPoll(byKey);
        if (_capacityActive)
        {
            (_capacityUsage, _capacityUsageReason) = refused is null
                ? (CapacityUsageState.Available, null)
                : (CapacityUsageState.Refused, refused);
        }

        foreach (var (key, row) in _rowsByKey)
        {
            if (byKey.TryGetValue(key, out var sample))
            {
                row.ApplyUsage(sample.Cpu, sample.Memory);
            }
            else
            {
                row.ClearUsage();
            }
        }

        // A poll rewrites the very values a CPU- or memory-sorted list is ordered by, and
        // it rewrites every row at once — so unlike a watch tick, which changes one row,
        // this re-orders the whole list — keeping the selection and the scroll offset, so a
        // CPU-sorted list does not jump under the reader every 15 seconds (see
        // ResortVisibleRows).
        if (SortColumnId is ResourceColumn.Cpu or ResourceColumn.Memory)
        {
            ResortVisibleRows();
        }

        RefreshCapacity();
    }
}
