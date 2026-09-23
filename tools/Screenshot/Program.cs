using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using KubeNimbus.App;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using KubeNimbus.Core;
using KubeNimbus.Screenshot;

// Usage: dotnet run --project tools/Screenshot -- <outputDir> [scenario-substring]
// Renders every scenario in both light and dark, one PNG per (scenario, theme).
// This is the replacement for the local session's Avalonia DevTools MCP
// screenshot loop when developing in an environment with no display — see
// CLAUDE.md "Headless screenshot harness".

var outDir = args.Length > 0 ? args[0] : "screenshots";
var filter = args.Length > 1 ? args[1] : null;
Directory.CreateDirectory(outDir);

// Scenarios construct real MainWindowViewModels, which read the workspace on
// construction and save it whenever a cluster is pinned. Point that at a scratch
// directory so rendering fixtures can't read — or clobber — the developer's own
// open tabs, pins and theme.
WorkspaceStore.DirectoryOverride = Path.Combine(Path.GetTempPath(), "kubenimbus-screenshot-workspace");
Directory.CreateDirectory(WorkspaceStore.DirectoryOverride);
File.Delete(Path.Combine(WorkspaceStore.DirectoryOverride, "workspace.json"));

// The same redirect for settings.json, and for a stronger reason: the preferences a
// scenario touches (theme, advanced view, sidebar visibility) are exactly the ones the
// developer running the harness has chosen for themselves, and several scenarios set
// them by construction. Deleting the file first also pins every render to the shipped
// defaults, so a screenshot can never quietly depend on whatever was left behind by
// the previous run.
KubeNimbus.Core.Settings.AppSettingsStore.DirectoryOverride = WorkspaceStore.DirectoryOverride;
File.Delete(Path.Combine(WorkspaceStore.DirectoryOverride, "settings.json"));

BuildAvaloniaApp().SetupWithoutStarting();


