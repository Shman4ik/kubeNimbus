using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// The live list's watches: Refresh, stopping and restarting the watch for the selected kind,
// the several-namespace watch, the demo dataset's rows and the fleet watch, and how each one
// ends (aot-watch-and-discovery.md, several-namespaces.md, fleet-views.md).
public sealed partial class ClusterTabViewModel
{
    [RelayCommand]
    private void Refresh()
    {
        if (IsHelmView)
        {
            _ = RefreshHelmReleasesAsync();
        }
        else if (IsArgoView)
        {
            // The dashboard reads the shared watch too: relist it, or a refresh is answered
            // with what the cache already holds.
            if (IsFleetView)
            {
                foreach (var target in _argoFleetTargets)
                {
                    target.Member.Client.Watches.Relist(target.Descriptor);
                }
            }
            else if (Client is { } argoClient && ArgoApplicationDescriptor() is { } applications)
            {
                argoClient.Watches.Relist(applications);
            }

            _ = ReloadArgoAsync();
        }
        else
        {
            // A refresh is a request to read the cluster again, which the shared watches would
            // otherwise answer with what they already hold.
            if (IsFleetView)
            {
                // Each cluster is read whole or in one namespace (StartFleetWatch), through its
                // own descriptor for the kind.
                var fleetNamespace = SelectedKind is { } fleetKind && WatchNamespaces(fleetKind.Descriptor) is [var one] ? one : null;
                foreach (var target in _fleetTargets.Values)
                {
                    target.Member.Client.Watches.Relist(target.Descriptor, fleetNamespace);
                }
            }
            else if (Client is { } client && SelectedKind is { } kind)
            {
                foreach (var ns in WatchNamespaces(kind.Descriptor))
                {
                    client.Watches.Relist(kind.Descriptor, ns);
                }
            }

            RestartWatch();
        }
    }

    /// <summary>A list watch is running (its token is live) — what the tests read.</summary>
    internal bool HasListWatch => _watchCts is not null;

    /// <summary>Cancels the current list watch (and the metrics poll riding on its token).</summary>
    private void StopWatch()
    {
        _lastUsage = null;
        MetricsNotice = null;
        _capacityActive = false;
        Capacity.Hide();
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;
    }

