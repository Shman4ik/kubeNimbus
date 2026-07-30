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
    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", true, [], []);

    private static ClusterTabViewModel BaseTab(bool populateRows = true)
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
            // stand in for one poll's samples — otherwise the CPU/Memory columns
            // never appear in a screenshot. Deterministic per row, not random,
            // so screenshots stay diffable.
            tab.AreMetricsVisible = true;
            for (var i = 0; i < tab.Rows.Count; i++)
            {
                tab.Rows[i].ApplyUsage(
                    cpuNanocores: (3 + i * 17) * 1_000_000L,
                    memoryBytes: (48 + i * 37) * 1024L * 1024L);
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

    public static ClusterTabViewModel WorkloadsList() => BaseTab();

    public static ClusterTabViewModel SidebarFiltered()
    {
        var tab = BaseTab();
        tab.SidebarFilter = "route";
        return tab;
    }

    public static ClusterTabViewModel SidebarCrdsExpanded()
    {
        var tab = BaseTab();
        var crds = tab.SidebarSections.First(s => s.Title == "CRDs");
        crds.IsExpanded = true;
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

    public static ClusterTabViewModel PodDetail()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        tab.SelectedRow = row;

        var client = FixtureData.CreateOfflineClient();
        var detail = new PodDetailTabViewModel(client, row, _ => { }, (_, _) => Task.CompletedTask) { IsPreview = false };

        detail.LogLines.Add("2026-07-20T08:41:02.114Z INFO  starting report-generator v2.14.3");
        detail.LogLines.Add("2026-07-20T08:41:02.331Z INFO  connected to postgres primary (payments-db.internal:5432)");
        detail.LogLines.Add("2026-07-20T08:41:02.402Z INFO  listening on :8080");
        detail.LogLines.Add("2026-07-20T08:44:17.008Z INFO  generated monthly-settlement report for merchant=acme-retail (842ms)");
        detail.LogLines.Add("2026-07-20T08:44:55.771Z WARN  slow query detected: SELECT * FROM settlements WHERE ... (1204ms)");
        detail.LogLines.Add("2026-07-20T08:45:01.220Z INFO  generated chargeback-summary report for merchant=north-store (391ms)");
        detail.IsFollowingLogs = true;

        // Same reasoning as the list rows: no live metrics API behind the fixture,
        // so the per-container usage line gets a stand-in sample.
        for (var i = 0; i < detail.Containers.Count; i++)
        {
            detail.Containers[i].ApplyUsage((11 + i * 23) * 1_000_000L, (64 + i * 55) * 1024L * 1024L);
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

    public static ClusterTabViewModel YamlEditorConflict()
    {
        var tab = YamlEditor();
        if (tab.SelectedInspectorTab is YamlEditorTabViewModel yaml)
        {
            yaml.ConflictDetails = "Field .spec.replicas is owned by field manager \"kubectl-scale\" (apply conflicts with your changes).";
        }

        return tab;
    }

    public static ClusterTabViewModel Exec()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        var client = FixtureData.CreateOfflineClient();
        var exec = new ExecTabViewModel(client, "payments", row.Name, "app") { IsPreview = false };

        exec.OutputText =
            "/ # ps aux\n" +
            "PID   USER     TIME  COMMAND\n" +
            "    1 root      0:03 report-generator --port=8080\n" +
            "   42 root      0:00 /bin/sh\n" +
            "/ # curl -s localhost:8080/healthz\n" +
            "{\"status\":\"ok\",\"uptime\":\"3h12m\"}\n" +
            "/ # ";
        exec.IsConnected = true;

        tab.InspectorTabs.Add(exec);
        tab.SelectedInspectorTab = exec;
        return tab;
    }

    public static ClusterTabViewModel PortForward()
    {
        var tab = BaseTab();
        var row = tab.Rows.First(r => r.Name.StartsWith("payment-service-report-generator", StringComparison.Ordinal));
        var client = FixtureData.CreateOfflineClient();
        var pf = new PortForwardTabViewModel(client, "payments", row.Name, 8080) { IsPreview = false };
        pf.LocalPort = 54321;
        pf.IsRunning = true;
        pf.StatusMessage = "Forwarding 127.0.0.1:54321 → payment-service-report-generator-7f9c8d6bcd-x7k2m:8080";

        tab.InspectorTabs.Add(pf);
        tab.SelectedInspectorTab = pf;
        return tab;
    }
}
