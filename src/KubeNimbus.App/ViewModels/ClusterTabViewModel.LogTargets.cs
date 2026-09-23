using Avalonia.Threading;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// "Open any pod's or workload's logs from anywhere": the palette's <c>Logs: …</c> rows,
/// and the one entry point every open-logs gesture goes through.
///
/// <para>
/// <b>A one-shot list per palette open, not a watch.</b> The palette is open for seconds;
/// a watch per tab for the palette's sake would be a second long-lived connection on every
/// cluster to serve a list nobody is looking at most of the time. When the tab is already
/// showing Pods, the list's own rows are the pods and only the workload kinds are fetched.
/// </para>
///
/// <para>
/// <b>Stale-while-loading, per scope.</b> The previous answer for the same namespace and
/// cluster set stays on screen while the next is in flight, under a note saying it is
/// being refreshed; an answer for a different scope is dropped rather than shown, because
/// "the pods of the namespace you were in last time" is not what the palette was asked.
/// </para>
/// </summary>
public sealed partial class ClusterTabViewModel
{
    private CancellationTokenSource? _logTargetsCts;

    /// <summary>The scope of the load in flight, or null when none is.</summary>
    private string? _logTargetsLoadingScope;

    /// <summary>The scope <see cref="_logTargets"/> answers.</summary>
    private string? _logTargetsScope;

    private LogTargetList? _logTargets;

    private IReadOnlyList<PaletteItem> _logTargetRows = [];

    /// <summary>
    /// Raised on the UI thread when a load lands, so an open palette re-reads its rows.
    /// A settable callback rather than an event, same as <see cref="AdvancedViewChanged"/>:
    /// the shell is the only listener, and it sets it once when the tab enters the strip.
    /// </summary>
    public Action? LogTargetsChanged { get; set; }

    /// <summary>The palette rows for the current scope — the last answer, possibly stale while a refresh runs.</summary>
    public IReadOnlyList<PaletteItem> LogTargetRows =>
        _logTargetsScope == LogTargetsScopeKey() ? _logTargetRows : [];

    /// <summary>Where the log rows stand, for the palette's notes.</summary>
    public LogTargetsState LogTargetsState => new(
        IsConnected: IsConnected,
        IsConnecting: IsConnecting,
        IsLoading: _logTargetsLoadingScope is not null,
        Scope: LogTargetLoader.Scope(LogTargetsNamespace),
        Result: _logTargetsScope == LogTargetsScopeKey() ? _logTargets : null);

    /// <summary>The namespace the rows come from: the tab's own selection, as the list uses it.</summary>
    private string? LogTargetsNamespace => SelectedNamespace == AllNamespaces ? null : SelectedNamespace;

    /// <summary>
    /// What makes two loads the same question: the namespace, and in fleet mode the
    /// membership (a cluster joining the fleet changes the answer).
    /// </summary>
    private string LogTargetsScopeKey()
    {
        var clusters = IsFleetView && FleetMembersProvider?.Invoke() is { Count: > 0 } members
            ? string.Join(",", members.Select(m => m.ClusterName))
            : "";
        return $"{clusters}|{SelectedNamespace}";
    }