var scenarios = new (string Name, Func<Control> Build)[]
{
    ("ux-namespace-picker", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("ux-unhealthy-toggle", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("ux-workload-pods", () => HostInMainWindow(ClusterTabScenarios.WorkloadDetail(), height: 1000)),
    ("ux-workload-conditions", () => HostInMainWindow(ClusterTabScenarios.WorkloadDetail(1), height: 1000)),
    ("ux-workload-events", () => HostInMainWindow(ClusterTabScenarios.WorkloadDetail(2), height: 1000)),
    ("cluster-tab-workloads-list", () => HostInMainWindow(ClusterTabScenarios.WorkloadsList())),
    // The before/after pair for the advanced view. Same tab, same seeded usage data —
    // the only difference is the switch, and what it may change is now exactly the
    // sidebar: the Cluster and CRDs sections go, and the list, its usage columns and
    // every other content-area control stay where they were.
    ("cluster-tab-basic-sidebar", () => HostInMainWindow(ClusterTabScenarios.BasicSidebar())),
    ("cluster-tab-workloads-list-metrics", () => HostInMainWindow(ClusterTabScenarios.WorkloadsListWithMetrics())),
    // The Events list as `kubectl get events` prints it (Last seen, Type, Reason,
    // Object, Count, Message; newest first), its empty state (events expire), a search
    // that matches a message rather than a name, the states the demo dataset cannot hold
    // (fleet, events.k8s.io series, no object, no timestamp, a multi-line message), and
    // the whole thing at a narrow window, where the prose columns run out first.
    ("cluster-tab-events-list", () => HostInMainWindow(ClusterTabScenarios.EventsList())),
    ("cluster-tab-events-empty", () => HostInMainWindow(ClusterTabScenarios.EventsListEmpty())),
    ("cluster-tab-events-search", () => HostInMainWindow(ClusterTabScenarios.EventsList(filter: "probe"))),
    ("cluster-tab-events-edge-cases", () => HostInMainWindow(ClusterTabScenarios.EventsListEdgeCases())),
    ("cluster-tab-events-narrow", () => HostInMainWindow(ClusterTabScenarios.EventsList(), width: 1024)),
    ("cluster-tab-fleet-list", () => HostInMainWindow(ClusterTabScenarios.FleetList(), height: 1000)),
    ("cluster-tab-fleet-list-partial", () => HostInMainWindow(ClusterTabScenarios.FleetListPartial(), height: 1000)),
    ("cluster-tab-sidebar-filtered", () => HostInMainWindow(ClusterTabScenarios.SidebarFiltered())),
    ("cluster-tab-sidebar-filtered-by-group", () => HostInMainWindow(ClusterTabScenarios.SidebarFilteredByGroup())),
    ("cluster-tab-sidebar-recent", () => HostInMainWindow(ClusterTabScenarios.SidebarRecentKinds())),
    ("cluster-tab-sidebar-crds-expanded", () => HostInMainWindow(ClusterTabScenarios.SidebarCrdsExpanded(), height: 1500)),
    // Taller than the default: at 800 the dock's log pane is clipped by the
    // window edge, which is the one thing this scenario exists to show.
    ("cluster-tab-pod-detail", () => HostInMainWindow(ClusterTabScenarios.PodDetail(), height: 1000)),
    ("cluster-tab-pod-detail-environment", () => HostInMainWindow(ClusterTabScenarios.PodDetailEnvironment())),
    ("cluster-tab-pod-detail-events", () => HostInMainWindow(ClusterTabScenarios.PodDetailEvents())),
    ("cluster-tab-pod-detail-overview", () => HostInMainWindow(ClusterTabScenarios.PodDetailOverview(), height: 1000)),
    ("cluster-tab-pod-detail-overview-unschedulable",
        () => HostInMainWindow(ClusterTabScenarios.PodDetailOverviewUnschedulable(), height: 1000)),
    // The inverted-polarity condition (DisruptionTarget) and the unclassified one, both
    // of which had no object anywhere in the repo to render them — see the scenario.
    ("cluster-tab-pod-detail-overview-disrupted",
        () => HostInMainWindow(ClusterTabScenarios.PodDetailOverviewDisrupted(), height: 1000)),
    ("cluster-tab-pod-detail-usage", () => HostInMainWindow(ClusterTabScenarios.PodDetailUsage(), height: 1000)),
    ("cluster-tab-pod-detail-usage-unavailable", () => HostInMainWindow(ClusterTabScenarios.PodDetailUsageUnavailable())),
    ("cluster-tab-pod-detail-usage-unset", () => HostInMainWindow(ClusterTabScenarios.PodDetailUsageUnset())),
    ("cluster-tab-yaml-editor", () => HostInMainWindow(ClusterTabScenarios.YamlEditor())),
    ("cluster-tab-yaml-editor-maximized", () => HostInMainWindow(ClusterTabScenarios.YamlEditorMaximized())),
    ("cluster-tab-yaml-diff-preview", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffPreview())),
    ("cluster-tab-yaml-diff-split", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffSplit())),
    ("cluster-tab-yaml-diff-fields", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffFields())),
    ("cluster-tab-yaml-diff-no-change", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffNoChange())),
    ("cluster-tab-yaml-validation-rejected", () => HostInMainWindow(ClusterTabScenarios.YamlEditorValidationRejected())),
    ("cluster-tab-yaml-conflict", () => HostInMainWindow(ClusterTabScenarios.YamlEditorConflict())),
    ("cluster-tab-yaml-secret-masked", () => HostInMainWindow(ClusterTabScenarios.YamlEditorSecretMasked())),
    ("cluster-tab-yaml-secret-revealed", () => HostInMainWindow(ClusterTabScenarios.YamlEditorSecretRevealed())),
    ("cluster-tab-exec", () => HostInMainWindow(ClusterTabScenarios.Exec())),
    ("cluster-tab-exec-fullscreen", () => HostInMainWindow(ClusterTabScenarios.ExecFullScreen())),
    ("cluster-tab-exec-fullscreen-maximized", () => HostInMainWindow(ClusterTabScenarios.ExecFullScreenMaximized())),
    ("cluster-tab-exec-no-shell", () => HostInMainWindow(ClusterTabScenarios.ExecNoShell())),
    ("cluster-tab-port-forward", () => HostInMainWindow(ClusterTabScenarios.PortForward())),
    ("cluster-tab-port-forward-idle", () => HostInMainWindow(ClusterTabScenarios.PortForwardIdle())),
    ("cluster-tab-helm-releases", () => HostInMainWindow(ClusterTabScenarios.HelmReleases())),
    ("cluster-tab-helm-release-detail", () => HostInMainWindow(ClusterTabScenarios.HelmReleaseDetail())),
    ("cluster-tab-rbac-who-can", () => HostInMainWindow(ClusterTabScenarios.RbacWhoCan(), height: 1000)),
    ("cluster-tab-rbac-who-can-empty", () => HostInMainWindow(ClusterTabScenarios.RbacWhoCan(empty: true))),
    ("cluster-tab-list-filtered", () => HostInMainWindow(ClusterTabScenarios.FilteredList())),
    // A list the reader has re-cut: the Name column dragged wider (the audit's own
    // complaint — two pods of one ReplicaSet rendering identically because the ellipsis
    // fell on the discriminating suffix) and ordered by a header click, with the arrow
    // saying which column and which way. Both come back out of the workspace, which is
    // what makes this a render of the *restore* path and not of a hand-set grid.
    ("cluster-tab-list-sorted", () => HostInMainWindow(ClusterTabScenarios.SortedList())),
    ("cluster-tab-list-filtered-empty", () => HostInMainWindow(ClusterTabScenarios.FilteredListEmpty())),
    // Unhealthy only (k9s's "toggle faults"): the narrowed list, its good-news empty
    // state, the same mode over a partial fleet and on the demo cluster, and the
    // disabled-but-still-on chip over a kind that has no health verdict to filter by.
    ("cluster-tab-list-unhealthy", () => HostInMainWindow(ClusterTabScenarios.UnhealthyList())),
    ("cluster-tab-list-unhealthy-all-healthy", () => HostInMainWindow(ClusterTabScenarios.UnhealthyListAllHealthy())),
    ("cluster-tab-list-unhealthy-fleet-partial",
        () => HostInMainWindow(ClusterTabScenarios.UnhealthyFleetPartial(), height: 1000)),
    // The toggled list at a narrow window: the caption and the chip beside the search
    // box are the two things this item added to the header row, and 1024px is where
    // that row runs out first.
    ("cluster-tab-list-unhealthy-narrow", () => HostInMainWindow(ClusterTabScenarios.UnhealthyList(), width: 1024)),
    ("cluster-tab-list-unhealthy-unavailable", () => HostInMainWindow(ClusterTabScenarios.UnhealthyUnavailable())),
    ("cluster-tab-list-unhealthy-demo", () => HostInMainWindow(ClusterTabScenarios.DemoUnhealthy())),
    // The mutating workload actions and their armed confirm strip.
    ("cluster-tab-row-action-scale", () => HostInMainWindow(ClusterTabScenarios.RowActionScale())),
    ("cluster-tab-row-action-restart", () => HostInMainWindow(ClusterTabScenarios.RowActionRestart())),
    ("cluster-tab-row-action-failed", () => HostInMainWindow(ClusterTabScenarios.RowActionFailed())),
    // "Open a terminal on this cluster" — the two outcomes the app has to state, since
    // the successful one opens a window in front of the app and needs no screenshot.
    ("cluster-tab-terminal-no-kubectl", () => HostInMainWindow(ClusterTabScenarios.TerminalNoKubectl())),
    ("cluster-tab-empty-namespace", () => HostInMainWindow(ClusterTabScenarios.EmptyNamespace())),
    ("cluster-tab-loading", () => HostInMainWindow(ClusterTabScenarios.Loading())),
    ("cluster-tab-disconnected", () => HostInMainWindow(ClusterTabScenarios.Disconnected())),
    // The demo cluster, built by running the real ConnectCommand — see ClusterTabScenarios.
    ("cluster-tab-demo-list", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("cluster-tab-demo-pod-detail", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetail(), height: 1000)),
    ("cluster-tab-demo-exec-unavailable", () => HostInMainWindow(ClusterTabScenarios.DemoExecUnavailable())),
    // Multi-pod logs. Taller than the default for the same reason pod detail is: the
    // whole point is how many merged lines you can read at once. The second shot is the
    // filter's own empty state, which is a different next step from "no pods logged".
    ("cluster-tab-workload-logs", () => HostInMainWindow(ClusterTabScenarios.DemoWorkloadLogs(), height: 1000)),
    ("cluster-tab-workload-logs-filtered-empty",
        () => HostInMainWindow(ClusterTabScenarios.DemoWorkloadLogs("checkout"))),
    // The CRD printer-column pair: the same Certificate list without and with the
    // advanced view, which is where the CRD's own `priority: 1` columns live.
    ("cluster-tab-crd-printer-columns", () => HostInMainWindow(ClusterTabScenarios.DemoCrdPrinterColumns())),
    ("cluster-tab-demo-scale-unavailable", () => HostInMainWindow(ClusterTabScenarios.DemoScaleUnavailable())),

    // FEAT-4 — the node surface. All on the demo cluster, which is where the node
    // dataset lives; the drain's progress states are the two the harness cannot produce
    // for real (no API server to evict through) and are written in, exactly as the
    // scale/exec/YAML fixtures are.
    ("cluster-tab-node-list", () => HostInMainWindow(ClusterTabScenarios.NodeList())),
    ("cluster-tab-node-detail", () => HostInMainWindow(ClusterTabScenarios.NodeDetail(), height: 1000)),
    ("cluster-tab-node-detail-pods", () => HostInMainWindow(ClusterTabScenarios.NodeDetailPods(), height: 1000)),
    ("cluster-tab-node-detail-events", () => HostInMainWindow(ClusterTabScenarios.NodeDetailEvents(), height: 1000)),
    ("cluster-tab-node-detail-usage", () => HostInMainWindow(ClusterTabScenarios.NodeDetailUsage(), height: 1000)),
    ("cluster-tab-node-detail-cordoned", () => HostInMainWindow(ClusterTabScenarios.NodeDetailCordoned(), height: 1000)),
    ("cluster-tab-node-drain-blocked", () => HostInMainWindow(ClusterTabScenarios.NodeDrainBlocked(), height: 1000)),
    ("cluster-tab-node-drain-running", () => HostInMainWindow(ClusterTabScenarios.NodeDrainRunning(), height: 1000)),
    ("cluster-tab-node-drain-stopped", () => HostInMainWindow(ClusterTabScenarios.NodeDrainStopped(), height: 1000)),
    ("cluster-tab-demo-terminal-unavailable",
        () => HostInMainWindow(ClusterTabScenarios.DemoTerminalUnavailable())),

    // Argo CD. The dashboard is the headline shot: two independent pills per row, the
    // seven counts, and the attention ordering that puts a Synced-but-Degraded
    // Application at the top.
    ("cluster-tab-argo-dashboard", () => HostInMainWindow(ClusterTabScenarios.ArgoDashboard())),
    ("cluster-tab-argo-application-detail",
        () => HostInMainWindow(ClusterTabScenarios.ArgoApplicationDetail(), height: 1000)),
    ("cluster-tab-argo-sync-unavailable", () => HostInMainWindow(ClusterTabScenarios.ArgoSyncUnavailable())),
    // L1 — the palette's log rows. On the demo cluster, which is the one place the rows
    // come from a real listing (the dataset) rather than a fixture; the states a sandbox
    // cannot produce on demand (in flight, refused, capped, a fleet) are written in
    // through the tab's fixture seam, after a real open so the seam is what wins.
    ("ux-logs-palette", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    // L2 — the row's logs icon and logs opened full-size. The hover is a real pointer
    // move (HoverRow) over a second row, so the shot shows the icon on the hovered and the
    // selected row and on no other; the ux- check clicks it at its edge, Shift+clicks it,
    // presses Shift+L and Esc.
    ("ux-row-logs", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("cluster-tab-row-logs", () => HostInMainWindow(ClusterTabScenarios.DemoRowLogs())),
    ("cluster-tab-row-logs-narrow", () => HostInMainWindow(ClusterTabScenarios.DemoRowLogs(), width: 860)),
    ("cluster-tab-row-logs-deployments", () => HostInMainWindow(ClusterTabScenarios.DemoRowLogsDeployments())),
    ("cluster-tab-row-logs-none", () => HostInMainWindow(ClusterTabScenarios.DemoRowLogsNone())),
    ("cluster-tab-logs-maximized", () => HostInMainWindow(ClusterTabScenarios.DemoLogsMaximized())),
    ("palette-logs", () => LogsPalette(ClusterTabScenarios.DemoList(), "")),
    ("palette-logs-search", () => LogsPalette(ClusterTabScenarios.DemoList(), "report")),
    ("palette-logs-narrow", () => LogsPalette(ClusterTabScenarios.DemoList(), "", width: 800)),
    // Without the prefix: a plain Ctrl/Cmd+K search for a name finds the log rows too,
    // after whatever commands match — which here is none.
    ("palette-logs-unprefixed", () => LogsPalette(ClusterTabScenarios.DemoList(), "checkout", prefix: false)),
    ("palette-logs-loading", () => LogsPalette(ClusterTabScenarios.DemoList(), "",
        fixture: tab => tab.SetLogTargetsForFixture(ClusterTabScenarios.StaleLogTargets(), loading: true))),
    ("palette-logs-denied", () => LogsPalette(ClusterTabScenarios.WorkloadsList(), "",
        fixture: tab => tab.SetLogTargetsForFixture(ClusterTabScenarios.RefusedLogTargets(), loading: false))),
    ("palette-logs-capped", () => LogsPalette(ClusterTabScenarios.WorkloadsList(), "",
        fixture: tab => tab.SetLogTargetsForFixture(ClusterTabScenarios.CappedLogTargets(), loading: false))),
    ("palette-logs-empty", () => LogsPalette(ClusterTabScenarios.WorkloadsList(), "",
        fixture: tab => tab.SetLogTargetsForFixture(KubeNimbus.App.ViewModels.LogTargetList.Empty, loading: false))),
    ("palette-logs-fleet", () => LogsPalette(ClusterTabScenarios.WorkloadsList(), "",
        fixture: tab => tab.SetLogTargetsForFixture(ClusterTabScenarios.FleetLogTargets(), loading: false))),
    ("palette-logs-disconnected", () => LogsPalette(ClusterTabScenarios.NotConnected(), "")),
    ("main-window", () => BuildMainWindowContent()),
    ("main-window-no-kubeconfig", () => BuildNoKubeconfigContent()),
    ("main-window-shortcuts", () => BuildMainWindowContent(openShortcuts: true)),
    ("main-window-switcher", () => BuildSwitcherContent()),
    // "pro" is a subsequence of several of these and a prefix of others — the
    // ranking (prefix > contiguous > subsequence) is the point of the shot.
    ("main-window-switcher-search", () => BuildSwitcherContent("pro")),

    // Preferences and About, which are overlays over the shell now rather than
    // windows of their own. Rendering them is not really about the picture: the
    // harness is CI's one check that a view still loads at all (a stale avares://
    // URI or a DataTemplate that stopped resolving compiles perfectly), and these
    // two views are loaded from nowhere else.
    ("main-window-preferences", () => BuildMainWindowContent(openPreferences: true)),
    // The same page scrolled to its Logs and metrics cards, where L2's "Open logs
    // maximized" switch sits below the fold of the shot above.
    ("main-window-preferences-logs", () => BuildMainWindowContent(openPreferences: true)),
    ("main-window-about", () => BuildMainWindowContent(openAbout: true)),
};

foreach (var (name, build) in scenarios)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        Capture(name, theme, build);
    }
}

