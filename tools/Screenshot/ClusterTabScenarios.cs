using KubeNimbus.Core.Networking;
using System.Text.Json;
using Avalonia.Threading;
using KubeNimbus.App;
using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.Screenshot;

/// <summary>
/// Builds fully-populated <see cref="ClusterTabViewModel"/> instances for
/// screenshot scenarios, bypassing ConnectAsync (which needs a real cluster)
/// by setting the same public properties it would have set, from fixture data.
/// </summary>
internal static class ClusterTabScenarios
{
    /// <summary>
    /// Carries the <c>scale</c> subresource because a real server's discovery does, and
    /// that — not the kind's name — is what makes the Scale action appear (see
    /// <see cref="WorkloadActions.SupportsScale"/>).
    /// </summary>
    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", true, [], [])
        {
            Subresources = ["scale", "status"],
        };

    private static readonly ResourceDescriptor SecretDescriptor =
        new("", "v1", "Secret", "secrets", "secret", true, [], []);

    /// <summary>Fixed "now" so every screenshot's time axis (and its captions) is diffable.
    /// The running demo cluster passes the real clock instead — see <see cref="DemoUsage"/>.</summary>
    private static readonly DateTimeOffset FixtureNow = new(2026, 7, 30, 8, 45, 0, TimeSpan.Zero);

    private static ClusterTabViewModel BaseTab(bool populateRows = true, bool seedUsage = true)
    {
        var context = new ClusterContext("prod-payments", "prod-payments-cluster", "payments", "fake-user", "fixture");
        var tab = new ClusterTabViewModel(context) { IsConnected = true };
        tab.SetConnectedStatus("v1.31.2");

        var catalog = FixtureData.BuildCatalog();
        foreach (var section in FixtureData.BuildSidebarSections(catalog))
        {
            tab.SidebarSections.Add(section);
        }

        // The pass production runs at the end of every sidebar rebuild: it is what puts
        // the kind-count badges on and applies the advanced view's section gate. The
        // fixture adds its sections by hand rather than through discovery, so without
        // this the sidebar renders as though the switch were off no matter what it says.
        tab.ApplySidebarChrome();

        tab.NamespaceOptions.Add(ClusterTabViewModel.AllNamespaces);
        foreach (var ns in FixtureData.Namespaces)
        {
            tab.NamespaceOptions.Add(ns);
        }

        tab.SelectedNamespace = "payments";

        var podKind = tab.SidebarSections
            .First(s => s.Title == "Workloads").Kinds
            .First(k => k.Descriptor.Kind == "Pod");

        // No IsSelected here or anywhere below: the highlight follows SelectedKind
        // (ClusterTabViewModel.MarkSelectedKind). Setting it by hand is how
        // cluster-tab-row-action-scale came to draw Pods and Deployments both selected.
        tab.SelectedKind = podKind;

        if (populateRows)
        {
            // The tab says it is showing the `payments` namespace, so it shows that
            // namespace. The shared dataset also carries the kube-system pods the node
            // surface needs (a DaemonSet pod, a static pod's mirror, an unmanaged pod
            // and one with an emptyDir), and letting those into a namespace-scoped
            // fixture would make every list scenario disagree with its own namespace
            // picker — and would have silently rewritten a dozen committed screenshots.
            foreach (var pod in FixtureData.Pods.Where(p => p.Namespace == "payments"))
            {
                tab.Rows.Add(new ResourceRowViewModel(pod));
            }

            tab.SelectedRow = tab.Rows.FirstOrDefault();

            // metrics-server can't be reached from an offline fixture client, so
            // stand in for a session's worth of polls — otherwise the CPU/Memory
            // columns never appear in a screenshot and their sparklines have
            // nothing to draw. Deterministic per row, not random, so screenshots
            // stay diffable.
            if (seedUsage)
            {
                tab.AreMetricsVisible = true;
                for (var i = 0; i < tab.Rows.Count; i++)
                {
                    SeedUsage(tab.Rows[i], i, (3 + i * 17) * 1_000_000L, (48 + i * 37) * 1024L * 1024L);
                }
            }
        }

        // Setting SelectedNamespace above triggers the real RestartWatch(), which
        // (with no Client wired up in this fixture) latches IsListEmpty before the
        // rows above are added. Recompute now that the fixture's own row population
        // is done — production code never hits this ordering since RestartWatch's
        // background pump is what populates Rows there, not a direct caller.
        tab.IsListEmpty = tab.Rows.Count == 0;
        tab.IsListLoading = false;

        return tab;
    }

    /// <summary>
    /// The default layout: advanced view on, which is what a fresh install opens on —
    /// the whole sidebar catalog, and every content-area column the cluster can fill.
    /// </summary>
    public static ClusterTabViewModel WorkloadsList() => BaseTab();

    /// <summary>
    /// The same tab with the advanced view off: the Cluster and CRDs sections are gone
    /// from the sidebar and everything else — the list, its usage columns, the whole
    /// content area — is untouched. That second half is the claim worth rendering: the
    /// switch used to take the usage columns, the fleet toggle and the log tools with
    /// it, which is precisely what it no longer does.
    /// </summary>
    public static ClusterTabViewModel BasicSidebar() => Basic(BaseTab());

    /// <summary>
    /// Turns the advanced view off on a fixture tab. Set *after* the sections are in
    /// place: <c>OnIsAdvancedViewChanged</c> is what pushes the gate onto them, so a
    /// tab flipped before it had any sections would render with all of them.
    /// </summary>
    private static ClusterTabViewModel Basic(ClusterTabViewModel tab)
    {
        tab.IsAdvancedView = false;
        return tab;
    }

    /// <summary>Same pod list, with the CPU/Mem column populated — demonstrates the
    /// metrics.k8s.io-present path.</summary>
    public static ClusterTabViewModel WorkloadsListWithMetrics()
    {
        var tab = BaseTab(seedUsage: false);
        ApplyMetrics(tab);
        return tab;
    }

    private static void ApplyMetrics(ClusterTabViewModel tab)
    {
        tab.AreMetricsVisible = true;

        // The dataset's own PodMetrics, replayed through the real ApplyUsage. A pod with
        // no entry records a window of gaps, which is the "—" column state and an
        // empty sparkline rather than a flat line at zero.
        DemoUsage.SeedRows(tab.Rows, FixtureNow);
    }

    /// <summary>
    /// Replays a session's worth of polls into a row through the app's own
    /// <see cref="DemoUsage"/>, at the fixture clock so the images stay diffable.
    /// The seeding itself lives with the dataset, in the app: what a screenshot shows
    /// and what the shipping demo cluster shows have to come out of one code path.
    /// </summary>
    private static void SeedUsage(ResourceRowViewModel row, int seed, long? cpu, long? memory) =>
        DemoUsage.Seed(row, seed, cpu, memory, FixtureNow);

    // --------------------------------------------------------- demo cluster
    //
    // Unlike every other scenario here, these do NOT hand-build a tab: they run the
    // real ConnectCommand on a real ClusterContext.Demo. That path needs no cluster —
    // that is the whole point of it — so the harness can exercise production code end
    // to end, which also makes these the only scenarios that would catch the demo
    // cluster's connect breaking.

    private static ClusterTabViewModel DemoTab()
    {
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        return tab;
    }

