using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// What the list's usage poller needs from the cluster tab that owns it: whether the usage
/// columns are on, whether the Nodes capacity strip is the list's, and the three things a
/// poll changes on the tab — the rows' samples, the columns, and the strip.
/// </summary>
internal interface IListMetricsHost
{
    /// <summary>The list's CPU/Memory columns are on; a poll starts only then.</summary>
    bool AreMetricsVisible { get; }

    /// <summary>The Nodes list's capacity strip is on screen, and says a refusal itself (UI rule 20).</summary>
    bool IsCapacityActive { get; }

    /// <summary>Pushes one poll's samples onto the rows (<see cref="ClusterTabViewModel.ApplyUsage"/>).</summary>
    void ApplyUsage(Dictionary<string, (long? Cpu, long? Memory)> byKey, CancellationToken token, string? refused);

    /// <summary>Takes the usage columns away: the list's usage was refused.</summary>
    void HideUsageColumns();

    /// <summary>No cluster in scope serves metrics.k8s.io: the columns go and the strip says so.</summary>
    void OnMetricsApiGone();
}

/// <summary>
/// Usage for the list's CPU/Memory columns (metrics.md): polling metrics.k8s.io beside the
/// list's watch, a refusal that hides the columns and says why, and the last poll kept for
/// rows that arrive after it. A child of <see cref="ClusterTabViewModel"/>, reached as
/// <see cref="ClusterTabViewModel.Metrics"/>. Whether the columns are on
/// (<see cref="ClusterTabViewModel.AreMetricsVisible"/>) and pushing a poll's samples onto the
/// rows stay the tab's, because the watch, the capacity strip and the sort all read them.
/// </summary>
public sealed partial class ListMetricsViewModel : ObservableObject
{
    private readonly IListMetricsHost _host;

    internal ListMetricsViewModel(IListMetricsHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>
    /// How often metrics are re-read, from settings. metrics.k8s.io aggregates over a
    /// ~30s window, so polling faster than the default buys no resolution; the setting
    /// exists for the other direction — a large cluster or a metered link, where this
    /// being the app's only poll makes it the only thing worth turning down.
    /// </summary>
    private static TimeSpan PollInterval =>
        TimeSpan.FromSeconds(App.LoadSettings().MetricsPollSeconds);

    /// <summary>
    /// The tab's cluster serves metrics.k8s.io, as probed at connect (<see cref="DetectAsync"/>)
    /// or set for the demo cluster; a poll that finds no cluster in scope serving it clears it.
    /// </summary>
    internal bool IsApiAvailable { get; set; }

    /// <summary>
    /// Metered kinds whose usage this user may not read (a 403 from metrics.k8s.io), with
    /// the server's sentence, so returning to the list does not ask again. Narrow RBAC is
    /// the expected case, and a refusal is an answer, not a blip (#310). Cleared on connect,
    /// the one moment the user's permissions can have changed under the app's feet.
    /// </summary>
    private readonly Dictionary<string, string> _refused = new(StringComparer.Ordinal);

    /// <summary>
    /// Plenty of clusters run without metrics-server. Probing once at connect
    /// (off the cached discovery catalog) keeps the usage columns out of the way
    /// entirely on those clusters instead of showing a column full of dashes.
    /// </summary>
    internal async Task DetectAsync(ClusterClient? client)
    {
        _refused.Clear();
        try
        {
            IsApiAvailable = client is not null && await client.IsMetricsApiAvailableAsync();
        }
        catch (Exception)
        {
            IsApiAvailable = false; // usage is supplementary; never fail the connect over it
        }
    }

    /// <summary>
    /// Polls usage for the visible list alongside its watch, on the same
    /// cancellation token — switching kind or namespace tears both down together.
    /// The metrics API has no watch endpoint (it's a point-in-time aggregate),
    /// so this is the one place the app polls rather than streams.
    /// </summary>
    internal void Start(
        ResourceDescriptor descriptor,
        IReadOnlyList<(string ClusterName, ClusterClient Client)> sources,
        IReadOnlyList<string?> namespaces,
        CancellationToken token)
    {
        if (!_host.AreMetricsVisible || sources.Count == 0)
        {
            return;
        }

        var pods = descriptor.Kind == "Pod";

        // Outside fleet mode a refusal is remembered for the tab's connection: the columns
        // stay hidden with the reason, and nothing is asked again (#310).
        var remember = sources.Count == 1 && sources[0].ClusterName.Length == 0;
        if (remember && _refused.TryGetValue(descriptor.Kind, out var known))
        {
            _host.ApplyUsage([], token, known);
            Refuse(descriptor.Kind, known, token, remember: false);
            return;
        }

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(PollInterval);
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
                            IsApiAvailable = false;
                            _host.OnMetricsApiGone();
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
                            _host.ApplyUsage(byKey, token, reason);
                            Refuse(descriptor.Kind, reason, token, remember);
                        });
                        return;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() => _host.ApplyUsage(byKey, token, refused));
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
    private string? _notice;

    /// <summary>
    /// The list's usage was refused: hide the columns and say why. Internal so the
    /// view-model tests can deliver a poll's refusal without a cluster.
    /// </summary>
    internal void Refuse(string kind, string reason, CancellationToken token, bool remember)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (remember)
        {
            _refused[kind] = reason;
        }

        _host.HideUsageColumns();
        Notice = _host.IsCapacityActive
            ? null
            : $"CPU and memory are hidden: this user may not read {kind.ToLowerInvariant()} usage"
              + (reason.Length > 0 ? $" ({reason})." : ".");
    }

    /// <summary>True when a refusal for <paramref name="kind"/> is remembered, so its poll does not start.</summary>
    internal bool IsRefused(string kind) => _refused.ContainsKey(kind);

    /// <summary>
    /// The last poll of the list's current watch, kept so a row that arrives after it takes
    /// its sample at once (ENG-55, #252) instead of reading "—" until the next poll, up to a
    /// poll interval later. Memory only, and dropped with the watch (<see cref="ForgetList"/>),
    /// so a sample never outlives the kind and namespaces it was read for.
    /// </summary>
    private Dictionary<string, (long? Cpu, long? Memory)>? _lastPoll;

    /// <summary>Keeps a poll that was applied to the rows, for the rows that arrive after it.</summary>
    internal void KeepLastPoll(Dictionary<string, (long? Cpu, long? Memory)> byKey) => _lastPoll = byKey;

    /// <summary>A new row takes the last poll's sample for its key, when that poll had one.</summary>
    internal void ApplyLastSample(string key, ResourceRowViewModel row)
    {
        if (_lastPoll is { } usage && usage.TryGetValue(key, out var sample))
        {
            row.ApplyUsage(sample.Cpu, sample.Memory);
        }
    }

    /// <summary>
    /// The list's watch stopped: its last poll and its notice were about that list, and go
    /// with it. A refusal is about the user, not the list, and stays remembered.
    /// </summary>
    internal void ForgetList()
    {
        _lastPoll = null;
        Notice = null;
    }
}