    private void RestartWatch()
    {
        StopWatch();

        // An armed scale/restart/delete is a question about the list that is being torn
        // down here (kind switched, namespace switched, fleet toggled). Its target row
        // is about to leave the screen, so the strip goes with it rather than lingering
        // over a list it no longer belongs to. A *completed* action's result strip is
        // dismissed the same way, which is correct: the answer was already read.
        PendingRowAction = null;

        // A refusal was about the list being torn down; the next one asks again (#339).
        ListRefusal = null;
        Rows.Clear();
        _rowsByKey.Clear();
        _fleetTargets.Clear();
        IsListLoading = (Client is not null || IsDemo) && SelectedKind is not null && !_reconnectFailed;
        RecomputeListEmpty();

        // After a failed reconnect the failure view stands in the list's place, and the client
        // is the one that just failed: a kind or namespace change made behind it starts nothing
        // (#294). The Reconnect that succeeds clears the flag and restarts the list itself.
        if ((Client is null && !IsDemo) || SelectedKind is null || _reconnectFailed)
        {
            AreMetricsVisible = false;
            PrinterColumns = [];
            return;
        }

        var descriptor = SelectedKind.Descriptor;
        var namespaces = WatchNamespaces(descriptor);
        var @namespace = namespaces is [var single] ? single : null;
        _pendingNamespaces.Clear();
        _watchedNamespaces.Clear();
        _refusedNamespaces.Clear();
        _refusedMembers.Clear();
        _fleetNamespaceFilter = null;

        if (Client is not { } client)
        {
            UpdatePrinterColumns(descriptor);

            // Only reachable on the demo cluster — the guard above returned for every
            // other tab with no client. Rows come from the shipped dataset; no watch,
            // no metrics poll, no socket.
            PopulateDemoRows(descriptor, namespaces);
            StartCapacity(null, CancellationToken.None);
            return;
        }

        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;

        // Fleet mode uses *this* tab's descriptor for the columns, deliberately. The
        // headers can only be one set, so they come from the cluster whose sidebar the
        // kind was selected in; every row is then evaluated against those same JSON
        // paths whatever cluster served it. A member serving an older version with a
        // different shape resolves to blank cells rather than to a wrong value, which
        // is the same outcome a missing field already has, and the alternative —
        // per-cluster headers — is not a thing a single table can render.
        UpdatePrinterColumns(descriptor);

        if (IsFleetView && FleetMembersProvider?.Invoke() is { Count: > 0 } members)
        {
            // In fleet mode metrics availability is per cluster and unknown up front,
            // so the columns go on for a metered kind and the poll takes them away
            // again if no cluster in scope actually serves metrics.k8s.io.
            AreMetricsVisible = IsMeteredKind(descriptor);
            StartFleetWatch(descriptor, members, namespaces, token);
            return;
        }

        AreMetricsVisible = _metricsApiAvailable && IsMeteredKind(descriptor);
        StartCapacity(client, token);

        if (namespaces.Count > 1)
        {
            StartNamespacesWatch(client, descriptor, namespaces, token);
            StartMetricsPolling(descriptor, [("", client)], namespaces, token);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Through the connection's shared watches: a kind the Applications mode already
                // reads, or one this list showed a moment ago, is given its rows at once.
                await foreach (var batch in client.Watches.WatchAsync(
                    descriptor, @namespace,
                    connectionLost: ex => Dispatcher.UIThread.Post(() =>
                    {
                        ConnectionWarning = ex.Message;

                        // After the warning, which resets it: a lost or refused watch is
                        // the one warning a reconnect can actually do something about. An
                        // object the app could not read is not — the watch is still up, and
                        // a reconnect would read the same object again.
                        ConnectionWarningOffersReconnect = ex is not UnreadableObjectException;
                    }),
                    cancellationToken: token).InBatches(cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyBatch(batch));
                }
            }
            catch (OperationCanceledException)
            {
                // normal when switching kind/namespace or disconnecting
            }
            catch (Exception ex)
            {
                // A watch that ended is not a watch that is still loading. Leaving the
                // spinner up here would turn a reported failure into a window that looks
                // busy forever, which is the same lie the empty-list state used to tell
                // in the other direction (UI rule 18).
                Dispatcher.UIThread.Post(() => EndListWatch(ex, token));
            }
        }, token);

        StartMetricsPolling(descriptor, [("", client)], namespaces, token);
    }

    /// <summary>
    /// The namespaces a list of this kind reads: none for a cluster-scoped kind or All
    /// namespaces (one read across the cluster, written as a single null), otherwise every
    /// chosen one.
    /// </summary>
    private IReadOnlyList<string?> WatchNamespaces(ResourceDescriptor descriptor) =>
        descriptor.Namespaced && SelectedNamespaces is { Count: > 0 } chosen ? [.. chosen] : [null];

    // The namespaces of a several-namespace watch that have not yet delivered their Synced.
    private readonly HashSet<string> _pendingNamespaces = new(StringComparer.Ordinal);

    /// <summary>
    /// Several namespaces: one list+watch each, merged (<see cref="NamespaceWatch"/>). The
    /// events carry their namespace, so a Reset clears only that namespace's rows, and the
    /// list stops waiting at its first row or once every namespace has answered — never at
    /// the first namespace to come back empty (UI rule 18).
    /// </summary>
    private void StartNamespacesWatch(
        ClusterClient client, ResourceDescriptor descriptor, IReadOnlyList<string?> namespaces, CancellationToken token)
    {
        var names = namespaces.OfType<string>().ToList();
        foreach (var ns in names)
        {
            _pendingNamespaces.Add(ns);
            _watchedNamespaces.Add(ns);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in NamespaceWatch.WatchAsync(
                    client, descriptor, names,
                    connectionLost: (ns, ex) => Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        ConnectionWarning = $"{ns}: {ex.Message}";
                        ConnectionWarningOffersReconnect = ex is not UnreadableObjectException;
                    }),

                    // A refusal (#339) ends that namespace's shared watch: it arrives here, and
                    // NamespaceWatch follows it with that namespace's Synced, so the list does
                    // not wait for it for ever.
                    sourceFailed: (ns, ex) => Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            EndNamespaceWatch(ns, ex);
                        }
                    }),
                    cancellationToken: token).InBatches(cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            ApplyNamespacedBatch(batch);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // normal when switching kind/namespace or disconnecting
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Status = $"Watch ended: {ex.Message}";
                    IsListLoading = false;
                    RecomputeListEmpty();
                });
            }
        }, token);
    }

    // Every namespace a several-namespace watch reads, and the ones that refused it (#354).
    private readonly HashSet<string> _watchedNamespaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _refusedNamespaces = new(StringComparer.Ordinal);

    /// <summary>
    /// One namespace's watch ended for good. A refusal is remembered, so that a list every
    /// chosen namespace refused says so in the list's place (<see cref="ListRefusal"/>) rather
    /// than "No Pods found" (UI rule 20, #354); a partial one stays a warning line.
    /// </summary>
    internal void EndNamespaceWatch(string @namespace, Exception ex)
    {
        _watchedNamespaces.Add(@namespace);
        if (WatchRefusedException.From(ex) is { } refused)
        {
            _refusedNamespaces[@namespace] = refused.Message;
        }

        ConnectionWarning = $"{@namespace}: {ex.Message}";
        ConcludeNamespaceRefusals();
        RecomputeListEmpty();
    }

    /// <summary>
    /// Once every namespace has answered: a list with no rows whose every namespace refused it
    /// is a refusal, stated once in the list's place; some refused and some not is a warning
    /// that stays, since the rows on screen are not every chosen namespace's.
    /// </summary>
    private void ConcludeNamespaceRefusals()
    {
        if (_pendingNamespaces.Count > 0 || _refusedNamespaces.Count == 0)
        {
            return;
        }

        if (Rows.Count == 0 && _watchedNamespaces.All(_refusedNamespaces.ContainsKey))
        {
            ListRefusalHint = "";
            ListRefusal = string.Join("\n\n", _refusedNamespaces.Values.Distinct(StringComparer.Ordinal));
            ConnectionWarning = null;
        }
        else
        {
            ConnectionWarning = string.Join("; ", _refusedNamespaces.Select(r => $"{r.Key}: {r.Value}"));
        }
    }

    /// <summary>One event of a several-namespace watch. Internal so the tests can post events the way the watch does.</summary>
    internal void ApplyNamespaced(NamespacedResourceEvent tagged)
    {
        ApplyNamespacedOne(tagged);
        ConcludeNamespaceRefusals();
        RecomputeListEmpty();
    }

    /// <summary>The several-namespace counterpart of <see cref="ApplyBatch"/>.</summary>
    internal void ApplyNamespacedBatch(IReadOnlyList<NamespacedResourceEvent> events)
    {
        _stagedRows = [];
        try
        {
            foreach (var evt in events)
            {
                ApplyNamespacedOne(evt);
            }
        }
        finally
        {
            FlushStagedRows();
            _stagedRows = null;
        }

        ConcludeNamespaceRefusals();
        RecomputeListEmpty();
    }

    private void ApplyNamespacedOne(NamespacedResourceEvent tagged)
    {
        switch (tagged.Event.Type)
        {
            case ResourceEventType.Reset:
            {
                // This namespace's rows only, in one pass and one notification — the fleet
                // view's cluster-scoped Reset, with the namespace in the cluster's place.
                _pendingNamespaces.Add(tagged.Namespace);
                _watchedNamespaces.Add(tagged.Namespace);
                FlushStagedRows();
                var stale = new HashSet<ResourceRowViewModel>();
                foreach (var key in _rowsByKey
                    .Where(entry => string.Equals(entry.Value.Namespace, tagged.Namespace, StringComparison.Ordinal))
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    if (_rowsByKey.Remove(key, out var row))
                    {
                        stale.Add(row);
                    }
                }

                if (stale.Count > 0)
                {
                    Rows.ReplaceAll(Rows.Where(row => !stale.Contains(row)).ToList());
                }

                if (Rows.Count == 0 && (_stagedRows?.Count ?? 0) == 0)
                {
                    IsListLoading = true;
                }

                break;
            }

            case ResourceEventType.Synced:
                _pendingNamespaces.Remove(tagged.Namespace);
                if (_pendingNamespaces.Count == 0)
                {
                    IsListLoading = false;
                    ConnectionWarning = null;
                }

                break;

            default:
                ApplyOne(tagged.Event);
                break;
        }
    }

    /// <summary>
    /// The demo counterpart of a list+watch: rows straight from the shipped dataset,
    /// no client and no watch. A kind the dataset has nothing for comes back empty and
    /// lands on the list's real "No &lt;kind&gt; found" state — most of a 100-kind
    /// catalog is like that, and it has to read as an empty namespace rather than as
    /// something broken (UI rule 9).
    /// </summary>
    private void PopulateDemoRows(ResourceDescriptor descriptor, IReadOnlyList<string?> namespaces)
    {
        var printerColumns = VisiblePrinterColumns;
        foreach (var resource in namespaces.SelectMany(ns => Demo.DemoData.ResourcesFor(descriptor, ns)))
        {
            var row = new ResourceRowViewModel(resource);
            row.SetPrinterColumns(printerColumns);
            _rowsByKey[resource.Key] = row;
            Rows.Add(row);
        }

        AreMetricsVisible = IsMeteredKind(descriptor);
        if (AreMetricsVisible)
        {
            // Through the real ApplyUsage, one stamped sample per simulated poll —
            // metrics.k8s.io has no history endpoint, so this is the only honest way to
            // give the sparklines a shape without a second charting code path.
            Demo.DemoUsage.SeedRows(Rows);
        }

        IsListLoading = false;
        RecomputeListEmpty();
    }

    /// <summary>
    /// Fleet mode: resolve the selected kind against every connected cluster's own
    /// discovery, then run one list+watch per cluster merged into this list.
    /// </summary>
    private void StartFleetWatch(
        ResourceDescriptor descriptor, IReadOnlyList<FleetMember> members, IReadOnlyList<string?> namespaces, CancellationToken token)
    {
        // Several namespaces across several clusters would be clusters × namespaces watches;
        // the fleet reads each cluster whole instead and keeps the chosen namespaces' rows
        // (ApplyFleetOne). One namespace, or none, is passed through as before.
        var @namespace = namespaces is [var single] ? single : null;
        _fleetNamespaceFilter = namespaces.Count > 1 ? [.. namespaces.OfType<string>()] : null;

        _ = Task.Run(async () =>
        {
            try
            {
                var targets = await ClusterFleet.ResolveAsync(
                    members, descriptor.Group, descriptor.Kind,
                    memberUnavailable: (member, ex) => Dispatcher.UIThread.Post(
                        () => ConnectionWarning = $"{member.ClusterName}: {ex.Message}"),
                    cancellationToken: token);

                // The Pod descriptor of each member, for the same reason the target
                // descriptor is per member: whether a node there can be drained is that
                // server's answer, not this tab's. The catalogs are already cached on
                // each client, so this costs nothing after the first list.
                var memberPodCatalogs = new List<(string ClusterName, IReadOnlyList<ResourceDescriptor> Catalog)>();
                foreach (var target in targets)
                {
                    memberPodCatalogs.Add((
                        target.Member.ClusterName,
                        await target.Member.Client.GetResourceCatalogAsync(token)));
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _fleetTargets.Clear();
                    foreach (var target in targets)
                    {
                        _fleetTargets[target.Member.ClusterName] = target;
                    }

                    foreach (var (clusterName, catalog) in memberPodCatalogs)
                    {
                        RecordPodDescriptor(clusterName, catalog);
                    }

                    FleetSummary = $"{targets.Count} of {members.Count} clusters serve {descriptor.Kind}";
                    if (targets.Count == 0)
                    {
                        IsListLoading = false;
                        RecomputeListEmpty();
                    }
                });

                if (targets.Count == 0)
                {
                    return;
                }

                StartMetricsPolling(
                    descriptor,
                    targets.Select(t => (t.Member.ClusterName, t.Member.Client)).ToArray(),
                    namespaces,
                    token);

                await foreach (var batch in ClusterFleet.WatchAsync(
                    targets, @namespace,
                    connectionLost: (member, ex) => Dispatcher.UIThread.Post(
                        () => ConnectionWarning = $"{member.ClusterName}: {ex.Message}"),
                    memberEnded: (member, ex) => Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            EndFleetMemberWatch(member.ClusterName, ex);
                        }
                    }),
                    cancellationToken: token).InBatches(cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyFleetBatch(batch));
                }

                // Every member's watch ended (each refused, #339, or failed for good): each said
                // so in the warning line, and nothing more is coming, so the wait is over.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        EndFleetWatch(targets.Count);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // normal when switching kind/namespace, leaving fleet mode, or disconnecting
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Status = $"Fleet watch ended: {ex.Message}";
                    IsListLoading = false;
                    RecomputeListEmpty();
                });
            }
        }, token);
    }

    // The chosen namespaces a fleet watch keeps, when it reads each cluster whole; null otherwise.
    private HashSet<string>? _fleetNamespaceFilter;

    // The fleet members whose watch was refused (#354), with the server's sentence.
    private readonly Dictionary<string, string> _refusedMembers = new(StringComparer.Ordinal);

    /// <summary>One fleet member's watch ended for good: said in the warning line, a refusal remembered.</summary>
    internal void EndFleetMemberWatch(string clusterName, Exception ex)
    {
        if (WatchRefusedException.From(ex) is { } refused)
        {
            _refusedMembers[clusterName] = refused.Message;
        }

        ConnectionWarning = $"{clusterName}: {ex.Message}";
    }

    /// <summary>
    /// Every member's watch of <paramref name="targets"/> members ended. With no rows and every
    /// member refused, the list states the refusal in its place, one line per cluster, rather
    /// than "No Pods found" (UI rule 20, #354); otherwise the wait simply ends.
    /// </summary>
    internal void EndFleetWatch(int targets)
    {
        if (Rows.Count == 0 && targets > 0 && _refusedMembers.Count >= targets)
        {
            ListRefusalHint = "";
            ListRefusal = string.Join("\n\n", _refusedMembers.Select(r => $"{r.Key}: {r.Value}"));
            ConnectionWarning = null;
        }

        IsListLoading = false;
        RecomputeListEmpty();
    }
}
