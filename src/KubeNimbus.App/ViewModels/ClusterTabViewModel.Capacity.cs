using System.Net;
using Avalonia.Threading;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The capacity strip above the Nodes list (FEAT-25, #206). The node half comes from the list's
/// own rows and the usage half from the list's own metrics poll, so the strip costs one request
/// of its own: the pods in every namespace, for the requests figure, read when the list opens and
/// every <see cref="CapacityPodsInterval"/> after on the list's token, so it stops with the list.
/// </summary>
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// How often the requests figure re-reads the pods. Requests move when pods are scheduled
    /// or deleted, not by the second, and a cluster-wide pod list is the largest read the app
    /// makes, so this is four metrics polls rather than one.
    /// </summary>
    internal static TimeSpan CapacityPodsInterval { get; set; } = TimeSpan.FromSeconds(60);

    public CapacityStripViewModel Capacity { get; } = new();

    private CapacityUsageState _capacityUsage;
    private string? _capacityUsageReason;
    private IReadOnlyCollection<DynamicResource>? _capacityPods;
    private CapacityPodsState _capacityPodsState;
    private string? _capacityPodsReason;
    private bool _capacityActive;

    /// <summary>The core Node kind, outside fleet mode: the one list the strip sits above.</summary>
    private bool IsCapacityList =>
        SelectedKind?.Descriptor is { Kind: "Node", Group: "" } && !IsFleetView;

    /// <summary>Called by <see cref="RestartWatch"/> with the new list's token, once its metrics state is known.</summary>
    private void StartCapacity(ClusterClient? client, CancellationToken token)
    {
        _capacityActive = IsCapacityList;
        _capacityPods = null;
        _capacityPodsReason = null;
        _capacityUsageReason = null;
        if (!_capacityActive)
        {
            Capacity.Hide();
            return;
        }

        _capacityUsage = AreMetricsVisible ? CapacityUsageState.Reading : CapacityUsageState.NoMetricsApi;
        _capacityPodsState = CapacityPodsState.Reading;

        if (client is null)
        {
            // The demo cluster: its dataset's pods and the usage DemoUsage seeded into the
            // rows, through the same summary (demo rule 4).
            _capacityUsage = AreMetricsVisible ? CapacityUsageState.Available : CapacityUsageState.NoMetricsApi;
            _capacityPods = Demo.DemoData.OfKind("Pod");
            _capacityPodsState = CapacityPodsState.Read;
            RefreshCapacity();
            return;
        }

        RefreshCapacity();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    IReadOnlyCollection<DynamicResource>? pods = null;
                    var state = CapacityPodsState.Read;
                    string? reason = null;
                    try
                    {
                        pods = await client.ListResourceOnceAsync(ResourceDescriptor.Pods, null, cancellationToken: token);
                    }
                    catch (KubernetesApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
                    {
                        state = CapacityPodsState.Refused;
                        reason = ex.ServerMessage ?? ex.Message;
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        state = CapacityPodsState.Failed;
                        reason = ex.Message;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() => ApplyCapacityPods(pods, state, reason, token));

                    // A refusal is an answer, not a hiccup: asking again every minute only repeats it.
                    if (state == CapacityPodsState.Refused)
                    {
                        return;
                    }

                    await Task.Delay(CapacityPodsInterval, token);
                }
            }
            catch (OperationCanceledException)
            {
                // the list moved on
            }
        }, token);
    }

    /// <summary>One read of the pods behind the requests figure, for the list whose token it carries.</summary>
    internal void ApplyCapacityPods(
        IReadOnlyCollection<DynamicResource>? pods, CapacityPodsState state, string? reason, CancellationToken token = default)
    {
        if (token.IsCancellationRequested || !_capacityActive)
        {
            return;
        }

        // A failed re-read keeps the figure the last good read gave, rather than trading a
        // number that was true a minute ago for a sentence about a blip.
        if (state == CapacityPodsState.Failed && _capacityPodsState == CapacityPodsState.Read)
        {
            return;
        }

        _capacityPods = pods;
        _capacityPodsState = state;
        _capacityPodsReason = reason;
        RefreshCapacity();
    }

    /// <summary>What the list's metrics poll learned about usage, for the strip.</summary>
    internal void SetCapacityUsage(CapacityUsageState state, string? reason = null)
    {
        if (!_capacityActive)
        {
            return;
        }

        _capacityUsage = state;
        _capacityUsageReason = reason;
        RefreshCapacity();
    }

    /// <summary>Re-words the strip from the rows as they are now. Cheap: a node list is short.</summary>
    private void RefreshCapacity()
    {
        if (!_capacityActive)
        {
            return;
        }

        var nodes = new List<DynamicResource>(Rows.Count);
        var byName = new Dictionary<string, ResourceRowViewModel>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            nodes.Add(row.Resource);
            byName[row.Resource.Name] = row;
        }

        Capacity.Update(
            nodes,
            name => byName.TryGetValue(name, out var row) && (row.LatestCpuNanocores is not null || row.LatestMemoryBytes is not null)
                ? (row.LatestCpuNanocores, row.LatestMemoryBytes)
                : null,
            _capacityUsage,
            _capacityUsageReason,
            _capacityPods,
            _capacityPodsState,
            _capacityPodsReason);
    }
}
