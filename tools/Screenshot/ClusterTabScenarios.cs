using Avalonia.Threading;
using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

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
        var tab = new ClusterTabViewModel(context)
        {
            IsConnected = true,
            Status = "Connected — Kubernetes v1.31.2.",
        };

        var catalog = FixtureData.BuildCatalog();
        foreach (var section in FixtureData.BuildSidebarSections(catalog))
        {
            tab.SidebarSections.Add(section);
        }

        tab.NamespaceOptions.Add(ClusterTabViewModel.AllNamespaces);
        foreach (var ns in FixtureData.Namespaces)
        {
            tab.NamespaceOptions.Add(ns);
        }

        tab.SelectedNamespace = "payments";

        var podKind = tab.SidebarSections
            .First(s => s.Title == "Workloads").Kinds
            .First(k => k.Descriptor.Kind == "Pod");
        podKind.IsSelected = true;
        tab.SelectedKind = podKind;

        if (populateRows)
        {
            foreach (var pod in FixtureData.Pods)
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
    /// The default layout: advanced view off, which is what a fresh install opens on.
    /// The usage data is seeded all the same — the columns are hidden by the switch,
    /// not absent — so this and <see cref="AdvancedView"/> are a true before/after.
    /// </summary>
    public static ClusterTabViewModel WorkloadsList() => BaseTab();

    /// <summary>
    /// The same tab with the advanced view on: usage columns and their sparklines
    /// back, sidebar kind-count badges back. Turning it on must restore today's
    /// surface exactly — it is a hide/show switch, not a second layout.
    /// </summary>
    public static ClusterTabViewModel AdvancedView() => Advanced(BaseTab());

    /// <summary>
    /// Flips a fixture tab into the advanced view. Set *after* the sections and rows
    /// are in place: the real <c>OnIsAdvancedViewChanged</c> is what pushes the
    /// count badges onto the sections, so a tab that was already advanced before it
    /// had any sections would render without them.
    /// </summary>
    private static ClusterTabViewModel Advanced(ClusterTabViewModel tab)
    {
        tab.IsAdvancedView = true;
        return tab;
    }

    /// <summary>Same pod list, with the CPU/Mem column populated — demonstrates the metrics.k8s.io-present path.
    /// Advanced, because that is the only place those columns exist.</summary>
    public static ClusterTabViewModel WorkloadsListWithMetrics()
    {
        var tab = BaseTab(seedUsage: false);
        ApplyMetrics(tab);
        return Advanced(tab);
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
    /// </summary>
    public static ClusterTabViewModel DemoCrdPrinterColumns() => SelectDemoCertificates(DemoTab());

    /// <summary>
    /// The same list with the advanced view on, which is this app's <c>-o wide</c>: the
    /// CRD's two <c>priority: 1</c> columns (ISSUER and STATUS) join it, and leave again
    /// when the switch goes off. A true before/after with the scenario above — same tab,
    /// same objects, one switch.
    /// </summary>
    public static ClusterTabViewModel DemoCrdPrinterColumnsWide() => Advanced(SelectDemoCertificates(DemoTab()));

    private static ClusterTabViewModel SelectDemoCertificates(ClusterTabViewModel tab)
    {
        tab.SelectedNamespace = ClusterTabViewModel.AllNamespaces;
        var kind = tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "cert-manager.io", Kind: "Certificate" });
        tab.SelectKindCommand.Execute(kind);
        return tab;
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

    /// <summary>Namespace/cluster-wide Events browsing — selecting the Events kind in the sidebar
    /// (Config section, distinct bell icon) shows the same generic list, with Type-driven color coding.</summary>
    public static ClusterTabViewModel EventsList()
    {
        var tab = BaseTab(populateRows: false);
        var config = tab.SidebarSections.First(s => s.Title == "Config");

        // Config now starts collapsed (it is no longer the catalog's junk drawer, but
        // it is still not what a session opens on), and this shot is about the row
        // that's selected in it.
        config.IsExpanded = true;

        var eventsKind = config.Kinds.First(k => k.Descriptor.Kind == "Event");
        eventsKind.IsSelected = true;
        tab.SelectedKind = eventsKind;

        foreach (var e in FixtureData.Events)
        {
            tab.Rows.Add(new ResourceRowViewModel(e));
        }

        tab.IsListEmpty = false;
        tab.IsListLoading = false;
        return tab;
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
            foreach (var pod in FixtureData.Pods)
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
        return Advanced(tab);
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
        foreach (var pod in FixtureData.Pods)
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
        kind.IsSelected = true;
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
    private static RowActionViewModel ArmRowAction(ClusterTabViewModel tab, RowActionKind kind)
    {
        var row = tab.SelectedRow!;
        var action = new RowActionViewModel(
            kind, FixtureData.CreateOfflineClient(), tab.SelectedKind!.Descriptor, row.Namespace, row.Name,
            replicas: WorkloadActions.DeclaredReplicas(row.Resource));

        tab.PendingRowAction = action;
        return action;
    }

    /// <summary>
    /// Scale, armed: the replica box, the current scale beside it, and the confirm. The
    /// scale subresource cannot be read from an offline client, so the reading it would
    /// have produced is written in — obviously-fake numbers, as with every other fixture.
    /// </summary>
    public static ClusterTabViewModel RowActionScale()
    {
        var tab = DeploymentsTab();
        var action = ArmRowAction(tab, RowActionKind.Scale);
        action.Replicas = 4;
        action.CurrentScale = "currently 2 set · 1 running";
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
    /// A search that matches nothing — a different state from an empty namespace, and
    /// the one the list had no visual for at all before the box existed.
    /// </summary>
    public static ClusterTabViewModel FilteredListEmpty()
    {
        var tab = BaseTab();
        tab.RowFilter = "nginx-ingress";
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
        return tab;
    }

    public static ClusterTabViewModel PodDetail(bool seedUsage = true)
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
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
            detail.LogLines.Add(new LogLineViewModel(raw, detail.ShowLogTimestamps));
        }

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
        foreach (var e in FixtureData.Events)
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

    /// <summary>Pod detail's Usage tab — CPU/memory over the session's poll window, pod total plus per container.
    /// Advanced, since the Usage tab only exists there.</summary>
    public static ClusterTabViewModel PodDetailUsage()
    {
        var tab = Advanced(PodDetail());
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
        var tab = Advanced(PodDetail(seedUsage: false));
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            detail.SelectedDetailTabIndex = 3;
            detail.IsMetricsUnavailable = true;
        }

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

        foreach (var kind in tab.SidebarSections.SelectMany(s => s.Kinds))
        {
            kind.IsSelected = ReferenceEquals(kind, helmKind);
        }

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
        return Advanced(tab);
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

    public static ClusterTabViewModel YamlEditorMaximized()
    {
        var tab = YamlEditor();
        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>A server-side apply conflict. Advanced, because force-apply — the only
    /// thing that resolves one from inside the app — is an advanced-view control.</summary>
    public static ClusterTabViewModel YamlEditorConflict()
    {
        var tab = Advanced(YamlEditor());
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
    /// The <c>ESC[7m</c> header renders <b>unhighlighted</b>, and that is not a mistake
    /// in the fixture: reverse video with default colours is a defect in the terminal
    /// control (CLAUDE.md, "The exec terminal"). Emitting what real <c>top</c> emits
    /// keeps the screenshot honest, and it will start drawing a band by itself the day
    /// that is fixed.
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
    /// The blank-terminal states, and the only one of them the harness can reach for
    /// real: the offline client's three shell attempts all fail, so this is the actual
    /// message <c>ConnectAsync</c> writes. A terminal with nothing in it is
    /// indistinguishable from a broken pane, which is why the status covers it until
    /// the first byte arrives (UI rule 9).
    /// </summary>
    public static ClusterTabViewModel ExecNoShell()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        var exec = new ExecTabViewModel(FixtureData.CreateOfflineClient(), "payments", row.Name, "app") { IsPreview = false };

        for (var i = 0; i < 100 && exec.StatusMessage?.StartsWith("No usable shell", StringComparison.Ordinal) != true; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        tab.InspectorTabs.Add(exec);
        tab.SelectedInspectorTab = exec;
        return tab;
    }

    private static ClusterTabViewModel BuildExec(string output)
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        var client = FixtureData.CreateOfflineClient();
        var exec = new ExecTabViewModel(client, "payments", row.Name, "app") { IsPreview = false };

        // Drain the offline client's three failed shell attempts first. Their
        // continuations land on the same RunJobs() the capture pumps, so a status set
        // before they finish is overwritten by "Unable to connect to the remote
        // server" — the same trap HelmReleaseDetail documents, and the reason this
        // pane's screenshot used to caption a working session with a connect failure.
        for (var i = 0; i < 100 && exec.StatusMessage?.StartsWith("No usable shell", StringComparison.Ordinal) != true; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

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
        pf.IsRunning = true;
        // Running: the status bar carries the local URL itself, so StatusMessage is
        // empty — matching what StartAsync leaves behind.
        pf.StatusMessage = null;

        tab.InspectorTabs.Add(pf);
        tab.SelectedInspectorTab = pf;
        return tab;
    }
}