    /// <summary>
    /// Starts a one-shot look at every pod and workload in scope, unless one for the same
    /// scope is already in flight. Returns immediately: the rows arrive through
    /// <see cref="LogTargetsChanged"/>. On the demo cluster the dataset is in memory and the
    /// answer lands before this returns.
    /// </summary>
    public void RequestLogTargets()
    {
        var scope = LogTargetsScopeKey();
        if (_logTargetsLoadingScope == scope)
        {
            return;
        }

        var sources = LogTargetSources();
        if (sources.Count == 0)
        {
            return;
        }

        _logTargetsCts?.Cancel();
        _logTargetsCts?.Dispose();
        var cts = new CancellationTokenSource();
        _logTargetsCts = cts;
        var @namespace = LogTargetsNamespace;

        if (IsDemo)
        {
            // Every task the demo source returns is already complete, so this finishes
            // synchronously — no pool thread and no dispatcher hop for data in memory.
            var demo = LogTargetLoader.LoadAsync(sources, @namespace, cancellationToken: cts.Token);
            if (demo.IsCompletedSuccessfully)
            {
                LandLogTargets(scope, demo.Result, LogPaletteRows.Build(demo.Result.Targets, OpenLogTarget));
                return;
            }
        }

        _logTargetsLoadingScope = scope;
        LogTargetsChanged?.Invoke();

        // Off the UI thread, rows included: a namespace of two thousand pods is two
        // thousand status summaries, and the first request can run an exec credential
        // plugin before it goes anywhere.
        _ = Task.Run(async () =>
        {
            LogTargetList result;
            IReadOnlyList<PaletteItem> rows;
            try
            {
                result = await LogTargetLoader.LoadAsync(sources, @namespace, cancellationToken: cts.Token);
                rows = LogPaletteRows.Build(result.Targets, OpenLogTarget);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loader turns every per-list failure into a problem row; anything that
                // still escapes is reported the same way rather than leaving "Loading…" up
                // for ever (UI rule 18: every wait ends, including the ones that end badly).
                result = new LogTargetList([], [new LogTargetProblem("couldn't list pods", ex.Message, false)], []);
                rows = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!cts.IsCancellationRequested)
                {
                    LandLogTargets(scope, result, rows);
                }
            });
        }, cts.Token);
    }

    private void LandLogTargets(string scope, LogTargetList result, IReadOnlyList<PaletteItem> rows)
    {
        _logTargetsLoadingScope = null;
        _logTargetsScope = scope;
        _logTargets = result;
        _logTargetRows = rows;
        LogTargetsChanged?.Invoke();
    }

    /// <summary>
    /// Fixture seam for the screenshot harness and the view-model tests: shows a given
    /// answer, or a load in flight over it, without listing anything. Any real load is
    /// cancelled first, so it cannot land over the fixture a moment later.
    /// </summary>
    internal void SetLogTargetsForFixture(LogTargetList? result, bool loading)
    {
        _logTargetsCts?.Cancel();
        _logTargetsCts?.Dispose();
        _logTargetsCts = null;
        var scope = LogTargetsScopeKey();
        _logTargetsLoadingScope = loading ? scope : null;
        _logTargetsScope = result is null ? null : scope;
        _logTargets = result;
        _logTargetRows = result is null ? [] : LogPaletteRows.Build(result.Targets, OpenLogTarget);
        LogTargetsChanged?.Invoke();
    }

    /// <summary>
    /// The clusters to list from: every fleet member while the list aggregates, the tab's
    /// own cluster otherwise, the dataset on the demo cluster. Built on the UI thread, which
    /// is where <see cref="Rows"/> may be read — the copy of the pods it hands on is what
    /// the pool thread sees.
    /// </summary>
    private List<LogTargetSource> LogTargetSources()
    {
        // The list's own rows are the pods when it is showing Pods for this scope and has
        // finished its first list — a half-loaded list would be read as a short namespace.
        var listIsPods = SelectedKind?.Descriptor is { Group: "", Kind: "Pod" }
                         && IsResourceListVisible
                         && !IsListLoading;

        IReadOnlyList<DynamicResource>? KnownPods(string clusterName) =>
            listIsPods
                ? [.. Rows.Where(r => r.ClusterName == clusterName).Select(r => r.Resource)]
                : null;

        if (IsDemo)
        {
            return [LogTargetSource.Demo(KnownPods(""))];
        }

        if (!IsConnected)
        {
            return [];
        }

        if (IsFleetView && FleetMembersProvider?.Invoke() is { Count: > 0 } members)
        {
            return [.. members.Select(m => LogTargetSource.For(m.ClusterName, m.Client, KnownPods(m.ClusterName)))];
        }

        return Client is { } client ? [LogTargetSource.For("", client, KnownPods(""))] : [];
    }

    private void OpenLogTarget(LogTarget target) => _ = OpenLogsForAsync(target);

    /// <summary>The target for a list row: its own kind, cluster and client.</summary>
    public LogTarget? LogTargetFor(ResourceRowViewModel row) =>
        DescriptorFor(row) is { } descriptor
            ? new LogTarget(row.Resource, descriptor, row.ClusterName, ClientFor(row))
            : null;

    /// <summary>
    /// The row's logs icon: selects the row (the icon swallows the press the grid would
    /// otherwise have selected it with, and the list should say which row the inspector is
    /// showing), then opens its logs through <see cref="OpenLogsForAsync"/> like every
    /// other route. <paramref name="maximized"/> is the Shift+click; a plain click leaves
    /// the choice to the "Open logs maximized" preference, exactly as L does.
    /// </summary>
    public Task OpenRowLogsAsync(ResourceRowViewModel row, bool maximized = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.HasLogs)
        {
            return Task.CompletedTask;
        }

        SelectedRow = row;
        return OpenLogsForRowAsync(row, previous: false, maximized: maximized ? true : null);
    }

    /// <summary>
    /// A list row's logs, whatever the row is: its own (a pod, or whatever names its pods),
    /// or — on an Event about a pod — that pod's, resolved first so a pod that has gone
    /// since the event was logged is stated in the list's warning rather than opening a
    /// pane over nothing. <paramref name="previous"/> applies to a pod row only.
    /// </summary>
    private async Task OpenLogsForRowAsync(ResourceRowViewModel row, bool previous, bool? maximized)
    {
        if (!LogTarget.HasOwnLogs(row.Resource) && LogTarget.InvolvedPod(row.Resource) is { } pod)
        {
            var @namespace = row.Resource.InvolvedObjectNamespace() ?? row.Namespace;
            if (await OpenNamedLogsAsync(pod, @namespace, row.ClusterName, ClientFor(row), maximized) is { } problem)
            {
                ConnectionWarning = problem;
            }

            return;
        }

        if (LogTargetFor(row) is { } target)
        {
            await OpenLogsForAsync(target, previous, maximized);
        }
    }

    /// <summary>
    /// The <see cref="OpenNamedLogs"/> an inspector pane is handed: bound to the cluster
    /// and client the pane's own object came from, so a fleet row's pane opens logs on
    /// that row's cluster — the same binding <see cref="OpenOwnerAsync"/> is given.
    /// </summary>
    private OpenNamedLogs NamedLogsOpener(string clusterName, ClusterClient? client) =>
        (target, namespaceHint, maximized) =>
            OpenNamedLogsAsync(target, namespaceHint, clusterName, client, maximized ? true : null);

    /// <summary>
    /// Opens the logs of an object some pane names — workload detail's and node detail's
    /// pod lists, an Argo Application's managed workloads, the pod an Event is about. The
    /// object is read first (the shipped dataset on the demo cluster), for two reasons: a
    /// pane that names an object does not always hold it (an Argo row is a kind and a
    /// name), and a pane's list can be older than the cluster (node detail's pods are one
    /// list, and an Event outlives its pod by an hour) — so "gone" is found out here and
    /// said, rather than opening a pane that then fails to stream. Everything that
    /// resolves goes through <see cref="OpenLogsForAsync"/>, so the pane chosen, the
    /// inspector tab reused and the maximized preference are the list's own.
    /// </summary>
    /// <returns>Null when logs opened; otherwise the sentence to show.</returns>
    internal Task<string?> OpenNamedLogsAsync(
        OwnerRef target, string? namespaceHint, string clusterName, ClusterClient? client, bool? maximized)
    {
        var source = client is not null ? NamedObjectSource.For(client)
            : IsDemo ? NamedObjectSource.Demo
            : null;
        return source is null
            ? Task.FromResult<string?>("Not connected to this cluster.")
            : OpenNamedLogsAsync(target, namespaceHint, clusterName, client, source, maximized);
    }

    /// <summary>
    /// The same, reading through <paramref name="source"/> — which is the whole of what
    /// differs between a real cluster, the demo dataset and a test's stand-in (the
    /// <see cref="LogTargetSource"/> precedent), so "gone", "refused" and "names no pods"
    /// are pinned against exactly the code a real cluster runs.
    /// </summary>
    internal async Task<string?> OpenNamedLogsAsync(
        OwnerRef target, string? namespaceHint, string clusterName, ClusterClient? client,
        NamedObjectSource source, bool? maximized)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        ResourceDescriptor? descriptor;
        DynamicResource? resolved;
        try
        {
            var catalog = await source.Catalog(CancellationToken.None);
            descriptor = catalog.FirstOrDefault(d => d.ApiVersion == target.ApiVersion && d.Kind == target.Kind);
            if (descriptor is null)
            {
                return source.IsDemo
                    ? $"{target.Kind}/{target.Name} isn't part of the demo dataset, so there are no logs to show."
                    : $"This cluster does not serve {target.Kind} ({target.ApiVersion}).";
            }

            resolved = await source.Read(
                descriptor, descriptor.Namespaced ? namespaceHint : null, target.Name, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // A 403 is the common one, and the server's sentence names the verb and the
            // subject — more useful than anything worded here.
            return ex.Message;
        }

        if (resolved is null)
        {
            if (source.IsDemo)
            {
                return $"{target.Kind}/{target.Name} isn't part of the demo dataset, so there are no logs to show.";
            }

            var qualified = namespaceHint is { Length: > 0 } && descriptor.Namespaced
                ? $"{namespaceHint}/{target.Name}"
                : target.Name;
            return $"{target.Kind} {qualified} no longer exists — it was deleted or replaced since this was listed.";
        }

        if (!LogTarget.HasOwnLogs(resolved))
        {
            return $"{target.Kind}/{target.Name} names no pods, so there are no logs to open.";
        }

        await OpenLogsForAsync(new LogTarget(resolved, descriptor, clusterName, client), previous: false, maximized);
        return null;
    }

    /// <summary>
    /// Opens the logs of a pod or of a workload's pods. The one entry point: the list's L,
    /// Shift+L and P keys, the row's logs icon, its context menu and the palette's rows all
    /// come here, so each makes the same choice of pane and reuses the same inspector tab. A
    /// pod opens its detail pane on the Logs tab (<paramref name="previous"/> shows the
    /// crashed instance); anything that names its pods opens the one-stream pane over them.
    /// Neither ever replaces an open editor tab (UI rule 5) — both open permanent tabs, not
    /// previews.
    ///
    /// <para>
    /// <paramref name="maximized"/>: true maximizes the inspector over the list (Shift+L, a
    /// Shift+click on the logs icon); null leaves it to the "Open logs maximized" preference,
    /// read here at the moment of opening so a change applies to the next open. Neither ever
    /// <em>un</em>-maximizes: logs opened while the inspector already fills the area stay
    /// full-size, and the way back is Esc or the dock's restore icon.
    /// </para>
    /// </summary>
    public async Task OpenLogsForAsync(LogTarget target, bool previous = false, bool? maximized = null)
    {
        if (await OpenLogsPaneAsync(target, previous) && (maximized ?? App.LoadSettings().OpenLogsMaximized))
        {
            IsInspectorMaximized = true;
        }
    }

    /// <summary>Opens (or re-selects) the pane; false when there was nothing to open.</summary>
    private async Task<bool> OpenLogsPaneAsync(LogTarget target, bool previous)
    {
        // The live row when the list holds this object, so the pane follows its watch;
        // otherwise a row over the object as it was listed, which is what owner navigation
        // already does for an object the list is not showing.
        var key = ResourceRowViewModel.KeyFor(target.ClusterName, target.Resource.Key);
        var row = _rowsByKey.TryGetValue(key, out var live)
                  && string.Equals(live.Resource.Kind, target.Resource.Kind, StringComparison.Ordinal)
            ? live
            : new ResourceRowViewModel(target.Resource, target.ClusterName);

        if (target.IsPod)
        {
            await OpenRowAsync(row, preview: false, target.Descriptor, target.Client);
            if (SelectedInspectorTab is not PodDetailTabViewModel detail)
            {
                return false;
            }

            detail.SelectedDetailTabIndex = 0;
            detail.IsShowingPreviousLogs = previous;
            return true;
        }

        if (LabelSelector.ForPodsOf(row.Resource) is not { } selector)
        {
            return false;
        }

        // Null in demo mode, where the pane still works: its pods come out of the shipped
        // dataset through the same LabelSelector.Matches the live path renders into a
        // query — see InspectorTabViewModelBase.IsDemo.
        var client = target.Client;
        if (client is null && !IsDemo)
        {
            return false;
        }

        var tabKey = WorkloadLogsTabViewModel.KeyFor(row.ClusterName, target.Descriptor, row.Namespace, row.Name);
        if (InspectorTabs.FirstOrDefault(t => t.Key == tabKey) is { } existing)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return true;
        }

        AddInspectorTab(new WorkloadLogsTabViewModel(client, target.Descriptor, row.Resource, selector, row.ClusterName));
        return true;
    }
}