    /// <summary>
    /// What "Explore demo cluster" opens on: a populated pod list with the persistent
    /// sample-data banner above it. The banner is the thing to look at — nobody may
    /// mistake this screen for their own cluster.
    /// </summary>
    public static ClusterTabViewModel WorkloadDetail(int selectedTab = 0)
    {
        var tab = DemoTab();
        var kind = tab.SidebarSections.SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "apps", Kind: "Deployment" });
        tab.SelectKindCommand.Execute(kind);
        tab.SelectedRow = tab.Rows.First();
        tab.OpenSelectedCommand.Execute(null);
        if (tab.SelectedInspectorTab is WorkloadDetailTabViewModel detail)
        {
            detail.SelectedTabIndex = selectedTab;

            // A selected pod, so the Pods shot shows the row's logs icon (L3) — drawn on
            // the selected and the hovered row only, like the resource list's own.
            detail.SelectedPod = detail.Pods.FirstOrDefault();
            detail.Conditions.Add(new("Available", "True", "MinimumReplicasAvailable", "Deployment has minimum availability."));
            detail.Conditions.Add(new("Progressing", "True", "NewReplicaSetAvailable", "ReplicaSet has successfully progressed."));
            using var document = JsonDocument.Parse("""
                {"apiVersion":"v1","kind":"Event","metadata":{"name":"workload-event","namespace":"payments"},
                "type":"Normal","reason":"ScalingReplicaSet","message":"Scaled up replica set to 3","count":1,
                "involvedObject":{"kind":"Deployment","name":"payment-service-report-generator","namespace":"payments"}}
                """);
            detail.Events.Add(new EventRowViewModel(new DynamicResource(document.RootElement.Clone())));
            detail.EventsStatus = "1 event";
        }
        return tab;
    }

    public static ClusterTabViewModel DemoList() => DemoTab();

    /// <summary>Demo pod detail — logs, containers and events, all from the shipped dataset.</summary>
    public static ClusterTabViewModel DemoPodDetail()
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows.FirstOrDefault(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal))
            ?? tab.Rows.FirstOrDefault();
        tab.OpenSelectedCommand.Execute(null);

        // Logs arrive on a timer (DemoLogs.Interval), which the headless harness does
        // not run in real time — so drain the dispatcher a few times to let the first
        // lines land rather than capturing the pane's "waiting for output" state.
        DrainDemoLogs(tab);
        return tab;
    }

    /// <summary>
    /// The inspector state a demo cluster genuinely cannot serve. Exec is the example;
    /// port-forward and the YAML editor's write half render the same way. This is the
    /// scenario that pins UI rule 9 for demo mode — "not available" must be a stated,
    /// styled state, never a spinner or a blank pane.
    /// </summary>
    public static ClusterTabViewModel DemoExecUnavailable()
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows.FirstOrDefault();
        tab.ExecIntoSelectedCommand.Execute(null);
        return tab;
    }

    /// <summary>
    /// A CRD list wearing the columns the CRD itself declares — the whole of FEAT-2 in
    /// one image. Selecting cert-manager's Certificate kind goes through the real
    /// <c>SelectKindCommand</c>, so the columns are read from the dataset's own
    /// CustomResourceDefinition by the same <c>PrinterColumns.Parse</c> a live cluster's
    /// GET goes through, and the cells by the same evaluator. What to look at: READY and
    /// SECRET where the generic Status pill used to be, the READY cell coming from a
    /// condition filter (<c>.status.conditions[?(@.type=="Ready")].status</c>), the
    /// object with no status at all rendering as an empty cell rather than an error, and
    /// AGE still being the list's own live column rather than the CRD's declared one.
    ///
    /// <para>
    /// The CRD's two <c>priority: 1</c> columns (ISSUER and STATUS) are here too. They
    /// used to need the advanced view — this app's <c>-o wide</c> — and had a scenario
    /// of their own to render that; the switch governs the sidebar and nothing else now,
    /// so every declared column is simply drawn and the pair collapses into this one.
    /// </para>
    /// </summary>
    /// <summary>
    /// L2 — the row's logs icon. A demo pod list with one row selected; the scenario's
    /// interaction step (<c>UxInteractionChecks.HoverRow</c>) then puts the pointer over a
    /// second row, so the shot shows the icon on both states it appears in and on none of
    /// the other rows.
    /// </summary>
    public static ClusterTabViewModel DemoRowLogs()
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows.Skip(1).FirstOrDefault() ?? tab.Rows.FirstOrDefault();
        return tab;
    }

    /// <summary>The same icon on a workload list — L opens the one-stream pane there.</summary>
    public static ClusterTabViewModel DemoRowLogsDeployments()
    {
        var tab = DemoTab();
        var kind = tab.SidebarSections.SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "apps", Kind: "Deployment" });
        tab.SelectKindCommand.Execute(kind);
        tab.SelectedRow = tab.Rows.FirstOrDefault();
        return tab;
    }

    /// <summary>A kind with no logs: no icon on the selected row, and no slot for one.</summary>
    public static ClusterTabViewModel DemoRowLogsNone()
    {
        var tab = DemoTab();
        var kind = tab.SidebarSections.SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "ConfigMap" });
        tab.SelectKindCommand.Execute(kind);
        tab.SelectedRow = tab.Rows.FirstOrDefault();
        return tab;
    }

    /// <summary>
    /// Shift+L: the selected pod's logs opened with the inspector already maximized over
    /// the list — through the real command, so the dock state is the one the key produces.
    /// </summary>
    public static ClusterTabViewModel DemoLogsMaximized()
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows.FirstOrDefault(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal))
            ?? tab.Rows.FirstOrDefault();
        tab.OpenLogsMaximizedCommand.Execute(null);
        DrainDemoLogs(tab);
        return tab;
    }

    public static ClusterTabViewModel DemoCrdPrinterColumns() => SelectDemoCertificates(DemoTab());

    private static ClusterTabViewModel SelectDemoCertificates(ClusterTabViewModel tab)
    {
        tab.SelectedNamespace = ClusterTabViewModel.AllNamespaces;
        var kind = tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "cert-manager.io", Kind: "Certificate" });
        tab.SelectKindCommand.Execute(kind);
        return tab;
    }

    /// <summary>
    /// FEAT-3's whole claim in one image: every pod of a Deployment tailed in one pane,
    /// each line keyed to its pod by colour and by a printed name. The demo dataset holds
    /// three replicas of <c>payment-service-report-generator</c> and two of them belong to
    /// the old ReplicaSet while the third belongs to the new one, so what this renders is
    /// a rolling deployment read as a single stream — which is the item's acceptance
    /// criterion, not a decorative choice of fixture.
    /// </summary>
    /// <remarks>
    /// Built by running the real command on a real demo tab, so the selector is parsed
    /// from the Deployment object, the pods are found through
    /// <see cref="LabelSelector.Matches"/>, and the lines go through the same merge,
    /// buffer and filter a live cluster's would.
    /// </remarks>
    public static ClusterTabViewModel DemoWorkloadLogs(
        string filter = "", bool filterMode = true, string workload = "payment-service-report-generator")
    {
        var tab = DemoTab();
        var kind = tab.SidebarSections
            .First(s => s.Title == "Workloads").Kinds
            .First(k => k.Descriptor is { Group: "apps", Kind: "Deployment" });
        tab.SelectKindCommand.Execute(kind);

        tab.SelectedRow = tab.Rows.FirstOrDefault(r => r.Name == workload)
            ?? tab.Rows.FirstOrDefault();
        tab.OpenWorkloadLogsCommand.Execute(null);

        DrainWorkloadLogs(tab);
        if (tab.SelectedInspectorTab is WorkloadLogsTabViewModel logs)
        {
            // Filtering is the search's second mode since FEAT-33; the filtered-empty shot
            // is about that mode's own empty state, so it asks for it.
            logs.IsLogFilterMode = filterMode && filter.Length > 0;
            logs.LogSearchText = filter;
        }

        return tab;
    }

    /// <summary>
    /// ENG-45: the demo's fraud detector, whose two pods are unschedulable. The chips read
    /// "not started" and the body says why, in the sentence a live cluster's pane reads
    /// from the pod (LogStreamEnd) — they used to read "ended" over "waiting for output".
    /// </summary>
    public static ClusterTabViewModel DemoWorkloadLogsNotStarted()
    {
        var tab = DemoWorkloadLogs(workload: "fraud-detector");
        if (tab.SelectedInspectorTab is WorkloadLogsTabViewModel logs)
        {
            for (var i = 0; i < 300 && logs.Sources.Any(s => s.State is LogSourceState.Starting or LogSourceState.Streaming); i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Dispatcher.UIThread.RunJobs();
        }

        return tab;
    }

    /// <summary>
    /// FEAT-33 in the multi-pod pane: finding keeps every pod's lines on screen with the
    /// matches highlighted and the current one marked, the counter in the box.
    /// </summary>
    public static ClusterTabViewModel DemoWorkloadLogsFind() => DemoWorkloadLogs("report", filterMode: false);

    /// <summary>
    /// FEAT-33, FEAT-36 and FEAT-39 in pod detail's pane, on the demo pod's whole canned
    /// stream: a search in find mode (every line kept, matches highlighted, "n of m" and
    /// the arrows in the box), timestamps shown in local time with the UTC chip beside the
    /// clock, and optionally the Info level hidden — the Levels button then names what is
    /// left and turns accent.
    /// </summary>
    public static ClusterTabViewModel DemoPodDetailSearch(string query = "report", bool hideInfo = false)
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows.FirstOrDefault(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal))
            ?? tab.Rows.FirstOrDefault();
        tab.OpenSelectedCommand.Execute(null);

        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            for (var i = 0; i < 600 && detail.LogStatus is null; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            detail.ShowLogTimestamps = true;
            detail.Levels.ShowInfo = !hideInfo;
            detail.LogSearchText = query;
            detail.FindPreviousLogMatchCommand.Execute(null);

            // The toggle is a preference and writes settings.json (FEAT-37) — redirected
            // here, but shared by every scenario after this one, which would all open with
            // timestamps on. Put the file back; this pane keeps its own state.
            var store = new AppSettingsStore();
            store.Save(store.Load() with { LogShowTimestamps = false });
        }

        return tab;
    }

    /// <summary>
    /// The log viewer's grep: a regular expression filtering with two lines of context
    /// around each match, a pinned highlight in its own colour, and the error jump on the
    /// error line. Everything the pane does that used to send a log to an editor, on one
    /// screen.
    /// </summary>
    public static ClusterTabViewModel DemoPodDetailGrep()
    {
        var tab = DemoPodDetailSearch(query: "");
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            detail.Pins.Add("merchant", regex: false, matchCase: false);
            detail.IsLogRegex = true;
            detail.LogContextLines = 2;
            detail.IsLogFilterMode = true;
            detail.LogSearchText = "slow|upload";
            detail.Problems.PreviousErrorCommand.Execute(null);
        }

        return tab;
    }

    /// <summary>A JSON line opened under itself, and the error jump's cursor on the error line.</summary>
    public static ClusterTabViewModel DemoPodDetailJson()
    {
        var tab = DemoPodDetailSearch(query: "");
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            if (detail.LogLines.FirstOrDefault(l => l.IsJson) is { } json)
            {
                json.IsExpanded = true;
            }

            detail.Problems.PreviousErrorCommand.Execute(null);
        }

        return tab;
    }

    /// <summary>
    /// Pumps the dispatcher until enough of the three replayed streams has landed to show
    /// the merge doing its job. Deeper than <see cref="DrainDemoLogs"/>'s six lines on
    /// purpose: the first lines of each pod are its own startup, and the interleaving only
    /// becomes visible further in, which is exactly the part worth screenshotting.
    /// </summary>
    private static void DrainWorkloadLogs(ClusterTabViewModel tab)
    {
        if (tab.SelectedInspectorTab is not WorkloadLogsTabViewModel logs)
        {
            return;
        }

        // Until every replayed stream has ended (ENG-53), not until some number of lines has
        // arrived: a search set after the drain lands on the newest match there is, so a drain
        // that stopped part-way through the replay put "14 of 17" in one run and "15 of 17"
        // in the next.
        for (var i = 0; i < 800 && logs.Sources.Any(s => s.State is LogSourceState.Starting or LogSourceState.Streaming); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps the dispatcher until the demo log replay has produced something to render.</summary>
    private static void DrainDemoLogs(ClusterTabViewModel tab)
    {
        if (tab.SelectedInspectorTab is not PodDetailTabViewModel detail)
        {
            return;
        }

        for (var i = 0; i < 200 && detail.LogLines.Count < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// The Events list, read the way <c>kubectl get events</c> prints it: Last seen, Type,
    /// Reason, Object, Count, Message — newest first by default. All namespaces, so the
    /// Namespace column carries both of the dataset's namespaces. The rows are exactly
    /// the demo dataset's events (demo rule 3: one dataset).
    /// </summary>
    public static ClusterTabViewModel EventsList(string? filter = null)
    {
        var tab = EventsTab(ClusterTabViewModel.AllNamespaces);

        foreach (var e in FixtureData.Events)
        {
            tab.Rows.Add(new ResourceRowViewModel(e));
        }

        tab.IsListEmpty = false;
        tab.IsListLoading = false;
        if (filter is not null)
        {
            tab.RowFilter = filter;
        }

        return tab;
    }

    /// <summary>
    /// An Events tab with nothing in it: the empty state, which for this kind adds that
    /// events expire — an hour after they last happen by default — so an empty list reads
    /// as "nothing recent" rather than as a broken watch.
    /// </summary>
    public static ClusterTabViewModel EventsListEmpty()
    {
        var tab = EventsTab("payments");
        tab.IsListLoading = false;
        tab.IsListEmpty = true;
        return tab;
    }

    /// <summary>
    /// The states the demo dataset cannot hold, across a fleet: an events.k8s.io-shaped
    /// series (<c>regarding</c>/<c>note</c>, a series count and <c>lastObservedTime</c>
    /// newer than its <c>eventTime</c>), an event naming no object, one with no timestamp
    /// at all, and a multi-line message. Synthetic on purpose — none of these belongs in a
    /// dataset a Store reviewer browses, and a real API server always stamps
    /// <c>creationTimestamp</c>, so "no timestamp" only exists here.
    /// </summary>
    public static ClusterTabViewModel EventsListEdgeCases()
    {
        var tab = EventsTab(ClusterTabViewModel.AllNamespaces);
        tab.IsFleetViewAvailable = true;
        tab.IsFleetView = true;
        tab.FleetSummary = "2 of 2 clusters serve Event";

        var now = DateTimeOffset.UtcNow;
        string At(TimeSpan ago) => (now - ago).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        foreach (var e in FixtureData.Events.Where(e => e.Namespace == "payments").Take(3))
        {
            tab.Rows.Add(new ResourceRowViewModel(e, "prod-payments"));
        }

        tab.Rows.Add(new ResourceRowViewModel(Parse($$"""
            {
              "apiVersion": "v1", "kind": "Event",
              "metadata": { "name": "ledger-db-0.17f9c2", "namespace": "ledger" },
              "regarding": { "apiVersion": "v1", "kind": "Pod", "name": "ledger-db-0", "namespace": "ledger" },
              "type": "Warning", "reason": "FailedScheduling",
              "note": "0/3 nodes are available: 1 node(s) had untolerated taint {node-role.kubernetes.io/control-plane: },\n2 Insufficient memory. preemption: 0/3 nodes are available: 3 No preemption victims found for incoming pod.",
              "eventTime": "{{At(TimeSpan.FromMinutes(47))}}",
              "series": { "count": 14, "lastObservedTime": "{{At(TimeSpan.FromMinutes(2))}}" }
            }
            """), "prod-ledger"));

        tab.Rows.Add(new ResourceRowViewModel(Parse($$"""
            {
              "apiVersion": "v1", "kind": "Event",
              "metadata": { "name": "cluster-autoscaler-status.17f9d0", "namespace": "kube-system" },
              "type": "Normal", "reason": "ScaleDown",
              "message": "Scale-down: removing empty node ip-10-0-3-17.eu-west-1.compute.internal",
              "count": 1, "lastTimestamp": "{{At(TimeSpan.FromMinutes(9))}}"
            }
            """), "prod-ledger"));

        tab.Rows.Add(new ResourceRowViewModel(Parse("""
            {
              "apiVersion": "v1", "kind": "Event",
              "metadata": { "name": "ledger-api.17f9e4", "namespace": "ledger" },
              "involvedObject": { "apiVersion": "apps/v1", "kind": "Deployment", "name": "ledger-api", "namespace": "ledger" },
              "type": "Normal", "reason": "ScalingReplicaSet",
              "message": "Scaled up replica set ledger-api-7c9d5f6b8 to 3"
            }
            """), "prod-ledger"));

        tab.IsListEmpty = false;
        tab.IsListLoading = false;
        return tab;
    }

    /// <summary>
    /// A tab showing the Events kind in <paramref name="namespace"/>, with no rows yet.
    /// The namespace and kind are set before any row is added, because both run the real
    /// <c>RestartWatch()</c>, which clears <c>Rows</c> (see <see cref="BaseTab"/>).
    /// </summary>
    private static ClusterTabViewModel EventsTab(string @namespace)
    {
        var tab = BaseTab(populateRows: false);
        var config = tab.SidebarSections.First(s => s.Title == "Config");

        // Config starts collapsed (it is no longer the catalog's junk drawer, but it is
        // still not what a session opens on), and these shots are about the row that is
        // selected in it.
        config.IsExpanded = true;

        tab.SelectedNamespace = @namespace;
        var eventsKind = config.Kinds.First(k => k.Descriptor.Kind == "Event");
        tab.SelectedKind = eventsKind;
        return tab;
    }

    private static DynamicResource Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    /// <summary>
    /// The aggregated fleet list: one kind across three connected clusters, with the
    /// Cluster column shown and the "n of m clusters" summary in the header.
    /// </summary>
    /// <remarks>
    /// Rows are added directly rather than through <c>ClusterFleet.WatchAsync</c> —
    /// that needs several live clusters, which no offline fixture can provide. Note the
    /// ordering: <c>IsFleetView</c> is set first because it triggers the real
    /// <c>RestartWatch()</c>, which clears <c>Rows</c>; populating before it would
    /// leave an empty list (same class of gotcha as <c>SelectedNamespace</c>, see
    /// <see cref="BaseTab"/>).
    /// </remarks>
    public static ClusterTabViewModel FleetList()
    {
        var tab = BaseTab(populateRows: false);
        tab.IsFleetViewAvailable = true;
        tab.IsFleetView = true;
        tab.FleetSummary = "3 of 3 clusters serve Pod";

        var seed = 0;
        foreach (var cluster in new[] { "prod-payments", "prod-ledger", "staging-eu" })
        {
            foreach (var pod in FixtureData.Pods.Where(p => p.Namespace == "payments"))
            {
                var row = new ResourceRowViewModel(pod, cluster);
                tab.Rows.Add(row);
                SeedUsage(row, seed, (4 + seed * 13) * 1_000_000L, (52 + seed * 29) * 1024L * 1024L);
                seed++;
            }
        }

        tab.AreMetricsVisible = true;
        tab.SelectedRow = tab.Rows.FirstOrDefault();
        tab.IsListLoading = false;
        tab.IsListEmpty = tab.Rows.Count == 0;

        // Aggregation is only reachable from the advanced view, so that is the state
        // this shot has to be in for the toggle and the Cluster column to read right.
        return tab;
    }

    /// <summary>
    /// A partial fleet — the honest common case: the kind isn't served everywhere (or a
    /// cluster is unreachable), which the header states rather than leaving the user to
    /// infer from the rows.
    /// </summary>
    public static ClusterTabViewModel FleetListPartial()
    {
        var tab = FleetList();
        tab.FleetSummary = "2 of 3 clusters serve Pod";
        tab.ConnectionWarning = "staging-eu: connection refused (127.0.0.1:6550)";
        foreach (var row in tab.Rows.Where(r => r.ClusterName == "staging-eu").ToArray())
        {
            tab.Rows.Remove(row);
        }

        return tab;
    }

    public static ClusterTabViewModel SidebarFiltered()
    {
        var tab = BaseTab();
        tab.SidebarFilter = "route";
        return tab;
    }

    /// <summary>
    /// Filtering by API group rather than by kind name — the case that makes the CRDs
    /// section navigable when several groups ship a same-named kind.
    /// </summary>
    public static ClusterTabViewModel SidebarFilteredByGroup()
    {
        var tab = BaseTab();
        tab.SidebarFilter = "cert-manager";
        return tab;
    }

    /// <summary>
    /// The pinned Recent section, built by selecting a few kinds through the real
    /// <c>SelectKindCommand</c> — the same path a click takes.
    /// </summary>
    public static ClusterTabViewModel SidebarRecentKinds()
    {
        var tab = BaseTab();

        // Whatever the fixture catalog actually holds in these sections, rather than
        // named kinds — a scenario shouldn't break because a fixture changed.
        foreach (var title in new[] { "Config", "Network", "Storage" })
        {
            if (tab.SidebarSections.FirstOrDefault(s => s.Title == title)?.Kinds.FirstOrDefault() is { } entry)
            {
                tab.SelectKindCommand.Execute(entry);
            }
        }

        // Back to Pods, and re-populate: each SelectKind ran the real RestartWatch,
        // which clears Rows and (with no live client behind the fixture) can't refill them.
        var pods = tab.SidebarSections.First(s => s.Title == "Workloads").Kinds.First(k => k.Descriptor.Kind == "Pod");
        tab.SelectKindCommand.Execute(pods);
        foreach (var pod in FixtureData.Pods.Where(p => p.Namespace == "payments"))
        {
            tab.Rows.Add(new ResourceRowViewModel(pod));
        }

        tab.SelectedRow = tab.Rows.FirstOrDefault();
        tab.IsListLoading = false;
        tab.IsListEmpty = tab.Rows.Count == 0;
        return tab;
    }

    public static ClusterTabViewModel SidebarCrdsExpanded()
    {
        var tab = BaseTab();
        var crds = tab.SidebarSections.First(s => s.Title == "CRDs");
        crds.IsExpanded = true;
        return tab;
    }

    // ------------------------------------------------ mutating workload actions
    //
    // The armed confirm strip for scale / rollout restart / delete. Every one of these
    // goes through the tab's real commands, so what renders is what the context menu
    // and the palette produce — including the capability gate: the Scale action only
    // appears because the fixture descriptor carries the `scale` subresource discovery
    // would have reported.

    /// <summary>A Deployment list with a row selected — the starting point for the workload actions.</summary>
    private static ClusterTabViewModel DeploymentsTab()
    {
        var tab = BaseTab(populateRows: false);
        var kind = tab.SidebarSections
            .First(s => s.Title == "Workloads").Kinds
            .First(k => k.Descriptor.Kind == "Deployment");
        tab.SelectedKind = kind;

        foreach (var deployment in FixtureData.Deployments)
        {
            tab.Rows.Add(new ResourceRowViewModel(deployment));
        }

        tab.SelectedRow = tab.Rows.FirstOrDefault(r => r.Name == "checkout-worker") ?? tab.Rows.FirstOrDefault();

        // Same ordering fix as BaseTab: assigning SelectedKind ran the real watch path,
        // which latched the empty state before these rows existed.
        tab.IsListLoading = false;
        tab.IsListEmpty = tab.Rows.Count == 0;
        return tab;
    }

    /// <summary>
    /// Arms an action on the selected Deployment row. The tab's own
    /// <c>ScaleSelectedCommand</c>/<c>RestartSelectedCommand</c> refuse here and are
    /// right to: a fixture tab has no <c>Client</c> and is not the demo cluster, which
    /// is precisely the "disconnected" case they must not act in. So the strip is built
    /// against the offline fixture client instead — the same thing the exec, YAML and
    /// Helm scenarios do with their inspector tabs, and for the same reason.
    /// </summary>
    /// <remarks>
    /// The strip names the tab's context and takes its environment, as the tab's own
    /// <c>ArmRowAction</c> does. The environment is the name guess rather than the tab's
    /// <c>Environment</c>, which the shell stamps only once the tab is hosted — after this
    /// runs. The fixture context is <c>prod-payments</c>, so these strips are production ones.
    /// </remarks>
    private static RowActionViewModel ArmRowAction(ClusterTabViewModel tab, RowActionKind kind)
    {
        var row = tab.SelectedRow!;
        var action = new RowActionViewModel(
            kind, FixtureData.CreateOfflineClient(), tab.SelectedKind!.Descriptor, row.Namespace, row.Name,
            tab.Context.Name,
            replicas: WorkloadActions.DeclaredReplicas(row.Resource),
            environment: ClusterEnvironments.Classify(tab.Context.Name, tab.Context.ClusterName));

        tab.PendingRowAction = action;
        return action;
    }

    /// <summary>
    /// Scale, armed: the replica box, the running count beside it, and the confirm, whose
    /// question reads "from 4 to 6" (checkout-worker declares 4). The scale subresource
    /// cannot be read from an offline client, so the reading it would have produced is
    /// written in — obviously-fake numbers, as with every other fixture.
    /// </summary>
    public static ClusterTabViewModel RowActionScale()
    {
        var tab = DeploymentsTab();
        var action = ArmRowAction(tab, RowActionKind.Scale);
        action.SetCurrentScale(4, 1);
        action.Replicas = 6;
        return tab;
    }

    /// <summary>
    /// A slipped digit: 40 typed for a Deployment at 4. The question says "from 4 replicas
    /// (1 running) to [40]" and the strip says it is ten times the current count (B3-4), in
    /// the warn line under the sentence.
    /// </summary>
    public static ClusterTabViewModel RowActionScaleJump()
    {
        var tab = DeploymentsTab();
        var action = ArmRowAction(tab, RowActionKind.Scale);
        action.SetCurrentScale(4, 1);
        action.Replicas = 40;
        return tab;
    }

    /// <summary>
    /// The scale strip once the API server has answered: the sentence stays, the box is
    /// read-only, and the result line leads with its check and carries Close (FEAT-77).
    /// </summary>
    public static ClusterTabViewModel RowActionScaled()
    {
        var tab = DeploymentsTab();
        var action = ArmRowAction(tab, RowActionKind.Scale);
        action.SetCurrentScale(4, 1);
        action.Replicas = 6;
        action.IsSuccess = true;
        action.IsDone = true;
        action.Message = "Scaled to 6. The list follows the rollout as the watch reports it.";
        return tab;
    }

    /// <summary>
    /// A request in flight: the moving bar leads the result line, the confirm and Cancel go
    /// dead (the patch is already with the API server), and nothing claims a verdict yet.
    /// </summary>
    public static ClusterTabViewModel RowActionBusy()
    {
        var tab = DeploymentsTab();
        var action = ArmRowAction(tab, RowActionKind.Restart);
        action.IsBusy = true;
        action.Message = "Restarting…";
        return tab;
    }

    /// <summary>
    /// Scaling a production workload to zero: the one scale warning drawn as an infoBar,
    /// because on production it is an outage (B3-4).
    /// </summary>
    public static ClusterTabViewModel RowActionScaleZeroProduction()
    {
        var tab = DeploymentsTab();
        var action = ArmRowAction(tab, RowActionKind.Scale);
        action.SetCurrentScale(4, 1);
        action.Replicas = 0;
        return tab;
    }

    /// <summary>
    /// Delete, armed on a production cluster: the strip names the cluster and says
    /// "(production)", its border takes the production colour (B3-1), and the glyph and the
    /// confirm are red (FEAT-77). On production this is what a delete shows even with
    /// "Confirm before deleting" turned off.
    /// </summary>
    public static ClusterTabViewModel RowActionDeleteProduction()
    {
        var tab = BaseTab();
        tab.SelectedRow = tab.Rows.FirstOrDefault(r => r.Name.StartsWith("checkout-worker", StringComparison.Ordinal))
            ?? tab.Rows.First();
        ArmRowAction(tab, RowActionKind.Delete);
        return tab;
    }

    /// <summary>Rollout restart, armed — the one-click action every competitor has and this app didn't.</summary>
    public static ClusterTabViewModel RowActionRestart()
    {
        var tab = DeploymentsTab();
        ArmRowAction(tab, RowActionKind.Restart);
        return tab;
    }

    /// <summary>
    /// The failure that actually happens: RBAC. The API server's own sentence names the
    /// subject, the verb and the resource, and it lands in the strip's InfoBar rather
    /// than anywhere the action can be mistaken for having worked (UI rule 9).
    /// </summary>
    public static ClusterTabViewModel RowActionFailed()
    {
        var tab = DeploymentsTab();
        var action = ArmRowAction(tab, RowActionKind.Restart);
        action.IsError = true;
        action.Message =
            "Restart failed: deployments.apps \"checkout-worker\" is forbidden: User \"deploy-bot\" "
            + "cannot patch resource \"deployments\" in API group \"apps\" in the namespace \"payments\"";
        return tab;
    }

    /// <summary>
    /// "Open a terminal on this cluster" when the machine has no <c>kubectl</c> — the
    /// state the backlog item is explicitly about, and the one the app has to say out
    /// loud because the terminal itself opens in front of the window.
    ///
    /// <para>
    /// The launch is not run: this harness has no terminal emulator, and a scenario that
    /// spawned processes would be a different kind of thing entirely. What is run is the
    /// app's own <see cref="ClusterTabViewModel.DescribeTerminalLaunch"/>, so the words
    /// on the screenshot are the words the app produces, not a fixture's paraphrase of
    /// them. The paths are obviously-synthetic, like every other fixture value.
    /// </para>
    /// </summary>
    public static ClusterTabViewModel TerminalNoKubectl()
    {
        var tab = BaseTab();
        var result = new TerminalLaunchResult(
            TerminalLaunchOutcome.Opened,
            TerminalLabel: "gnome-terminal",
            KubectlPath: null,
            KubeconfigValue:
            "/home/dev/.config/kubeNimbus/terminal/context-4f21ab90c7d3.kubeconfig:/home/dev/.kube/config",
            ContextName: tab.Context.Name,
            Tried: ["xdg-terminal-exec", "gnome-terminal"],
            Error: null);

        var (message, warning, error) = ClusterTabViewModel.DescribeTerminalLaunch(result);
        tab.TerminalNotice = message;
        tab.TerminalNoticeIsWarning = warning;
        tab.TerminalNoticeIsError = error;
        return tab;
    }

    /// <summary>
    /// The demo cluster's answer to the same gesture, driven through the real command —
    /// there is no kubeconfig behind the demo dataset, so it refuses in place and says
    /// why rather than opening a terminal pointed at a sentinel path.
    /// </summary>
    public static ClusterTabViewModel DemoTerminalUnavailable()
    {
        var tab = DemoTab();
        tab.OpenInTerminalCommand.Execute(null);
        return tab;
    }

    /// <summary>
    /// The demo cluster's answer. Scale needs an API server and the demo has none, so
    /// the strip arms, names what it cannot do and disables its confirm — the same
    /// treatment exec and port-forward get, and never a silent no-op.
    /// </summary>
    public static ClusterTabViewModel DemoScaleUnavailable()
    {
        var tab = DemoTab();
        var kind = tab.SidebarSections
            .First(s => s.Title == "Workloads").Kinds
            .First(k => k.Descriptor.Kind == "Deployment");
        tab.SelectKindCommand.Execute(kind);
        tab.SelectedRow = tab.Rows.FirstOrDefault();
        tab.ScaleSelectedCommand.Execute(null);
        return tab;
    }

    // ---------------------------------------------------------------- Argo CD
    //
    // All three run on the demo cluster, which is where the Argo dataset lives — seven
    // Applications covering every state the dashboard classifies. The parse, the counts,
    // the ordering and both panes are production code; only the sync and refresh requests
    // have no offline stand-in, which is exactly what the strip's notice says.

    private static ClusterTabViewModel ArgoTab()
    {
        var tab = DemoTab();
        var kind = tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.IsArgoDashboard);
        tab.SelectKindCommand.Execute(kind);
        return tab;
    }

    /// <summary>
    /// The GitOps dashboard. What to look at: the seven counts, the attention line under
    /// them, and the ordering — <c>fraud-detector</c> at the top because it is Synced and
    /// Degraded, which is the case the two independent pills exist for (a single Status
    /// column would have to pick one of the two and be wrong about the other).
    /// </summary>
    public static ClusterTabViewModel ArgoDashboard() => ArgoTab();

    /// <summary>
    /// One Application's detail pane, opened on the degraded one. Maximized: the managed
    /// resources sit under the overview card, and at the dock's default ~300px the thing
    /// this pane exists to show — <em>which</em> resource is degraded — is below the fold.
    /// </summary>
    public static ClusterTabViewModel ArgoApplicationDetail()
    {
        var tab = ArgoTab();
        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == "fraud-detector");
        tab.OpenArgoApplicationCommand.Execute(null);

        if (tab.SelectedInspectorTab is ArgoApplicationTabViewModel detail)
        {
            // The Resources tab, which is the one carrying the answer. The strip and the
            // TabControl both bind to this index, so this is exactly what a click does.
            detail.SelectedTabIndex = ArgoApplicationTabViewModel.ResourcesTabIndex;
        }

        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>
    /// A sync on the demo cluster. It fires on its click (UI rule 17), so the strip is only its
    /// result line: nothing was sent, named against the Application, with Close. This and the
    /// prune confirm below run the real command path end to end, since the demo path is
    /// designed to work without a client.
    /// </summary>
    public static ClusterTabViewModel ArgoSyncUnavailable()
    {
        var tab = ArgoTab();
        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == "ledger-api");
        tab.SyncArgoApplicationCommand.Execute(null);
        return tab;
    }

    /// <summary>
    /// Sync with prune, armed on the demo cluster: the half of a sync that deletes, so the one
    /// sync that asks first — the sentence that says what it deletes, and the in-place refusal.
    /// </summary>
    public static ClusterTabViewModel ArgoSyncPrune()
    {
        var tab = ArgoTab();
        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == "ledger-api");
        tab.SyncArgoApplicationWithPruneCommand.Execute(null);
        return tab;
    }

    // --------------------------------------------------------------- CronJobs
    //
    // FEAT-8 on the demo cluster, which ships one CronJob on its schedule and one
    // suspended. The capability checks, the confirm sentences and the demo refusal run
    // through the real commands; the created-Job state is written in, as the drain's
    // progress is, because creating a Job needs an API server.

    private static ClusterTabViewModel CronJobTab(string name)
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "batch", Kind: "CronJob" }));
        tab.SelectedRow = tab.Rows.First(r => r.Name == name);
        return tab;
    }

    /// <summary>Run now, armed on the demo cluster: the sentence, and the in-place refusal.</summary>
    public static ClusterTabViewModel CronJobRunNow()
    {
        var tab = CronJobTab("nightly-reconcile");
        tab.TriggerSelectedCommand.Execute(null);
        return tab;
    }

    /// <summary>
    /// After a run-now went through: the server's name for the Job, and "Open Job" in the
    /// confirm's slot — the one follow-up any of the strip's actions has.
    /// </summary>
    public static ClusterTabViewModel CronJobRunNowDone()
    {
        var tab = CronJobRunNow();
        var action = tab.PendingRowAction!;
        using var document = JsonDocument.Parse("""
            {"apiVersion":"batch/v1","kind":"Job","metadata":{"name":"nightly-reconcile-manual-x7k2m","namespace":"payments"}}
            """);
        action.OpenJob = _ => Task.CompletedTask;
        action.CreatedJob = new DynamicResource(document.RootElement.Clone());
        action.IsDone = true;
        action.IsSuccess = true;
        action.Message = "Created Job/nightly-reconcile-manual-x7k2m. Its pods appear as the Job controller starts them.";
        return tab;
    }

    /// <summary>Resume, armed on the suspended CronJob: the missed-run clause is the thing to read.</summary>
    public static ClusterTabViewModel CronJobResume()
    {
        var tab = CronJobTab("quarterly-report");
        tab.ResumeSelectedCommand.Execute(null);
        return tab;
    }

    /// <summary>
    /// A Job in the workload pane: its run progress against the backoff limit and, on the
    /// Conditions tab, why it failed. The demo run's pods are gone, which is the ordinary
    /// state of a failed Job an hour later and reads as such.
    /// </summary>
    public static ClusterTabViewModel JobDetail()
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "batch", Kind: "Job" }));
        tab.SelectedRow = tab.Rows.First(r => r.Name == "nightly-reconcile-29230920");
        tab.OpenSelectedCommand.Execute(null);
        if (tab.SelectedInspectorTab is WorkloadDetailTabViewModel detail)
        {
            detail.SelectedTabIndex = 1;
        }

        return tab;
    }

    /// <summary>
    /// FEAT-47: the demo's PersistentVolumes, a bound one and one nothing has claimed. The
    /// Details column names the bound volume's claim, as kubectl's CLAIM column does; the
    /// row's menu then carries "Open claim payments/data-redis-cache-0".
    /// </summary>
    public static ClusterTabViewModel PersistentVolumes()
    {
        var tab = DemoTab();
        var storage = tab.SidebarSections.First(s => s.Kinds.Any(k => k.Descriptor is { Group: "", Kind: "PersistentVolume" }));
        storage.IsExpanded = true;
        tab.SelectKindCommand.Execute(storage.Kinds.First(k => k.Descriptor is { Group: "", Kind: "PersistentVolume" }));
        tab.SelectedRow = tab.Rows.FirstOrDefault(r => r.Name.StartsWith("pvc-", StringComparison.Ordinal));
        return tab;
    }

    // ------------------------------------------------------------------ nodes
    //
    // Every one of these runs on the demo cluster, which is where the node dataset lives
    // (three nodes, one of them cordoned and under disk pressure). The plan, the
    // classification, the arithmetic and the pane are all production code — only the
    // eviction itself has no offline stand-in, which is exactly what the demo notice on
    // the strip says.

    private static ClusterTabViewModel NodeTab()
    {
        var tab = DemoTab();
        var kind = tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "Node" });
        tab.SelectKindCommand.Execute(kind);
        return tab;
    }

    private static ClusterTabViewModel OpenNode(string name, int tabIndex = 0)
    {
        var tab = NodeTab();
        tab.SelectedRow = tab.Rows.First(r => r.Name == name);
        tab.OpenSelectedCommand.Execute(null);

        if (tab.SelectedInspectorTab is NodeDetailTabViewModel detail)
        {
            detail.SelectedTabIndex = tabIndex;

            // A selected pod, so the Pods shot shows the row's logs icon (L3).
            detail.SelectedPod = detail.Pods.FirstOrDefault();
        }

        return tab;
    }

    /// <summary>
    /// L3's "pod gone" state: a logs open on a pod the node's list still names but the
    /// cluster no longer has, stated above the list in place of a dead click. The ghost row
    /// is opened through the real <c>OpenPodLogsAsync</c>, so the sentence is the one the
    /// shared resolver writes (the demo's own variant of it — the dataset is its cluster).
    /// </summary>
    public static ClusterTabViewModel NodeDetailPodGone()
    {
        var tab = OpenNode("demo-worker-1", tabIndex: NodeDetailTabViewModel.PodsTabIndex);
        if (tab.SelectedInspectorTab is NodeDetailTabViewModel detail)
        {
            using var document = JsonDocument.Parse("""
                {"apiVersion":"v1","kind":"Pod",
                 "metadata":{"name":"checkout-worker-5d8f7b9c4-x7k2m","namespace":"payments"},
                 "spec":{"nodeName":"demo-worker-1","containers":[{"name":"worker"}]},
                 "status":{"phase":"Running"}}
                """);
            var ghost = new NodePodViewModel(new DynamicResource(document.RootElement.Clone()));
            detail.OpenPodLogsAsync(ghost, maximized: false).GetAwaiter().GetResult();
        }

        return tab;
    }

    /// <summary>
    /// The demo Events list with an event about a pod selected, so the row's logs icon shows
    /// at the end of its Object cell (L3). All namespaces, Config expanded.
    /// </summary>
    public static ClusterTabViewModel DemoEventsPodLogs()
    {
        var tab = DemoTab();
        tab.SelectedNamespace = ClusterTabViewModel.AllNamespaces;
        var config = tab.SidebarSections.First(s => s.Kinds.Any(k => k.Descriptor is { Group: "", Kind: "Event" }));
        config.IsExpanded = true;
        tab.SelectKindCommand.Execute(config.Kinds.First(k => k.Descriptor is { Group: "", Kind: "Event" }));
        tab.SelectedRow = tab.VisibleRows.First(r => r.Resource.InvolvedObject() is { Kind: "Pod" });
        return tab;
    }

    /// <summary>
    /// The node list. What to look at: <c>Ready,SchedulingDisabled</c> on the cordoned
    /// worker, coloured warn rather than ok — kubectl's own string, which
    /// <c>ResourceStatusSummary.SummarizeNode</c> already produced before this item and
    /// which had no detail pane behind it.
    /// </summary>
    public static ClusterTabViewModel NodeList() => NodeTab();

    /// <summary>
    /// Node detail's Overview: allocatable vs requested with a bar per resource,
    /// conditions, taints and the kubelet block. The headroom figures are the production
    /// arithmetic over the demo pods actually placed on this node — including the
    /// scheduler's own rule that an init container floors rather than adds, and that a
    /// finished Job pod holds nothing.
    /// </summary>
    public static ClusterTabViewModel NodeDetail() => OpenNode("demo-worker-1", tabIndex: 0);

    /// <summary>The Pods tab: what is actually on the node, with each pod's own requests.</summary>
    public static ClusterTabViewModel NodeDetailPods() => OpenNode("demo-worker-1", tabIndex: 1);

    /// <summary>
    /// The Events tab on the node under disk pressure: the kubelet's own account of it
    /// (EvictionThresholdMet, the image GC that could not free enough) as Warning cards
    /// above the Normal transitions. These carry the node's <em>name</em> as their UID,
    /// which is why the selector matches kind and name.
    /// </summary>
    public static ClusterTabViewModel NodeDetailEvents() =>
        OpenNode("demo-worker-2", tabIndex: NodeDetailTabViewModel.EventsTabIndex);

    /// <summary>
    /// The Usage tab: measured CPU and memory over the replayed window, now and peak, and
    /// each as a share of allocatable — the same denominator as the Overview's requested
    /// bars, so "requested vs used" compares figures of one node.
    /// </summary>
    public static ClusterTabViewModel NodeDetailUsage() =>
        OpenNode("demo-worker-1", tabIndex: NodeDetailTabViewModel.UsageTabIndex);

    /// <summary>
    /// The state the whole surface exists for: a node that is cordoned <em>and</em>
    /// reporting disk pressure. Both halves of "nothing lands here" are on the pane —
    /// the status pill and the scheduler's own <c>node.kubernetes.io/unschedulable</c>
    /// taint — and the DiskPressure condition reads as a fault rather than as another
    /// row of False.
    /// </summary>
    /// <remarks>
    /// Maximized, because the taints are the half of this that the ~300px dock cuts off
    /// and they are half of the point: the cordon and its taint are two different fields
    /// saying the same thing, and a reader who sees only one of them wonders which is
    /// real.
    /// </remarks>
    public static ClusterTabViewModel NodeDetailCordoned()
    {
        var tab = OpenNode("demo-worker-2", tabIndex: 0);
        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>
    /// A drain, armed and refusing. This is the single most important image of the item:
    /// the plan is computed before anything is evicted, and the two pods that would be
    /// destroyed rather than moved — one no controller owns, one whose <c>emptyDir</c>
    /// goes with it — are named, with the option that would unblock each. Both are
    /// off by default and the confirm is dead while either refusal stands.
    /// </summary>
    public static ClusterTabViewModel NodeDrainBlocked()
    {
        var tab = NodeTab();
        tab.SelectedRow = tab.Rows.First(r => r.Name == "demo-worker-1");
        tab.DrainSelectedCommand.Execute(null);
        Drain(tab);
        return tab;
    }

    /// <summary>
    /// A drain in flight. The eviction loop needs an API server, so the strip's progress
    /// is written in here — obviously-synthetic, like every other fixture — but every
    /// state it renders is one <c>ClusterClient.DrainNodeAsync</c> really produces: an
    /// accepted eviction, a pod a PodDisruptionBudget is holding back (which is
    /// <em>correct</em> behaviour and must not read as a failure), and an RBAC refusal
    /// that retrying will not fix. Stop replaces Cancel outright while this is running.
    /// </summary>
    public static ClusterTabViewModel NodeDrainRunning()
    {
        var tab = NodeDrainBlocked();
        var action = tab.PendingRowAction!;
        action.DrainForce = true;
        action.DrainDeleteEmptyDirData = true;
        action.IsDraining = true;
        action.Message = "3 pods still on the node.";
        AddSteps(action,
            ("payments/payment-service-report-generator-7f9c8d6bcd-x7k2m", DrainStage.PodEvicted, "eviction accepted"),
            ("payments/checkout-worker-5d8f7b9c4-qz9pl", DrainStage.PodBlocked,
                "Cannot evict pod as it would violate the pod's disruption budget."),
            ("kube-system/legacy-batch-runner", DrainStage.PodFailed,
                "pods \"legacy-batch-runner\" is forbidden: User \"deploy-bot\" cannot create resource \"pods/eviction\""));
        return tab;
    }

    /// <summary>
    /// The state CLAUDE.md's node section calls the one that must never be implicit: a
    /// drain stopped halfway. The node stays cordoned, some pods moved and some did not,
    /// and the strip says exactly that plus what to do about it — which is the whole
    /// answer to "this drain runs in a desktop app and the desktop app can be closed".
    /// </summary>
    public static ClusterTabViewModel NodeDrainStopped()
    {
        var tab = NodeDrainRunning();
        var action = tab.PendingRowAction!;
        action.IsDraining = false;
        action.IsDone = true;
        action.IsError = true;
        action.Message =
            "Drain stopped. 1 pod(s) were evicted and the rest were not; demo-worker-1 is still cordoned. "
            + "Run the drain again to finish, or uncordon to put the node back into service as it is.";
        return tab;
    }

    private static void AddSteps(
        RowActionViewModel action, params (string Pod, DrainStage Stage, string Detail)[] steps)
    {
        foreach (var (pod, stage, detail) in steps)
        {
            action.DrainSteps.Add(new DrainStepViewModel(pod) { Stage = stage, Detail = detail });
        }
    }

    /// <summary>
    /// The demo drain's plan is loaded by an async command; the headless harness has to
    /// let it land before capturing. It never touches the network — the pods come from
    /// the shipped dataset — so one dispatcher drain is enough.
    /// </summary>
    private static void Drain(ClusterTabViewModel tab)
    {
        _ = tab;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The list narrowed by its search box. Goes through the real <c>RowFilter</c>
    /// setter, so what renders is what typing produces — including the "n of m"
    /// beside the box.
    /// </summary>
    public static ClusterTabViewModel FilteredList()
    {
        var tab = BaseTab();
        tab.RowFilter = "check";
        tab.SelectedRow = tab.VisibleRows.FirstOrDefault();
        return tab;
    }

    /// <summary>
    /// The list as the reader has re-cut it: sorted by a header click, with the Name
    /// column dragged wider.
    ///
    /// <para>
    /// Both are read back out of the workspace by production code — the sort by
    /// <c>ClusterTabViewModel</c>'s own <c>SelectedKind</c> hook, the width by
    /// <c>ClusterTabView.ApplyColumnLayout</c> — so this renders the restore path rather
    /// than a grid set up by hand. Sorted by Age, which is the column whose text ("5m",
    /// "16d") sorts wrongly if anything reads it as a string.
    /// </para>
    /// </summary>
    public static ClusterTabViewModel SortedList()
    {
        var podKey = GridLayoutStore.KeyFor(
            FixtureData.BuildCatalog().First(d => d is { Group: "", Kind: "Pod" }));
        GridLayoutStore.Update(podKey, layout => layout with
        {
            SortColumn = ResourceColumn.Age,
            SortDescending = true,
            ColumnWidths = new Dictionary<string, GridColumnWidth>
            {
                [ResourceColumn.Name] = new(GridColumnWidth.Star, 9),
                [ResourceColumn.Namespace] = new(GridColumnWidth.Pixels, 118),
            },
        });

        return BaseTab();
    }

    /// <summary>
    /// A search that matches nothing — a different state from an empty namespace, and
    /// the one the list had no visual for at all before the box existed.
    /// </summary>
    public static ClusterTabViewModel FilteredListEmpty()
    {
        var tab = BaseTab();
        tab.RowFilter = "nginx-ingress";
        return tab;
    }

    /// <summary>
    /// Unhealthy only, on the payments pod list: the CrashLoopBackOff worker and the
    /// Pending fraud detector, and nothing else. Through the real property, so the
    /// chip's checked state, the "2 of 8 unhealthy" caption and the rows are all what
    /// the toggle produces.
    /// </summary>
    public static ClusterTabViewModel UnhealthyList()
    {
        var tab = BaseTab();
        tab.IsUnhealthyOnly = true;
        tab.SelectedRow = tab.VisibleRows.FirstOrDefault();
        return tab;
    }

    /// <summary>
    /// Unhealthy only over a namespace where every pod is fine — the good-news state,
    /// which has to look like neither "no pods here" nor "nothing matches".
    /// </summary>
    public static ClusterTabViewModel UnhealthyListAllHealthy()
    {
        var tab = BaseTab(populateRows: false);
        tab.SelectedNamespace = "kube-system";
        foreach (var pod in FixtureData.Pods.Where(p => p.Namespace == "kube-system"))
        {
            tab.Rows.Add(new ResourceRowViewModel(pod));
        }

        tab.IsListLoading = false;
        tab.IsUnhealthyOnly = true;
        return tab;
    }

    /// <summary>The same mode over a partial fleet: filtering rows from every cluster
    /// still in it, with the unreachable member still stated in the header.</summary>
    public static ClusterTabViewModel UnhealthyFleetPartial()
    {
        var tab = FleetListPartial();
        tab.IsUnhealthyOnly = true;
        tab.SelectedRow = tab.VisibleRows.FirstOrDefault();
        return tab;
    }

    /// <summary>
    /// The mode left on while a kind with no health verdict is showing: the chip stays
    /// checked but disabled (its tooltip says why), and the list is the whole list rather
    /// than an always-empty one.
    /// </summary>
    public static ClusterTabViewModel UnhealthyUnavailable()
    {
        var tab = BaseTab(populateRows: false);
        tab.IsUnhealthyOnly = true;
        var configMaps = tab.SidebarSections.SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "ConfigMap" });
        tab.SelectedKind = configMaps;
        foreach (var configMap in DemoData.ConfigMaps.Where(c => c.Namespace == "payments"))
        {
            tab.Rows.Add(new ResourceRowViewModel(configMap));
        }

        tab.IsListLoading = false;
        return tab;
    }

    /// <summary>
    /// Unhealthy only on the demo cluster's own pod list, reached through the production
    /// connect path rather than a fixture — the half of the item's sandbox check a local
    /// API-server-only cluster cannot give, since no pod there ever starts.
    /// </summary>
    public static ClusterTabViewModel DemoUnhealthy()
    {
        var tab = DemoTab();
        tab.IsUnhealthyOnly = true;
        return tab;
    }

    public static ClusterTabViewModel EmptyNamespace()
    {
        var tab = BaseTab(populateRows: false);
        tab.SelectedNamespace = "kube-system";
        tab.IsListEmpty = true;
        return tab;
    }

    public static ClusterTabViewModel Loading()
    {
        var tab = BaseTab(populateRows: false);
        tab.IsListLoading = true;
        return tab;
    }

    public static ClusterTabViewModel Disconnected()
    {
        var tab = BaseTab();
        tab.ConnectionWarning = "Watch connection lost (SocketException); retrying in 4s.";
        tab.ConnectionWarningOffersReconnect = true;
        return tab;
    }

    /// <summary>
    /// A watch whose credential was refused mid-session (an SSO session ending) — the
    /// warning the informer raises for a 401, with the Reconnect button beside it.
    /// </summary>
    public static ClusterTabViewModel CredentialsExpired()
    {
        var tab = BaseTab();
        tab.ConnectionWarning =
            "The cluster rejected the credentials (401) — they have probably expired. kubeNimbus re-read the kubeconfig; retrying in 4s. Sign in again and the retry picks it up.";
        tab.ConnectionWarningOffersReconnect = true;
        return tab;
    }

    /// <summary>
    /// A healthy tab whose cluster entry sets <c>insecure-skip-tls-verify</c>: the routine
    /// "Connected" line would hide the status bar, and the TLS notice keeps it on screen.
    /// </summary>
    public static ClusterTabViewModel TlsUnverified()
    {
        var tab = BaseTab();
        tab.IsTlsUnverified = true;
        return tab;
    }

    /// <summary>
    /// A healthy tab whose cluster's server is a plain <c>http://</c> URL (ENG-59): the notice
    /// keeps the status bar on screen, in the slot the TLS notice uses.
    /// </summary>
    public static ClusterTabViewModel PlainHttp()
    {
        var tab = BaseTab();
        tab.IsPlainHttp = true;
        return tab;
    }

    /// <summary>
    /// A connect that failed, as the content area states it. The report is written in
    /// rather than produced by a failing connect so the shot is the same on every machine
    /// (no temp paths, no developer home directory); its sentences are the ones
    /// <see cref="ConnectionReport"/> produces for the same cause, which
    /// <c>ConnectionReportTests</c> pins.
    /// </summary>
    public static ClusterTabViewModel ConnectionFailed(string kind = "plugin")
    {
        var context = new ClusterContext("prod-eks-eu", "arn:aws:eks:eu-west-1:1234:cluster/prod", null, "prod-sso", "fixture");
        var tab = new ClusterTabViewModel(context);
        var server = "https://4F2A9C.gr7.eu-west-1.eks.amazonaws.com";
        IReadOnlyList<ConnectionFact> facts = kind == "plugin"
            ?
            [
                new("Kubeconfig", "/Users/reviewer/.kube/config"),
                new("Context", context.Name),
                new("Cluster", context.ClusterName),
                new("Server", server),
                new("User", "prod-sso"),
                new("Signs in with", "credential plugin aws (not found on PATH or in the usual install folders)"),
                new("Plugin install hint", "Install the AWS CLI: https://aws.amazon.com/cli/"),
            ]
            :
            [
                new("Kubeconfig", "/Users/reviewer/.kube/config"),
                new("Context", context.Name),
                new("Cluster", context.ClusterName),
                new("Server", server),
                new("User", "prod-sso"),
                new("Signs in with", "credential plugin aws (runs /opt/homebrew/bin/aws)"),
            ];

        var report = kind == "plugin"
            ? new ConnectionFailureReport(
                ConnectionReport.RunningPlugin,
                "The kubeconfig's credential plugin could not be started.",
                "Could not run the kubeconfig's credential plugin: An error occurred trying to start process 'aws' with working directory '/'. No such file or directory",
                "Install it, or put its full path in the kubeconfig's exec command. kubeNimbus looked on this app's PATH and in /usr/local/bin, /opt/homebrew/bin, /opt/local/bin, /Users/reviewer/.local/bin, /Users/reviewer/bin.",
                facts)
            : new ConnectionFailureReport(
                ConnectionReport.SigningIn,
                "The API server rejected the credentials (401 Unauthorized).",
                "Unauthorized (401 Unauthorized)",
                "They have most likely expired. Sign in again the way you normally do — aws sso login, az login, gcloud auth login — then Retry. kubeNimbus re-reads the kubeconfig on every attempt and keeps no copy of any credential.",
                facts);

        tab.ConnectionFailure = new ConnectionFailureViewModel(report, tab);
        tab.Status = $"Connection failed ({report.StepPhrase}).";
        return tab;
    }

    public static ClusterTabViewModel PodDetail(bool seedUsage = true, string namePrefix = "payment-service-report-generator")
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith(namePrefix, StringComparison.Ordinal));
        tab.SelectedRow = row;

        var client = FixtureData.CreateOfflineClient();
        var detail = new PodDetailTabViewModel(client, row, _ => { }, (_, _) => Task.CompletedTask) { IsPreview = false };

        foreach (var raw in new[]
        {
            "2026-07-20T08:41:02.114Z INFO  starting report-generator v2.14.3",
            "2026-07-20T08:41:02.331Z INFO  connected to postgres primary (payments-db.internal:5432)",
            "2026-07-20T08:41:02.402Z INFO  listening on :8080",
            "2026-07-20T08:44:17.008Z INFO  generated monthly-settlement report for merchant=acme-retail (842ms)",
            "2026-07-20T08:44:55.771Z WARN  slow query detected: SELECT * FROM settlements WHERE ... (1204ms)",
            "2026-07-20T08:44:58.019Z ERROR failed to upload report to blob storage: connection reset by peer",
            "2026-07-20T08:45:01.220Z INFO  generated chargeback-summary report for merchant=north-store (391ms)",
        })
        {
            detail.Enqueue(raw);
        }

        // Through the pane's own flush, so the projection, the problem counts and the ruler
        // see these lines the way they see a stream's.
        detail.FlushLogLines();

        detail.IsFollowingLogs = true;

        // Same reasoning as the list rows: no live metrics API behind the fixture,
        // so replay a session's worth of stand-in polls through the tab's real
        // ApplyMetrics — that populates the container usage chips *and* the Usage
        // tab's charts from production code rather than from a second code path.
        if (seedUsage)
        {
            SeedPodUsage(detail);
        }

        detail.Events.Clear();
        foreach (var e in FixtureData.Events.Where(e => e.InvolvedObject() is not { Kind: "Node" }))
        {
            detail.Events.Add(new EventRowViewModel(e));
        }

        tab.InspectorTabs.Add(detail);
        tab.SelectedInspectorTab = detail;
        return tab;
    }

    /// <summary>
    /// Pod detail's Environment tab: literal values, ConfigMap refs resolved in place,
    /// Secret refs masked, and one Secret revealed so both sides of the eye toggle are
    /// on screen at once.
    ///
    /// The tab auto-resolves its ConfigMap refs on build, which against the offline
    /// client fails fast and lands on the same <c>RunJobs()</c> the capture pumps — so
    /// the resolve is drained first and the fixture values written afterwards, leaving
    /// the rows exactly as a cluster that answered would have (same reasoning as
    /// <see cref="HelmReleaseDetail"/>).
    /// </summary>
    public static ClusterTabViewModel PodDetailEnvironment()
    {
        var tab = PodDetail();
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            detail.SelectedDetailTabIndex = 1;

            for (var i = 0; i < 100 && detail.EnvironmentVars.Any(v => v.IsRevealing); i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            foreach (var v in detail.EnvironmentVars)
            {
                v.RevealError = null;
            }

            // A ConfigMap value: on screen without being asked for.
            Reveal(detail, "FEATURE_FLAGS", "checkout_v2=on,receipts_pdf=off");
            // A Secret value someone has clicked the eye on. Every other Secret ref in
            // the fixture stays masked, which is the default this pass is about.
            Reveal(detail, "DB_USERNAME", "payments_svc");
        }

        return tab;

        static void Reveal(PodDetailTabViewModel detail, string name, string value)
        {
            if (detail.EnvironmentVars.FirstOrDefault(v => v.Name == name) is { } v)
            {
                v.RevealedValue = value;
                v.IsRevealed = true;
            }
        }
    }

    private static void SeedPodUsage(PodDetailTabViewModel detail) => DemoUsage.SeedPod(detail, FixtureNow);

    /// <summary>Pod detail's Usage tab — CPU/memory over the session's poll window, pod
    /// total plus per container.</summary>
    public static ClusterTabViewModel PodDetailUsage()
    {
        var tab = PodDetail();
        // Maximized: the per-container requests/limits are the point of this tab and sat
        // below the fold of a ~300px dock, which is exactly how they stayed unread.
        tab.IsInspectorMaximized = true;
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            detail.SelectedDetailTabIndex = 3;
        }

        return tab;
    }

    /// <summary>
    /// A container that declares neither a request nor a limit, on a pod that has never
    /// run (the dataset's unschedulable fraud-detector, so no metrics of its own). Both
    /// halves of the declared line therefore have to say so in words — the state UI rule 9
    /// is about, and the commonest one there is on a real cluster.
    /// </summary>
    public static ClusterTabViewModel PodDetailUsageUnset()
    {
        var tab = PodDetail(seedUsage: false, namePrefix: "fraud-detector");
        tab.IsInspectorMaximized = true;
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            detail.SelectedDetailTabIndex = 3;
        }

        return tab;
    }

    /// <summary>
    /// The Usage tab on a cluster with no metrics-server — the degradation path that
    /// has to read as "nothing to show here", not as a chart still loading.
    /// </summary>
    public static ClusterTabViewModel PodDetailUsageUnavailable()
    {
        var tab = PodDetail(seedUsage: false);
        tab.IsInspectorMaximized = true;
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            detail.SelectedDetailTabIndex = 3;
            detail.IsMetricsUnavailable = true;
        }

        return tab;
    }

    /// <summary>
    /// Pod detail's Overview tab, populated: conditions, tolerations (the two the
    /// DefaultTolerationSeconds plugin adds included), a node selector, QoS and priority
    /// class, and the selected container's three probes.
    /// </summary>
    public static ClusterTabViewModel PodDetailOverview()
    {
        var tab = PodDetail();
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            detail.SelectedDetailTabIndex = 4;
        }

        // Maximized, because four cards do not fit the dock's ~300px default and the
        // dock's own maximize toggle is what this tab wants — the same call the exec and
        // YAML scenarios make for the same reason. The unmaximized state is a scroll,
        // which is correct and not worth a second image.
        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>
    /// The other half of the same tab, and the state someone actually opens it in: a pod
    /// that will not schedule. One genuinely-bad condition with the scheduler's own
    /// message, and every other section empty — which is the whole of UI rule 9 for this
    /// pane, since "no tolerations" and "no probes" are answers rather than blanks.
    /// </summary>
    public static ClusterTabViewModel PodDetailOverviewUnschedulable()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("fraud-detector", StringComparison.Ordinal));
        tab.SelectedRow = row;

        var detail = new PodDetailTabViewModel(
            FixtureData.CreateOfflineClient(), row, _ => { }, (_, _) => Task.CompletedTask)
        {
            IsPreview = false,
            SelectedDetailTabIndex = 4,
        };

        tab.InspectorTabs.Add(detail);
        tab.SelectedInspectorTab = detail;
        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>
    /// The third condition polarity, which nothing in this repo rendered until now.
    ///
    /// <para>
    /// A pod's conditions are mostly the *positive* ones the scheduler and kubelet set,
    /// which is why <c>PodCondition.IsProblem</c> reads them the opposite way round from
    /// a node's. Two cases break that, and this terminating pod carries both:
    /// <c>DisruptionTarget</c>, the one type Kubernetes defines with the inverted
    /// polarity — True means the pod is being evicted, so it must render as a problem
    /// even though every other True on the card is green — and a custom readiness gate,
    /// whose type is on neither list and must therefore come back
    /// <c>Unclassified</c> and render grey rather than being guessed green.
    /// </para>
    ///
    /// <para>
    /// Both branches were reachable only in code before this scenario existed: no demo
    /// object and no fixture produced either, so the grey dot and the inverted red one
    /// had never appeared in a screenshot. That is the whole reason the shot is here —
    /// a false green on this card is a false reassurance for the one person reading it
    /// *because* something is wrong.
    /// </para>
    /// </summary>
    public static ClusterTabViewModel PodDetailOverviewDisrupted()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("notification-dispatcher", StringComparison.Ordinal));
        tab.SelectedRow = row;

        var detail = new PodDetailTabViewModel(
            FixtureData.CreateOfflineClient(), row, _ => { }, (_, _) => Task.CompletedTask)
        {
            IsPreview = false,
            SelectedDetailTabIndex = 4,
        };

        tab.InspectorTabs.Add(detail);
        tab.SelectedInspectorTab = detail;
        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>Pod detail's Events tab — Type-colored pills and the "open involved object" chevron.</summary>
    public static ClusterTabViewModel PodDetailEvents()
    {
        var tab = PodDetail();
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            detail.SelectedDetailTabIndex = 2;
        }

        return tab;
    }

    /// <summary>
    /// Helm release browser. The sidebar's Helm section only exists on clusters
    /// that store releases, so the fixture adds it the same way
    /// <c>AddHelmSectionIfPresentAsync</c> would after a successful probe.
    /// </summary>
    public static ClusterTabViewModel HelmReleases()
    {
        var tab = BaseTab();

        var helmSection = new SidebarSectionViewModel(SidebarGrouping.HelmSection);
        var helmKind = new SidebarKindViewModel(
            SidebarGrouping.HelmReleaseDescriptor, SidebarGrouping.IconKeyFor(SidebarGrouping.HelmSection));
        helmSection.Kinds.Add(helmKind);
        tab.SidebarSections.Add(helmSection);
        tab.SelectedKind = helmKind;
        tab.IsHelmView = true;
        tab.AreMetricsVisible = false;

        foreach (var release in FixtureData.HelmReleases)
        {
            tab.HelmReleases.Add(new HelmReleaseRowViewModel(release));
        }

        tab.SelectedHelmRelease = tab.HelmReleases.FirstOrDefault();
        tab.IsHelmEmpty = tab.HelmReleases.Count == 0;
        return tab;
    }

    /// <summary>
    /// One Helm release's detail panel. Until this existed <c>HelmReleaseView</c> was the
    /// only inspector view the harness never rendered, so nothing checked its XAML loaded
    /// — which is precisely what this harness is CI's smoke test for.
    ///
    /// The tab's constructor starts a load that fails fast against the offline client, and
    /// its continuation lands on the dispatcher — the same <c>RunJobs()</c> the capture
    /// pumps just before rendering, which is late enough to overwrite anything the fixture
    /// set first. So the load is drained here, and the fixture text is written afterwards,
    /// leaving the tab exactly as a successful <c>LoadAsync</c> would have.
    /// </summary>
    public static ClusterTabViewModel HelmReleaseDetail()
    {
        var tab = HelmReleases();
        var client = FixtureData.CreateOfflineClient();
        var release = FixtureData.HelmReleases[0];

        var helmTab = new HelmReleaseTabViewModel(client, release);

        for (var i = 0; i < 100 && helmTab.IsLoading; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        helmTab.IsPreview = false;
        helmTab.ValuesYaml = """
                replicaCount: 3
                image:
                  repository: registry.internal/payments/checkout
                  tag: "9.0.1"
                resources:
                  requests:
                    cpu: 250m
                    memory: 256Mi
                ingress:
                  enabled: true
                  host: checkout.payments.internal
                """;
        helmTab.Manifest = """
                # Source: checkout/templates/deployment.yaml
                apiVersion: apps/v1
                kind: Deployment
                metadata:
                  name: checkout-worker
                  namespace: payments
                spec:
                  replicas: 3
                """;
        helmTab.Notes = "checkout has been installed.\n\nGet the application URL:\n  kubectl -n payments port-forward svc/checkout 8080:80";

        foreach (var revision in FixtureData.HelmReleases)
        {
            helmTab.History.Add(new HelmReleaseRowViewModel(revision));
        }

        helmTab.SelectedRevision = helmTab.History.FirstOrDefault();
        helmTab.ErrorMessage = null;

        tab.InspectorTabs.Add(helmTab);
        tab.SelectedInspectorTab = helmTab;
        return tab;
    }

    /// <summary>
    /// The cluster-wide access review ("who can do X?"). The scan needs a real cluster's
    /// RBAC objects, so the fixture populates the results the same way
    /// <c>RunWhoCanAsync</c> would from a <see cref="WhoCanResult"/>.
    /// </summary>
    public static ClusterTabViewModel RbacWhoCan(bool empty = false)
    {
        var tab = BaseTab();
        var client = FixtureData.CreateOfflineClient();
        var query = new AccessQuery("delete", "pods", Namespace: "payments");

        var rbacTab = new RbacTabViewModel(client, "payments")
        {
            IsPreview = false,
            SelectedTabIndex = RbacTabViewModel.WhoCanTabIndex,
            WhoCanVerb = query.Verb,
            WhoCanResource = query.Resource,
            HasWhoCanRun = true,
            IsWhoCanRunning = false,
            WhoCanQueryText = query.Text,
        };

        if (empty)
        {
            rbacTab.IsWhoCanEmpty = true;
        }
        else
        {
            foreach (var access in WhoCanFixture())
            {
                rbacTab.WhoCanResults.Add(new WhoCanRowViewModel(client, query, access, CancellationToken.None));
            }
        }

        tab.InspectorTabs.Add(rbacTab);
        tab.SelectedInspectorTab = rbacTab;

        // The answer is a list of subjects with their granting rules nested under each —
        // it needs the whole content area, not the split dock's default sliver.
        tab.IsInspectorMaximized = true;

        // The only way into this pane is the palette's access-review entries, which
        // are advanced-view only.
        return tab;
    }

    /// <summary>
    /// Deliberately mixed: a cluster-wide wildcard grant, a narrow namespaced one, and a
    /// rule restricted to named objects — the three shapes that must read differently.
    /// </summary>
    private static SubjectAccess[] WhoCanFixture()
    {
        var wildcard = new PolicyRule(["*"], ["*"], ["*"], [], []);
        var podWrite = new PolicyRule(["get", "list", "delete"], [""], ["pods"], [], []);
        var namedPods = new PolicyRule(["delete"], [""], ["pods"], ["checkout-worker-0"], []);

        return
        [
            new SubjectAccess(
                new SubjectRef("Group", "system:masters", null),
                [new SubjectBinding("ClusterRoleBinding", "cluster-admin", null, "ClusterRole", "cluster-admin", [wildcard])]),
            new SubjectAccess(
                new SubjectRef("ServiceAccount", "deploy-bot", "payments"),
                [new SubjectBinding("RoleBinding", "payments-deployers", "payments", "Role", "pod-manager", [podWrite])]),
            new SubjectAccess(
                new SubjectRef("User", "oncall@example.com", null),
                [new SubjectBinding("RoleBinding", "oncall-restart", "payments", "ClusterRole", "pod-restarter", [namedPods])]),
        ];
    }

    /// <summary>
    /// My permissions under its "Signed in as" line (FEAT-56): the name and groups the API
    /// server reports (an EKS role, the case behind "I connected but everything is 403"), or,
    /// with <paramref name="identity"/> given, one of the ways it could not say. The review
    /// itself needs a real cluster, so the fixture sets what <c>LoadAsync</c> would.
    /// </summary>
    public static ClusterTabViewModel RbacMyPermissions(SelfSubjectIdentity? identity = null)
    {
        var tab = BaseTab();
        var rbacTab = new RbacTabViewModel(FixtureData.CreateOfflineClient(), "payments", load: false)
        {
            IsPreview = false,
            Identity = identity ?? new SelfSubjectIdentity(
                SelfSubjectReviewOutcome.Answered,
                "arn:aws:iam::111122223333:role/payments-dev",
                ["payments-oncall", "system:authenticated"],
                Detail: null),
        };

        rbacTab.MyRules.Add(new PolicyRule(["get", "list", "watch"], [""], ["pods", "pods/log", "services", "configmaps"], [], []));
        rbacTab.MyRules.Add(new PolicyRule(["get", "list", "watch"], ["apps"], ["deployments", "replicasets", "statefulsets"], [], []));
        rbacTab.MyRules.Add(new PolicyRule(["create"], [""], ["pods/exec"], [], []));
        rbacTab.MyRules.Add(new PolicyRule(["patch"], ["apps"], ["deployments"], ["checkout-worker"], []));
        rbacTab.MyRules.Add(new PolicyRule(["create"], ["authorization.k8s.io"], ["selfsubjectaccessreviews", "selfsubjectrulesreviews"], [], []));
        rbacTab.MyRules.Add(new PolicyRule(["get"], [], [], [], ["/healthz", "/version"]));

        tab.InspectorTabs.Add(rbacTab);
        tab.SelectedInspectorTab = rbacTab;
        return tab;
    }

    public static ClusterTabViewModel YamlEditor()
    {
        var tab = BaseTab();
        var deployment = FixtureData.Deployments.First(d => d.Name == "checkout-worker");
        var client = FixtureData.CreateOfflineClient();
        var yamlTab = new YamlEditorTabViewModel(client, DeploymentDescriptor, "payments", "checkout-worker", deployment.ToYaml())
        {
            IsPreview = false,
        };

        tab.InspectorTabs.Add(yamlTab);
        tab.SelectedInspectorTab = yamlTab;
        return tab;
    }

    /// <summary>
    /// The YAML editor's own delete confirm on a production cluster: it names the cluster and
    /// says "(production)", with the action strip's production border (B3-1). The cluster tab
    /// stamps the editor as it enters the dock; a fixture adds it by hand, so it stamps here.
    /// </summary>
    public static ClusterTabViewModel YamlEditorDeleteProduction()
    {
        var tab = YamlEditor();
        var editor = (YamlEditorTabViewModel)tab.SelectedInspectorTab!;
        editor.SetCluster(tab.Context.Name, ClusterEnvironments.Classify(tab.Context.Name, tab.Context.ClusterName));
        editor.IsConfirmingDelete = true;
        return tab;
    }

    public static ClusterTabViewModel YamlEditorMaximized()
    {
        var tab = YamlEditor();
        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>
    /// The apply preview: the server's own dry-run answer, armed but not applied. The
    /// diff is computed by the real <see cref="ResourceDiff"/> from two objects — the
    /// fixture deployment and an edited copy of it — so what renders is what the engine
    /// produces, not a hand-written list of rows. The one thing a fixture cannot supply
    /// is the dry-run response itself, which needs an API server.
    ///
    /// <para>
    /// Rendered maximized, because that is the state a diff worth reading is read in:
    /// at the dock's default ~300px the preview and the editor split the height and one
    /// row of the diff is visible at a time. The no-change scenario is the one that
    /// shows that split.
    /// </para>
    /// </summary>
    public static ClusterTabViewModel YamlEditorDiffPreview()
    {
        var tab = YamlEditor();
        tab.IsInspectorMaximized = true;
        if (tab.SelectedInspectorTab is YamlEditorTabViewModel yaml)
        {
            yaml.PendingPreview = new ApplyPreviewViewModel(FixturePreview(), isForce: false);
        }

        return tab;
    }

    /// <summary>
    /// The same diff as two aligned columns, and deliberately <b>not</b> maximized: the
    /// dock's default ~300px is where the panel's row heights fail first, and side by side
    /// is the mode that asks the most of the width. The alignment fillers — the blank a
    /// deleted line faces on the other side — are the whole reason this mode is more than
    /// a two-column layout, and nothing but a rendering shows whether they line up.
    /// </summary>
    public static ClusterTabViewModel YamlEditorDiffSplit()
    {
        var tab = YamlEditor();
        if (tab.SelectedInspectorTab is YamlEditorTabViewModel yaml)
        {
            yaml.PendingPreview = new ApplyPreviewViewModel(FixturePreview(), isForce: false);
            yaml.PreviewViewMode = YamlEditorTabViewModel.PreviewViewModeSplit;
        }

        return tab;
    }

    /// <summary>
    /// The field-path list — the third view mode, and the one that knows a container was
    /// inserted rather than every container rewritten. It is what the panel showed before
    /// the line diff existed, kept because a line count cannot say that.
    /// </summary>
    public static ClusterTabViewModel YamlEditorDiffFields()
    {
        var tab = YamlEditorDiffPreview();
        if (tab.SelectedInspectorTab is YamlEditorTabViewModel yaml)
        {
            yaml.PreviewViewMode = YamlEditorTabViewModel.PreviewViewModeFields;
        }

        return tab;
    }

    /// <summary>
    /// The same panel when the server says the apply would change nothing — a distinct
    /// state, and the answer to "did my edit actually do anything" (UI rule 9).
    /// </summary>
    public static ClusterTabViewModel YamlEditorDiffNoChange()
    {
        var tab = YamlEditor();
        if (tab.SelectedInspectorTab is YamlEditorTabViewModel yaml)
        {
            var live = FixtureLiveDeployment();
            yaml.PendingPreview = new ApplyPreviewViewModel(
                new ApplyPreview(
                    ResourceDiff.Between(live, live),
                    DynamicResource.FromListItem(live, DeploymentDescriptor),
                    DynamicResource.FromListItem(live, DeploymentDescriptor)),
                isForce: false);
        }

        return tab;
    }

    /// <summary>
    /// Strict field validation refusing a field the server does not know — the state the
    /// editor used to have no way to reach, because the API server's default Warn mode
    /// pruned the field and answered 200. Nothing was applied, the server's own sentence
    /// names the field, and there is deliberately no button: unlike a conflict there is
    /// nothing to force.
    /// </summary>
    public static ClusterTabViewModel YamlEditorValidationRejected()
    {
        var tab = YamlEditor();
        if (tab.SelectedInspectorTab is YamlEditorTabViewModel yaml)
        {
            yaml.ValidationDetails =
                "failed to create typed patch object (payments/checkout-worker; apps/v1, Kind=Deployment): "
                + ".spec.template.spec.contaienrs: field not declared in schema";
        }

        return tab;
    }

    /// <summary>
    /// A dry-run answer assembled the way <c>ClusterClient.PreviewApplyAsync</c> assembles
    /// one: the live object, the object the server says it would end up with, and the
    /// field diff of the pair. The panel computes its line diff from the two documents, so
    /// handing it only the diff would leave two of its three views empty.
    /// </summary>
    private static ApplyPreview FixturePreview()
    {
        var live = FixtureLiveDeployment();
        var previewed = FixturePreviewedDeployment();
        return new ApplyPreview(
            ResourceDiff.Between(live, previewed),
            DynamicResource.FromListItem(previewed, DeploymentDescriptor),
            DynamicResource.FromListItem(live, DeploymentDescriptor));
    }

    /// <summary>
    /// The object as the server holds it — including one bookkeeping field, so the panel's
    /// footnote is real, and enough untouched spec for the line diff to have unchanged runs
    /// worth collapsing. A four-line fixture would render a diff with no gaps in it, which
    /// is the one part of the panel a screenshot is uniquely able to check.
    /// </summary>
    private static JsonElement FixtureLiveDeployment() => JsonDocument.Parse("""
        {"apiVersion":"apps/v1","kind":"Deployment",
         "metadata":{"name":"checkout-worker","namespace":"payments","resourceVersion":"81523",
                     "labels":{"app.kubernetes.io/name":"checkout-worker"},
                     "annotations":{"deployment.kubernetes.io/revision":"7","kubenimbus.dev/owner":"payments-team"}},
         "spec":{"replicas":3,
                 "selector":{"matchLabels":{"app":"checkout-worker"}},
                 "strategy":{"type":"RollingUpdate","rollingUpdate":{"maxSurge":1,"maxUnavailable":0}},
                 "template":{
                   "metadata":{"labels":{"app":"checkout-worker"}},
                   "spec":{"serviceAccountName":"checkout",
                           "containers":[
                    {"name":"worker","image":"registry.example.com/checkout:1.14.2",
                     "args":["--queue","checkout","--concurrency","4"],
                     "ports":[{"name":"metrics","containerPort":9102}],
                     "env":[{"name":"LOG_LEVEL","value":"info"},
                            {"name":"QUEUE_URL","value":"amqp://broker.payments:5672"}],
                     "readinessProbe":{"httpGet":{"path":"/healthz","port":9102},"periodSeconds":10},
                     "resources":{"limits":{"memory":"512Mi"}}},
                    {"name":"metrics-sidecar","image":"prom/statsd-exporter:v0.27.1",
                     "ports":[{"name":"statsd","containerPort":9125}]}]}}}}
        """).RootElement.Clone();

    /// <summary>What a dry-run apply would return: two edits of the user's, plus a limit the cluster defaults in.</summary>
    private static JsonElement FixturePreviewedDeployment() => JsonDocument.Parse("""
        {"apiVersion":"apps/v1","kind":"Deployment",
         "metadata":{"name":"checkout-worker","namespace":"payments","resourceVersion":"81523",
                     "managedFields":[{"manager":"kubenimbus","operation":"Apply"}],
                     "labels":{"app.kubernetes.io/name":"checkout-worker","app.kubernetes.io/part-of":"payments"},
                     "annotations":{"deployment.kubernetes.io/revision":"7","kubenimbus.dev/owner":"payments-team"}},
         "spec":{"replicas":5,
                 "selector":{"matchLabels":{"app":"checkout-worker"}},
                 "strategy":{"type":"RollingUpdate","rollingUpdate":{"maxSurge":1,"maxUnavailable":0}},
                 "template":{
                   "metadata":{"labels":{"app":"checkout-worker"}},
                   "spec":{"serviceAccountName":"checkout",
                           "containers":[
                    {"name":"worker","image":"registry.example.com/checkout:1.15.0",
                     "args":["--queue","checkout","--concurrency","4"],
                     "ports":[{"name":"metrics","containerPort":9102}],
                     "env":[{"name":"LOG_LEVEL","value":"info"},
                            {"name":"QUEUE_URL","value":"amqp://broker.payments:5672"}],
                     "readinessProbe":{"httpGet":{"path":"/healthz","port":9102},"periodSeconds":10},
                     "resources":{"limits":{"memory":"1Gi","cpu":"500m"}}},
                    {"name":"metrics-sidecar","image":"prom/statsd-exporter:v0.27.1",
                     "ports":[{"name":"statsd","containerPort":9125}]}]}}}}
        """).RootElement.Clone();

    /// <summary>A server-side apply conflict, with the force-apply button that is the
    /// only thing resolving one from inside the app.</summary>
    public static ClusterTabViewModel YamlEditorConflict()
    {
        var tab = YamlEditor();
        if (tab.SelectedInspectorTab is YamlEditorTabViewModel yaml)
        {
            yaml.ConflictDetails = "Field .spec.replicas is owned by field manager \"kubectl-scale\" (apply conflicts with your changes).";
        }

        return tab;
    }

    /// <summary>Secret YAML — masked by default, matching kubectl's own base64 display.</summary>
    public static ClusterTabViewModel YamlEditorSecretMasked() => BuildYamlEditorSecret(reveal: false);

    /// <summary>Same Secret with "Reveal values" toggled on — exercises the real decode path (YamlJson parse + base64), not a stand-in.</summary>
    public static ClusterTabViewModel YamlEditorSecretRevealed() => BuildYamlEditorSecret(reveal: true);

    /// <summary>
    /// FEAT-30: the demo's <c>kubernetes.io/tls</c> Secret, opened through the real list on
    /// the demo cluster. The header's chip names the leaf and how long it has left (a real
    /// certificate generated for the demo, so the wording follows the wall clock the way
    /// every Age does), and the card is open on the chain — the leaf, and the CA that signed
    /// it, twice (once in the tls.crt bundle, once as ca.crt). Values stay masked: the card
    /// needs no Reveal, and the key is never read.
    /// </summary>
    public static ClusterTabViewModel YamlEditorTlsSecret()
    {
        var tab = DemoTab();
        var config = tab.SidebarSections.First(s => s.Kinds.Any(k => k.Descriptor is { Group: "", Kind: "Secret" }));
        config.IsExpanded = true;
        tab.SelectKindCommand.Execute(config.Kinds.First(k => k.Descriptor is { Group: "", Kind: "Secret" }));
        tab.SelectedRow = tab.Rows.First(r => r.Name == "checkout-tls");
        tab.OpenSelectedCommand.Execute(null);
        if (tab.SelectedInspectorTab is YamlEditorTabViewModel editor)
        {
            editor.IsCertificateDetailOpen = true;
        }

        tab.IsInspectorMaximized = true;
        return tab;
    }

    private static ClusterTabViewModel BuildYamlEditorSecret(bool reveal)
    {
        var tab = BaseTab();
        var client = FixtureData.CreateOfflineClient();
        var secret = FixtureData.Secret;
        var yamlTab = new YamlEditorTabViewModel(client, SecretDescriptor, secret.Namespace, secret.Name, secret.ToYaml())
        {
            IsPreview = false,
        };

        if (reveal)
        {
            yamlTab.ToggleSecretValuesRevealedCommand.Execute(null);
        }

        tab.InspectorTabs.Add(yamlTab);
        tab.SelectedInspectorTab = yamlTab;
        return tab;
    }

    /// <summary>
    /// A shell session with colour in it. Fed as escape sequences through the pane's
    /// own <see cref="ExecTabViewModel.Feed"/> — the same buffer and the same emulator
    /// the socket pump feeds — so what renders here is what the API server's bytes
    /// would render, not a screenshot-only approximation. This is the scenario that
    /// would go grey again if the terminal control ever stopped being wired up.
    /// </summary>
    public static ClusterTabViewModel Exec() => BuildExec(
        "/ # ls\r\n"
        + "\u001b[1;34mbin\u001b[0m   \u001b[1;34metc\u001b[0m   \u001b[1;34musr\u001b[0m   "
        + "\u001b[1;32mrun.sh\u001b[0m   report.log\r\n"
        + "/ # ./run.sh --once\r\n"
        + "\u001b[32mINFO \u001b[0m generating report for tenant=acme\r\n"
        + "\u001b[33mWARN \u001b[0m cache miss, falling back to the API\r\n"
        + "\u001b[31mERROR\u001b[0m upstream timed out after 5s\r\n"
        + "/ # ");

    /// <summary>
    /// The state the whole FEAT-10 item exists for: a full-screen tool. The frame is
    /// drawn the way <c>top</c> draws one — clear, home, then colour and reverse video
    /// at addressed positions — which the ANSI-stripping pane this replaced could not
    /// render at all (it printed the escape codes' remains as unspooling text).
    /// <para>
    /// The <c>ESC[7m</c> header is emitted with default colours, exactly as real <c>top</c>
    /// emits it. On SvcSystems.UI.Terminal 1.1.x that drew as plain text (ENG-19); since
    /// 2.0.0 it draws the inverted band, which this fixture showed by itself the day the
    /// package was updated. If a later version regresses it, this screenshot is where it
    /// shows (docs/engineering/exec-terminal.md).
    /// </para>
    /// </summary>
    public static ClusterTabViewModel ExecFullScreen() => BuildExec(
        "\u001b[2J\u001b[H"
        + "top - 14:02:11 up 3 days,  4:17,  load average: 0.32, 0.28, 0.24\r\n"
        + "Tasks:   4 total,   1 running,   3 sleeping\r\n"
        + "%Cpu(s):  \u001b[1;32m 6.2\u001b[0m us,  \u001b[1;33m 1.4\u001b[0m sy, "
        + "\u001b[1;36m92.4\u001b[0m id\r\n"
        + "MiB Mem :  \u001b[1m2048.0\u001b[0m total,  \u001b[1m 512.4\u001b[0m free,  "
        + "\u001b[1m1024.8\u001b[0m used\r\n"
        + "\r\n"
        + "\u001b[7m  PID USER      PR  NI    VIRT    RES  S  %CPU  %MEM     TIME+ COMMAND"
        + new string(' ', 40) + "\u001b[0m\r\n"
        + "    1 root      20   0  712540  48120  S   6.0   2.3   0:03.44 report-generator\r\n"
        + "   42 root      20   0    1652    964  S   0.0   0.1   0:00.02 sh\r\n"
        + "   57 root      20   0    2216   1104  R   0.3   0.1   0:00.01 top\r\n");

    /// <summary>
    /// The same full-screen tool with the dock maximized — the README gallery's cell,
    /// for the same reason <see cref="YamlEditorMaximized"/> is: a gallery image is
    /// rendered at half the table's width, and a ~300px dock inside a 1280px window
    /// shrinks to a band nobody can read. The process table is longer than
    /// <see cref="ExecFullScreen"/>'s because a maximized <c>top</c> that filled three
    /// rows of a forty-row screen would misrepresent the pane rather than flatter it.
    /// </summary>
    public static ClusterTabViewModel ExecFullScreenMaximized()
    {
        string[] processes =
        [
            "    1 root      20   0  712540  48120  S   6.0   2.3   0:03.44 report-generator",
            "   14 root      20   0  198432  22104  S   2.1   1.0   0:01.09 access-log-tailer",
            "   28 root      20   0  104880  11960  S   0.7   0.5   0:00.51 metrics-sidecar",
            "   42 root      20   0    1652    964  S   0.0   0.1   0:00.02 sh",
            "   57 root      20   0    2216   1104  R   0.3   0.1   0:00.01 top",
            "   63 root      20   0   88104   9240  S   0.2   0.4   0:00.18 tenant-sync",
            "   71 root      20   0   45012   5388  S   0.1   0.2   0:00.07 config-watch",
            "   88 root      20   0   32760   4120  S   0.0   0.2   0:00.03 healthz",
        ];

        var tab = BuildExec(
            "\u001b[2J\u001b[H"
            + "top - 14:02:11 up 3 days,  4:17,  load average: 0.32, 0.28, 0.24\r\n"
            + "Tasks:   8 total,   1 running,   7 sleeping,   0 stopped,   0 zombie\r\n"
            + "%Cpu(s):  \u001b[1;32m 9.4\u001b[0m us,  \u001b[1;33m 1.4\u001b[0m sy, "
            + "\u001b[1;36m89.2\u001b[0m id\r\n"
            + "MiB Mem :  \u001b[1m2048.0\u001b[0m total,  \u001b[1m 512.4\u001b[0m free,  "
            + "\u001b[1m1024.8\u001b[0m used,  \u001b[1m 510.8\u001b[0m buff/cache\r\n"
            + "MiB Swap:  \u001b[1m   0.0\u001b[0m total,  \u001b[1m   0.0\u001b[0m free,  "
            + "\u001b[1m   0.0\u001b[0m used.  \u001b[1m 892.1\u001b[0m avail Mem\r\n"
            + "\r\n"
            + "\u001b[7m  PID USER      PR  NI    VIRT    RES  S  %CPU  %MEM     TIME+ COMMAND"
            + new string(' ', 40) + "\u001b[0m\r\n"
            + string.Join("\r\n", processes) + "\r\n");

        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>
    /// The offline client's exec attempts all fail at the socket. Waited out first, because
    /// their continuations land on the same RunJobs() the capture pumps and a status set
    /// before they finish is overwritten by "Unable to connect to the remote server".
    /// </summary>
    private static void SettleExec(ExecTabViewModel exec)
    {
        for (var i = 0; i < 300 && exec.StatusMessage?.StartsWith("Could not exec", StringComparison.Ordinal) != true; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>The runtime's sentence for a shell the image does not have, as runc prints it.</summary>
    private static string NoSuchShell(string shell) =>
        "Internal error occurred: error executing command in container: failed to exec in container: "
        + "OCI runtime exec failed: exec failed: unable to start container process: "
        + $"exec: \"{shell}\": stat {shell}: no such file or directory: unknown";

    /// <summary>
    /// A distroless image: every shell refused as missing, so the pane says so and offers
    /// a debug container in place (UI rule 9 — a blank terminal is a state). The offline
    /// client never reaches a runtime, so the refusals are the runtime's real sentences,
    /// handed to the same <c>DescribeFailure</c> a live connect uses.
    /// </summary>
    public static ClusterTabViewModel ExecNoShell()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        var exec = new ExecTabViewModel(FixtureData.CreateOfflineClient(), "payments", row.Name, "app") { IsPreview = false };
        SettleExec(exec);
        exec.PresentExecFailures(PodOperatingSystem.Linux, [.. ExecShells.LinuxShells.Select(NoSuchShell)]);

        tab.InspectorTabs.Add(exec);
        tab.SelectedInspectorTab = exec;
        return tab;
    }

    /// <summary>
    /// The same pod after "Start debug container": a BusyBox shell in an ephemeral
    /// container that shares the app's processes, its files reached through /proc/1/root.
    /// </summary>
    public static ClusterTabViewModel ExecDebugContainer()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        var exec = new ExecTabViewModel(FixtureData.CreateOfflineClient(), "payments", row.Name, "app") { IsPreview = false };
        SettleExec(exec);
        exec.PresentExecFailures(PodOperatingSystem.Linux, [.. ExecShells.LinuxShells.Select(NoSuchShell)]);

        exec.DebugContainerName = "debugger-x7k2p";
        exec.Title = $"Debug: {row.Name}/debugger-x7k2p";
        exec.IsConnected = true;
        exec.StatusMessage = "Connected to debug container debugger-x7k2p (/bin/sh). It shares app's processes; "
            + "app's files are under /proc/1/root";
        exec.Feed(string.Join(
            "\r\n",
            "/ # ps",
            "PID   USER     TIME  COMMAND",
            "    1 1654      0:42 dotnet PaymentService.ReportGenerator.dll",
            "   38 root      0:00 sh",
            "   45 root      0:00 /bin/sh",
            "   51 root      0:00 ps",
            "/ # ls /proc/1/root/app | head -5",
            "PaymentService.ReportGenerator.dll",
            "PaymentService.ReportGenerator.deps.json",
            "PaymentService.ReportGenerator.runtimeconfig.json",
            "appsettings.json",
            "appsettings.Production.json",
            "/ # wget -qO- localhost:8080/healthz",
            "Healthy",
            "/ # "));

        tab.InspectorTabs.Add(exec);
        tab.SelectedInspectorTab = exec;
        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>
    /// A three-line paste into BusyBox <c>sh</c>, which never turns on bracketed paste: the
    /// pane asks before sending, over the top of the terminal, with the prompt the lines would
    /// land at still in view below.
    /// </summary>
    public static ClusterTabViewModel ExecPasteArmed()
    {
        var tab = Exec();
        var exec = (ExecTabViewModel)tab.SelectedInspectorTab!;
        exec.Paste("cd /tmp\nrm -rf report-cache\n./run.sh --once\n");
        return tab;
    }

    private static ClusterTabViewModel BuildExec(string output)
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        var client = FixtureData.CreateOfflineClient();
        var exec = new ExecTabViewModel(client, "payments", row.Name, "app") { IsPreview = false };

        // Drain the offline client's failed shell attempts first — the same trap
        // HelmReleaseDetail documents, and the reason this pane's screenshot used to
        // caption a working session with a connect failure. The failures then stand in
        // for a Linux pod's, so the shell box reads what it reads on one.
        SettleExec(exec);
        exec.PresentExecFailures(PodOperatingSystem.Linux, []);

        exec.IsConnected = true;
        exec.StatusMessage = "Connected to app (/bin/sh)";
        exec.Feed(output);

        tab.InspectorTabs.Add(exec);
        tab.SelectedInspectorTab = exec;
        return tab;
    }

    /// <summary>
    /// The same pane before anything is forwarded — the state the tab actually opens
    /// in, and the one that used to render as a row of controls with no statement of
    /// what they would do (UI rule 9).
    /// </summary>
    public static ClusterTabViewModel PortForwardIdle()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        var pf = new PortForwardTabViewModel(
            FixtureData.CreateOfflineClient(), "payments", row.Name,
            [new ContainerPort(8080, "http"), new ContainerPort(9090, "metrics")]) { IsPreview = false };

        tab.InspectorTabs.Add(pf);
        tab.SelectedInspectorTab = pf;
        return tab;
    }

    public static ClusterTabViewModel PortForward()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        var client = FixtureData.CreateOfflineClient();
        // Two named declared ports, so the picker renders what a real pod spec gives it
        // ("8080 · http") rather than a bare number.
        var pf = new PortForwardTabViewModel(
            client, "payments", row.Name,
            [new ContainerPort(8080, "http"), new ContainerPort(9090, "metrics")]) { IsPreview = false };
        pf.LocalPort = 54321;
        // In the app every forward pane has the window's registry, which is what makes
        // closing its tab keep it running — and the pane says so while it runs (FEAT-7).
        pf.Registry = new PortForwardRegistry();
        pf.IsRunning = true;
        // Running: the status bar carries the local URL itself, so StatusMessage is
        // empty — matching what StartAsync leaves behind.
        pf.StatusMessage = null;

        tab.InspectorTabs.Add(pf);
        tab.SelectedInspectorTab = pf;
        return tab;
    }

    /// <summary>
    /// FEAT-29: a forward through a Service, naming the pod it reaches — after a connection
    /// to the first pod failed and it moved to the other one, which it says.
    /// </summary>
    public static ClusterTabViewModel PortForwardService()
    {
        var tab = BaseTab();
        var pf = PortForwardTabViewModel.ForService(
            FixtureData.CreateOfflineClient(), "payments", "checkout",
            [new ServicePortInfo("http", 80, "http", 0, "TCP", ""), new ServicePortInfo("metrics", 9100, "9090", 0, "TCP", "")]);
        pf.IsPreview = false;
        pf.LocalPort = 54400;
        pf.Registry = new PortForwardRegistry();
        pf.IsRunning = true;
        pf.StatusMessage = null;
        pf.ResolvedPod = "checkout-worker-7b4c6d8f5-h2x7d:8080";
        pf.TargetNotice =
            "Moved to pod checkout-worker-7b4c6d8f5-h2x7d: a connection to checkout-worker-5d8f7b9c4-qz9pl failed, "
            + "and it is no longer a ready endpoint of the service.";

        tab.InspectorTabs.Add(pf);
        tab.SelectedInspectorTab = pf;
        return tab;
    }

    /// <summary>
    /// FEAT-7: the window's forwards, listed — one healthy, one whose last connection the
    /// kubelet refused, one through a Service — with the status bar counting them. The
    /// forwards are put in the shell's own registry by <paramref name="registry"/>, which
    /// is the one the status bar reads; the pod forward's own tab has been closed.
    /// </summary>
    public static void PortForwardsList(ClusterTabViewModel tab, PortForwardRegistry registry)
    {
        PortForwardTabViewModel Running(PortForwardTabViewModel pf, int port, string cluster)
        {
            pf.Owner = tab;
            pf.Registry = registry;
            pf.ClusterLabel = cluster;
            pf.LocalPort = port;
            pf.IsRunning = true;
            pf.StatusMessage = null;
            registry.Add(pf);
            return pf;
        }

        Running(new PortForwardTabViewModel(FixtureData.CreateOfflineClient(), "payments", "payment-api-6d9f8b7c5-x2k4q",
            [new ContainerPort(8080, "http")]), 54321, "prod-payments");
        var refused = Running(new PortForwardTabViewModel(FixtureData.CreateOfflineClient(), "payments", "ledger-api-5c7d9f8b6-m3n8p",
            [new ContainerPort(9090, "metrics")]), 9090, "prod-payments");
        refused.ConnectionError = "error forwarding port 9090 to pod ledger-api-5c7d9f8b6-m3n8p: connection refused";
        var service = Running(PortForwardTabViewModel.ForService(FixtureData.CreateOfflineClient(), "payments", "checkout",
            [new ServicePortInfo("http", 80, "http", 0, "TCP", "")]), 54400, "prod-payments");
        service.ResolvedPod = "checkout-worker-7b4c6d8f5-h2x7d:8080";

        tab.OpenPortForwardsCommand.Execute(null);
    }

    // ------------------------------------------------------------ L1: palette log rows
    //
    // The answers a real listing produces, written in for the states a sandbox will not
    // produce on demand. Built from the shipped demo objects so the rows read as the
    // demo cluster's own.

    private static IEnumerable<LogTarget> DemoLogTargets(string clusterName = "") =>
        DemoData.Deployments.Select(d => new LogTarget(d, DeploymentDescriptor, clusterName, null))
            .Concat(DemoData.Pods
                .Where(p => p.Namespace == "payments")
                .Select(p => new LogTarget(p, ResourceDescriptor.Pods, clusterName, null)));

    /// <summary>The last look at this namespace, shown while the next is in flight.</summary>
    public static LogTargetList StaleLogTargets() => new([.. DemoLogTargets().Take(4)], [], []);

    /// <summary>
    /// A user who may list Deployments here but not pods — the RBAC shape the note exists
    /// for: the rows that could be listed, and the refusal in the server's own words.
    /// </summary>
    public static LogTargetList RefusedLogTargets() => new(
        [.. DemoData.Deployments.Select(d => new LogTarget(d, DeploymentDescriptor, "", null))],
        [new LogTargetProblem(
            "not allowed to list pods in payments",
            "pods is forbidden: User \"dev@acme.io\" cannot list resource \"pods\" in API group \"\" in the namespace \"payments\"",
            IsForbidden: true)],
        []);

    public static LogTargetList CappedLogTargets() => new(
        [.. DemoLogTargets()],
        [],
        ["only the first 2,000 pods in every namespace are listed"]);

    /// <summary>Two clusters with the same objects — every row has to say which one it is.</summary>
    public static LogTargetList FleetLogTargets() => new(
        [.. DemoLogTargets("prod-payments").Take(5), .. DemoLogTargets("staging-eu").Take(5)],
        [new LogTargetProblem("qa-integration: couldn't list pods in payments", "Connection refused (10.4.0.12:6443)", IsForbidden: false)],
        []);

    /// <summary>A tab whose connection is gone, so the palette has nothing to list from.</summary>
    public static ClusterTabViewModel NotConnected()
    {
        var tab = BaseTab();
        tab.IsConnected = false;
        tab.Status = "Not connected.";
        return tab;
    }

    /// <summary>
    /// FEAT-17: the exec pane after "Open this session in your terminal" opened one — the
    /// notice laid over the top of the terminal, in the app's own words
    /// (<see cref="TerminalHandoff.Describe"/>). Set from a result rather than launched: the
    /// harness must never start a terminal.
    /// </summary>
    public static ClusterTabViewModel ExecHandoff()
    {
        var tab = Exec();
        var exec = (ExecTabViewModel)tab.SelectedInspectorTab!;
        var command = exec.BuildHandoffCommand();
        var (message, warning, error) = TerminalHandoff.Describe(new TerminalLaunchResult(
            TerminalLaunchOutcome.Opened, "PowerShell 7", @"C:\Program Files\kubectl\kubectl.exe",
            @"C:\Users\dev\AppData\Roaming\kubeNimbus\terminal\context-4f21ab90c7d3.kubeconfig;C:\Users\dev\.kube\config",
            tab.Context.Name, ["PowerShell 7"], null)
        {
            Command = command.Summary,
        });
        exec.HandoffNotice = message;
        exec.HandoffNoticeIsWarning = warning;
        exec.HandoffNoticeIsError = error;
        return tab;
    }

    /// <summary>
    /// FEAT-27: node detail's node shell when no kubectl could be found — the hand-off is a
    /// kubectl command, so nothing opens and the InfoBar under the chrome row says why.
    /// </summary>
    public static ClusterTabViewModel NodeShellNoKubectl()
    {
        var tab = OpenNode("demo-worker-1", tabIndex: NodeDetailTabViewModel.OverviewTabIndex);
        var detail = (NodeDetailTabViewModel)tab.SelectedInspectorTab!;
        var (message, warning, error) = TerminalHandoff.Describe(new TerminalLaunchResult(
            TerminalLaunchOutcome.NoKubectl, null, null, "", tab.Context.Name, [], null)
        {
            Command = TerminalHandoff.NodeShellCommand(detail.NodeName).Summary,
        });
        detail.NodeShellNotice = message;
        detail.NodeShellNoticeIsWarning = warning;
        detail.NodeShellNoticeIsError = error;
        return tab;
    }

    /// <summary>
    /// ENG-4 / ENG-54: a demo tab with two pods open in the dock, the first in front, for the
    /// keyboard walk (KeyboardChecks.KeyboardWalk). Demo rather than fixture so that choosing a
    /// kind from the keyboard repopulates the list from the dataset instead of starting a watch
    /// against the offline client.
    /// </summary>
    public static ClusterTabViewModel DemoDockTabs()
    {
        var tab = DemoTab();
        var pods = tab.Rows.Where(r => r.Name.StartsWith("payment-service", StringComparison.Ordinal)).Take(2).ToList();
        foreach (var pod in pods)
        {
            tab.SelectedRow = pod;
            tab.OpenSelectedCommand.Execute(null);
        }

        tab.SelectedRow = pods[0];
        tab.SelectedInspectorTab = tab.InspectorTabs[0];
        DrainDemoLogs(tab);
        return tab;
    }
}
