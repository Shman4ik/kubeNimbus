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
    ("cluster-tab-workloads-list", () => HostInMainWindow(ClusterTabScenarios.WorkloadsList())),
    // The before/after pair for the advanced view. Same tab, same seeded usage data —
    // the only difference is the switch, which is the claim being made: it hides and
    // shows, it does not build a second layout.
    ("cluster-tab-advanced-view", () => HostInMainWindow(ClusterTabScenarios.AdvancedView())),
    ("cluster-tab-workloads-list-metrics", () => HostInMainWindow(ClusterTabScenarios.WorkloadsListWithMetrics())),
    ("cluster-tab-events-list", () => HostInMainWindow(ClusterTabScenarios.EventsList())),
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
    ("cluster-tab-pod-detail-usage", () => HostInMainWindow(ClusterTabScenarios.PodDetailUsage(), height: 1000)),
    ("cluster-tab-pod-detail-usage-unavailable", () => HostInMainWindow(ClusterTabScenarios.PodDetailUsageUnavailable())),
    ("cluster-tab-yaml-editor", () => HostInMainWindow(ClusterTabScenarios.YamlEditor())),
    ("cluster-tab-yaml-editor-maximized", () => HostInMainWindow(ClusterTabScenarios.YamlEditorMaximized())),
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
    ("cluster-tab-list-filtered-empty", () => HostInMainWindow(ClusterTabScenarios.FilteredListEmpty())),
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
    ("cluster-tab-crd-printer-columns-wide", () => HostInMainWindow(ClusterTabScenarios.DemoCrdPrinterColumnsWide())),
    ("cluster-tab-demo-scale-unavailable", () => HostInMainWindow(ClusterTabScenarios.DemoScaleUnavailable())),
    ("cluster-tab-demo-terminal-unavailable",
        () => HostInMainWindow(ClusterTabScenarios.DemoTerminalUnavailable())),
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
static Control HostInMainWindow(ClusterTabViewModel tab, int height = 800)
{
    var window = new MainWindow { Width = 1280, Height = height };
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
