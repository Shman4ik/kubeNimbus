using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// Usage for the list's CPU/Memory columns: polling metrics.k8s.io beside the watch, a refusal
// that hides the columns and says why, and applying a poll's numbers to the rows (metrics.md).
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// Polls usage for the visible list alongside its watch, on the same
    /// cancellation token — switching kind or namespace tears both down together.
    /// The metrics API has no watch endpoint (it's a point-in-time aggregate),
    /// so this is the one place the app polls rather than streams.
    /// </summary>
    private void StartMetricsPolling(
        ResourceDescriptor descriptor,
        IReadOnlyList<(string ClusterName, ClusterClient Client)> sources,
        IReadOnlyList<string?> namespaces,
        CancellationToken token)
    {
        if (!AreMetricsVisible || sources.Count == 0)
        {
            return;
        }

        var pods = descriptor.Kind == "Pod";

        // Outside fleet mode a refusal is remembered for the tab's connection: the columns
        // stay hidden with the reason, and nothing is asked again (#310).
        var remember = sources.Count == 1 && sources[0].ClusterName.Length == 0;
        if (remember && _metricsRefused.TryGetValue(descriptor.Kind, out var known))
        {
            ApplyUsage([], token, known);
            RefuseMetrics(descriptor.Kind, known, token, remember: false);
            return;
        }

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(MetricsPollInterval);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var byKey = new Dictionary<string, (long? Cpu, long? Memory)>(StringComparer.Ordinal);
                    var unavailable = 0;
                    var refusedCount = 0;
                    string? refused = null;

                    // One request per cluster in scope (exactly one outside fleet mode).
                    // Keys are cluster-qualified the same way the rows are, so a pod
                    // with the same namespace/name on two clusters stays two rows.
                    foreach (var (clusterName, client) in sources)
                    {
                        try
                        {
                            if (pods)
                            {
                                // One request per chosen namespace, as the watch reads them.
                                foreach (var @namespace in namespaces)
                                {
                                    foreach (var m in await client.GetPodMetricsAsync(@namespace, token))
                                    {
                                        byKey[ResourceRowViewModel.KeyFor(clusterName, m.Key)] = (m.CpuNanocores, m.MemoryBytes);
                                    }
                                }
                            }
                            else
                            {
                                var read = await client.ReadNodeUsageAsync(token);
                                switch (read.Outcome)
                                {
                                    case NodeUsageOutcome.Unavailable:
                                        unavailable++;
                                        break;
                                    case NodeUsageOutcome.Refused:
                                        // Narrow RBAC: the metrics API is there and this user may not
                                        // read node usage. The capacity strip says so (#206) with the
                                        // server's sentence, and the columns go (#310).
                                        refusedCount++;
                                        refused ??= read.Reason;
                                        break;
                                    default:
                                        foreach (var m in read.Nodes)
                                        {
                                            byKey[ResourceRowViewModel.KeyFor(clusterName, $"/{m.Name}")] = (m.CpuNanocores, m.MemoryBytes);
                                        }

                                        break;
                                }
                            }
                        }
                        catch (MetricsUnavailableException)
                        {
                            // Registered but not serving (metrics-server down), or absent
                            // entirely on this cluster.
                            unavailable++;
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            // This user may not read pod usage: an answer, which asking again
                            // every interval only repeats (#310).
                            refusedCount++;
                            refused ??= (ex as KubernetesApiException)?.ServerMessage ?? ex.Message;
                        }
                        catch (Exception) when (!token.IsCancellationRequested)
                        {
                            // Transient (throttling, a restarting metrics-server):
                            // keep the last sample on screen and retry next tick.
                        }
                    }

                    if (unavailable == sources.Count)
                    {
                        // No cluster in scope has a usable metrics API: stop asking and
                        // take the columns away rather than polling into the void.
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _metricsApiAvailable = false;
                            AreMetricsVisible = false;
                            SetCapacityUsage(CapacityUsageState.NoMetricsApi);
                        });
                        return;
                    }

                    if (refusedCount > 0 && refusedCount + unavailable == sources.Count)
                    {
                        // Every cluster in scope refused or has no metrics API: the columns
                        // would read "—" forever, so they go the way they go on a cluster
                        // without metrics-server, with the reason said, and the poll stops.
                        var reason = refused ?? "";
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            ApplyUsage(byKey, token, reason);
                            RefuseMetrics(descriptor.Kind, reason, token, remember);
                        });
                        return;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() => ApplyUsage(byKey, token, refused));
                    await timer.WaitForNextTickAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                // normal on kind/namespace switch or disconnect
            }
        }, token);
    }

    /// <summary>
    /// Why the list's CPU/Memory columns are hidden when it is not that the cluster has no
    /// metrics API: this user may not read the kind's usage (a 403, #310). Said in the
    /// status bar, because a column that is simply absent does not say why. Not on the Nodes
    /// list, whose capacity strip already says "usage not permitted" (UI rule 20).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusWorthShowing))]
    private string? _metricsNotice;

    /// <summary>
    /// The list's usage was refused: hide the columns and say why. Internal so the
    /// view-model tests can deliver a poll's refusal without a cluster.
    /// </summary>
    internal void RefuseMetrics(string kind, string reason, CancellationToken token, bool remember)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (remember)
        {
            _metricsRefused[kind] = reason;
        }

        AreMetricsVisible = false;
        MetricsNotice = _capacityActive
            ? null
            : $"CPU and memory are hidden: this user may not read {kind.ToLowerInvariant()} usage"
              + (reason.Length > 0 ? $" ({reason})." : ".");
    }

    /// <summary>True when a refusal for <paramref name="kind"/> is remembered, so its poll does not start.</summary>
    internal bool IsMetricsRefused(string kind) => _metricsRefused.ContainsKey(kind);

    /// <summary>
    /// The last poll of the list's current watch, kept so a row that arrives after it takes
    /// its sample at once (ENG-55, #252) instead of reading "—" until the next poll, up to a
    /// poll interval later. Memory only, and dropped with the watch: <see cref="StopWatch"/>
    /// clears it, so a sample never outlives the kind and namespaces it was read for.
    /// </summary>
    private Dictionary<string, (long? Cpu, long? Memory)>? _lastUsage;

    /// <summary>A new row takes the last poll's sample for its key, when that poll had one.</summary>
    private void ApplyLastUsage(string key, ResourceRowViewModel row)
    {
        if (_lastUsage is { } usage && usage.TryGetValue(key, out var sample))
        {
            row.ApplyUsage(sample.Cpu, sample.Memory);
        }
    }

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

        _lastUsage = byKey;
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
