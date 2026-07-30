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

    private static readonly ResourceDescriptor SecretDescriptor =
        new("", "v1", "Secret", "secrets", "secret", true, [], []);

    /// <summary>
    /// How many poll ticks of fake history the fixtures build. 24 ticks at the app's
    /// real 15s cadence is six minutes — enough for a sparkline to actually have a
    /// shape, which a single stand-in sample never does.
    /// </summary>
    private const int FixtureSampleCount = 24;

    private static readonly TimeSpan FixturePollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Fixed "now" so every screenshot's time axis (and its captions) is diffable.</summary>
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

    public static ClusterTabViewModel WorkloadsList() => BaseTab();

    /// <summary>Same pod list, with the CPU/Mem column populated — demonstrates the metrics.k8s.io-present path.</summary>
    public static ClusterTabViewModel WorkloadsListWithMetrics()
    {
        var tab = BaseTab(seedUsage: false);
        ApplyMetrics(tab);
        return tab;
    }

    private static void ApplyMetrics(ClusterTabViewModel tab)
    {
        tab.AreMetricsVisible = true;
        var byKey = FixtureData.PodMetrics.ToDictionary(m => m.Key, StringComparer.Ordinal);
        for (var i = 0; i < tab.Rows.Count; i++)
        {
            var row = tab.Rows[i];
            if (byKey.TryGetValue(row.Key, out var m))
            {
                var (cpu, memory) = SumContainerUsage(m);
                SeedUsage(row, i, cpu, memory);
            }
            else
            {
                // A pod with no entry in the metrics response — the "—" column state,
                // and a gap-only (so empty) sparkline.
                for (var tick = 0; tick < FixtureSampleCount; tick++)
                {
                    row.ClearUsage(TickAt(tick));
                }
            }
        }
    }

    /// <summary>
    /// Replays <see cref="FixtureSampleCount"/> polls into a row's usage history,
    /// landing exactly on <paramref name="cpu"/>/<paramref name="memory"/> on the
    /// last tick — so the sparkline has a shape while the CPU/Memory text still
    /// matches the fixture's own numbers.
    /// </summary>
    private static void SeedUsage(ResourceRowViewModel row, int seed, long? cpu, long? memory)
    {
        for (var tick = 0; tick < FixtureSampleCount; tick++)
        {
            var final = tick == FixtureSampleCount - 1;
            row.ApplyUsage(Ripple(cpu, seed, tick, final), Ripple(memory, seed + 5, tick, final), TickAt(tick));
        }
    }

    private static DateTimeOffset TickAt(int tick) =>
        FixtureNow - FixturePollInterval * (FixtureSampleCount - 1 - tick);

    /// <summary>
    /// A deterministic 0.5–1.1× wobble around the fixture's reading. Two sines of
    /// different periods, so the series reads like a real workload rather than a
    /// textbook sine wave — and identically on every run, which is what keeps the
    /// screenshots diffable.
    /// </summary>
    private static long? Ripple(long? value, int seed, int tick, bool final)
    {
        if (value is not { } v)
        {
            return null;
        }

        if (final)
        {
            return v;
        }

        var factor = 0.8 + 0.22 * Math.Sin((tick + seed * 3) * 0.55) + 0.08 * Math.Cos((tick + seed) * 1.31);
        return (long)(v * Math.Clamp(factor, 0.35, 1.25));
    }

    private static (long? Cpu, long? Memory) SumContainerUsage(DynamicResource podMetrics)
    {
        if (!podMetrics.Raw.TryGetProperty("containers", out var containers) || containers.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return (null, null);
        }

        long cpu = 0, memory = 0;
        var any = false;
        foreach (var c in containers.EnumerateArray())
        {
            if (!c.TryGetProperty("usage", out var usage) || usage.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            if (usage.TryGetProperty("cpu", out var cpuEl) && Quantity.ParseCpuNanocores(cpuEl.GetString()) is { } c1)
            {
                cpu += c1;
                any = true;
            }

            if (usage.TryGetProperty("memory", out var memEl) && Quantity.ParseBytes(memEl.GetString()) is { } m1)
            {
                memory += m1;
                any = true;
            }
        }

        return any ? (cpu, memory) : (null, null);
    }

    /// <summary>Namespace/cluster-wide Events browsing — selecting the Events kind in the sidebar
    /// (Config section, distinct bell icon) shows the same generic list, with Type-driven color coding.</summary>
    public static ClusterTabViewModel EventsList()
    {
        var tab = BaseTab(populateRows: false);
        var eventsKind = tab.SidebarSections.First(s => s.Title == "Config").Kinds.First(k => k.Descriptor.Kind == "Event");
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

    /// <summary>Pod detail's Environment tab — literal values, unresolved Secret/ConfigMap refs, and
    /// one already-revealed value (demoing what RevealEnvVarCommand's result looks like, without a network call).</summary>
    public static ClusterTabViewModel PodDetailEnvironment()
    {
        var tab = PodDetail();
        if (tab.SelectedInspectorTab is PodDetailTabViewModel detail)
        {
            detail.SelectedDetailTabIndex = 1;
            var revealed = detail.EnvironmentVars.FirstOrDefault(v => v.Name == "DB_USERNAME");
            if (revealed is not null)
            {
                revealed.RevealedValue = "payments_svc";
            }
        }

        return tab;
    }

    /// <summary>
    /// Replays fixture polls into a pod detail tab through its real
    /// <see cref="PodDetailTabViewModel.ApplyMetrics"/>, so the per-container chips,
    /// the whole-pod charts and the window caption all come out of production code.
    /// </summary>
    private static void SeedPodUsage(PodDetailTabViewModel detail)
    {
        var bases = detail.Containers
            .Select((c, i) => (c.Name, Cpu: (11 + i * 23) * 1_000_000L, Memory: (64 + i * 55) * 1024L * 1024L))
            .ToArray();

        for (var tick = 0; tick < FixtureSampleCount; tick++)
        {
            var final = tick == FixtureSampleCount - 1;
            var containers = new List<ContainerMetrics>(bases.Length);
            for (var i = 0; i < bases.Length; i++)
            {
                containers.Add(new ContainerMetrics(
                    bases[i].Name,
                    Ripple(bases[i].Cpu, i, tick, final),
                    Ripple(bases[i].Memory, i + 5, tick, final)));
            }

            detail.ApplyMetrics(new PodMetrics(detail.PodNamespace, detail.PodName, containers), TickAt(tick));
        }
    }

    /// <summary>Pod detail's Usage tab — CPU/memory over the session's poll window, pod total plus per container.</summary>
    public static ClusterTabViewModel PodDetailUsage()
    {
        var tab = PodDetail();
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