Console.WriteLine($"Wrote screenshots to {Path.GetFullPath(outDir)}");
return;

void Capture(string name, ThemeVariant theme, Func<Control> build)
{
    Application.Current!.RequestedThemeVariant = theme;

    // Per-kind column widths and sort orders are persisted, so a scenario that seeds one
    // (see SortedList) would otherwise leave it in the shared scratch workspace for
    // every later scenario that lists the same kind. Cleared here rather than in the
    // scenario, because the view reads the layout while the window is laid out — which
    // is after the builder has returned.
    WorkspaceStore.Save(WorkspaceStore.Load() with { GridLayouts = [] });

    var content = build();
    var window = content as Window ?? new Window
    {
        Width = 1280,
        Height = 800,
        Content = content,
    };

    window.Show();
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();

    if (name == "ux-namespace-picker") UxInteractionChecks.NamespacePicker(window);
    if (name == "ux-unhealthy-toggle") UxInteractionChecks.UnhealthyToggle(window);
    if (name == "ux-logs-palette") UxInteractionChecks.LogsPalette(window);
    if (name == "ux-row-logs") UxInteractionChecks.RowLogs(window);
    if (name == "main-window-preferences-logs") UxInteractionChecks.ScrollPreferencesTo(window, "Open logs maximized");
    if (name.StartsWith("cluster-tab-row-logs", StringComparison.Ordinal)) UxInteractionChecks.HoverRow(window, 3);
    using var frame = window.CaptureRenderedFrame();
    var themeLabel = theme == ThemeVariant.Dark ? "dark" : "light";
    var path = Path.Combine(outDir, $"{name}.{themeLabel}.png");
    frame?.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    window.Close();
    Console.WriteLine(frame is null ? $"FAILED (no frame): {path}" : $"Wrote {path}");
}

// Hosts a single cluster tab inside a real MainWindow (command bar, tab strip,
// status bar) rather than a bare wrapper Border: ContentControl's implicit
// DataTemplate lookup walks the visual tree for a Window.DataTemplates match
// (see MainWindow.axaml), so an inspector tab only renders its real View —
// PodDetailView/YamlEditorView/etc — when hosted under the actual MainWindow.
// A bare wrapper falls back to a "ToString() in a TextBlock" placeholder.
static Control HostInMainWindow(ClusterTabViewModel tab, int height = 800, int width = 1280)
{
    var window = new MainWindow { Width = width, Height = height };
    var vm = new MainWindowViewModel();
    window.DataContext = vm;
    SeedContexts(vm);

    // Read the scenario's choice before adding the tab, because adding it is what
    // makes the shell stamp its own (persisted, default-off) value onto the tab —
    // the same seam production uses so a tab opened from anywhere arrives carrying
    // the global switch. The shell owns the flag, so the scenario has to set it here
    // rather than on the tab, or every advanced scenario silently renders plain.
    var advanced = tab.IsAdvancedView;

    vm.Tabs.Clear();
    vm.Tabs.Add(tab);
    vm.SelectedTab = tab;
    vm.IsAdvancedView = advanced;
    return window;
}

// A cluster tab with the palette open on its log rows. The open is the real one —
// Palette.Open runs the tab's RequestLogTargets, which on the demo cluster lists the
// dataset — and a fixture, when there is one, lands after it through the tab's seam.
static Control LogsPalette(
    ClusterTabViewModel tab, string query, Action<ClusterTabViewModel>? fixture = null, int width = 1280, bool prefix = true)
{
    var window = (Window)HostInMainWindow(tab, width: width);
    var vm = (MainWindowViewModel)window.DataContext!;
    vm.Palette.Open((prefix ? CommandPaletteViewModel.LogsPrefix : "") + query);
    fixture?.Invoke(tab);
    return window;
}

// Without this the command bar's cluster switcher reads "No clusters" in every
// screenshot — the fixture kubeconfig points at an address nothing listens on,
// so LoadContextsAsync finds nothing. That is a real state (it's what
// `cluster-tab-*` would show on a machine with no kubeconfig) but it is not the
// state these scenarios are about, and it makes every shot look like a failed
// connection.
//
// The set is deliberately messier than three tidy names: it spans every
// environment class, includes the auto-generated GKE/EKS shapes that are the
// reason the switcher searches instead of listing, and is long enough that the
// grouped/filtered popup has something to actually do.
static void SeedContexts(MainWindowViewModel vm)
{
    vm.AvailableContexts.Clear();
    foreach (var (name, cluster, ns) in new[]
             {
                 ("prod-payments", "payments-prod-euw1", "payments"),
                 ("prod-ledger", "ledger-prod-use1", "ledger"),
                 ("staging-eu", "staging-eu-west", "default"),
                 ("preprod-payments", "payments-preprod-euw1", "payments"),
                 ("qa-integration", "qa-int-cluster", "default"),
                 ("gke_acme-corp_europe-west4-a_analytics-prod", "analytics-prod", "analytics"),
                 ("arn:aws:eks:us-east-1:481516234298:cluster/search-staging", "search-staging", "search"),
                 ("kind-kubenimbus", "kind-kubenimbus", "default"),
                 ("docker-desktop", "docker-desktop", "default"),
                 ("minikube", "minikube", "default"),
             })
    {
        vm.AvailableContexts.Add(new ClusterContext(name, cluster, ns, "fixture-user", "/home/fixture/.kube/config"));
    }

    vm.HasContexts = true;
    vm.Status = $"{vm.AvailableContexts.Count} context(s) available.";
}

// The first thing a clean install shows, and — for anyone who downloaded a
// release rather than cloning the repo — quite possibly the only thing. It is
// the one screen with no cluster behind it, so the *other* scenarios all seed
// contexts to get past it (see SeedContexts); this one is deliberately the
// state they avoid. Search paths are written by hand rather than left to the
// real scan so the shot doesn't render the developer's own home directory.
static Control BuildNoKubeconfigContent()
{
    var window = new MainWindow { Width = 1280, Height = 800 };
    var vm = new MainWindowViewModel();
    window.DataContext = vm;

    vm.Tabs.Clear();
    vm.AvailableContexts.Clear();
    vm.HasContexts = false;
    // What LoadContextsAsync sets for this state. It is also what the empty-state card
    // renders as its heading — the card and the status bar bind the same property — so
    // this one string is deliberately doing both jobs.
    vm.KubeconfigSearchPathCount = 1;
    vm.Status = "No kubeconfig contexts found.";
    vm.KubeconfigSearchPaths = string.Join(
        System.Environment.NewLine,
        "missing  C:\\Users\\reviewer\\.kube\\config   (default location)");

    return window;
}

static Control BuildMainWindowContent(bool openShortcuts = false, bool openPreferences = false, bool openAbout = false)
{
    var window = new MainWindow();
    var vm = new MainWindowViewModel();
    window.DataContext = vm;
    window.Width = 1280;
    window.Height = 800;
    SeedContexts(vm);

    vm.Tabs.Clear();
    var tabA = ClusterTabScenarios.WorkloadsList();
    var tabB = ClusterTabScenarios.PodDetail();
    vm.Tabs.Add(tabA);
    vm.Tabs.Add(tabB);
    vm.SelectedTab = tabB;
    vm.IsShortcutsOpen = openShortcuts;

    // The preferences page proxies the shell's own state, and the settings it writes
    // land in the harness's redirected directory (AppSettingsStore.DirectoryOverride
    // at the top of this file) rather than the developer's own.
    vm.IsPreferencesOpen = openPreferences;
    vm.IsAboutOpen = openAbout;

    return window;
}

// The cluster switcher, open. `query` renders the searching state — one flat
// ranked list — against the grouped Open/Pinned/All layout of the empty query.
static Control BuildSwitcherContent(string? query = null)
{
    var window = new MainWindow();
    var vm = new MainWindowViewModel();
    window.DataContext = vm;
    window.Width = 1280;
    window.Height = 800;
    SeedContexts(vm);

    vm.Tabs.Clear();
    var tab = ClusterTabScenarios.WorkloadsList();
    vm.Tabs.Add(tab);
    vm.SelectedTab = tab;

    // Pinning is the feature that makes a long kubeconfig usable without typing,
    // so at least one pinned row has to be in the shot.
    vm.SetPinned("staging-eu", true);
    vm.SetPinned("gke_acme-corp_europe-west4-a_analytics-prod", true);

    vm.Switcher.Open();
    if (query is not null)
    {
        vm.Switcher.Query = query;
    }

    return window;
}

static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont();
