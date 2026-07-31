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
using KubeNimbus.Screenshot;

// Usage: dotnet run --project tools/Screenshot -- <outputDir> [scenario-substring]
// Renders every scenario in both light and dark, one PNG per (scenario, theme).
// This is the replacement for the local session's Avalonia DevTools MCP
// screenshot loop when developing in an environment with no display — see
// CLAUDE.md "Headless screenshot harness".

var outDir = args.Length > 0 ? args[0] : "screenshots";
var filter = args.Length > 1 ? args[1] : null;
Directory.CreateDirectory(outDir);

BuildAvaloniaApp().SetupWithoutStarting();

var scenarios = new (string Name, Func<Control> Build)[]
{
    ("cluster-tab-workloads-list", () => HostInMainWindow(ClusterTabScenarios.WorkloadsList())),
    ("cluster-tab-workloads-list-metrics", () => HostInMainWindow(ClusterTabScenarios.WorkloadsListWithMetrics())),
    ("cluster-tab-events-list", () => HostInMainWindow(ClusterTabScenarios.EventsList())),
    ("cluster-tab-fleet-list", () => HostInMainWindow(ClusterTabScenarios.FleetList(), height: 1000)),
    ("cluster-tab-fleet-list-partial", () => HostInMainWindow(ClusterTabScenarios.FleetListPartial(), height: 1000)),
    ("cluster-tab-sidebar-filtered", () => HostInMainWindow(ClusterTabScenarios.SidebarFiltered())),
    ("cluster-tab-sidebar-filtered-by-group", () => HostInMainWindow(ClusterTabScenarios.SidebarFilteredByGroup())),
    ("cluster-tab-sidebar-recent", () => HostInMainWindow(ClusterTabScenarios.SidebarRecentKinds())),
    ("cluster-tab-sidebar-crds-expanded", () => HostInMainWindow(ClusterTabScenarios.SidebarCrdsExpanded(), height: 1500)),
    ("cluster-tab-pod-detail", () => HostInMainWindow(ClusterTabScenarios.PodDetail())),
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
    ("cluster-tab-port-forward", () => HostInMainWindow(ClusterTabScenarios.PortForward())),
    ("cluster-tab-helm-releases", () => HostInMainWindow(ClusterTabScenarios.HelmReleases())),
    ("cluster-tab-rbac-who-can", () => HostInMainWindow(ClusterTabScenarios.RbacWhoCan(), height: 1000)),
    ("cluster-tab-rbac-who-can-empty", () => HostInMainWindow(ClusterTabScenarios.RbacWhoCan(empty: true))),
    ("cluster-tab-empty-namespace", () => HostInMainWindow(ClusterTabScenarios.EmptyNamespace())),
    ("cluster-tab-loading", () => HostInMainWindow(ClusterTabScenarios.Loading())),
    ("cluster-tab-disconnected", () => HostInMainWindow(ClusterTabScenarios.Disconnected())),
    ("main-window", () => BuildMainWindowContent()),
    ("main-window-shortcuts", () => BuildMainWindowContent(openShortcuts: true)),
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
    vm.Tabs.Clear();
    vm.Tabs.Add(tab);
    vm.SelectedTab = tab;
    return window;
}

static Control BuildMainWindowContent(bool openShortcuts = false)
{
    var window = new MainWindow();
    var vm = new MainWindowViewModel();
    window.DataContext = vm;
    window.Width = 1280;
    window.Height = 800;

    vm.Tabs.Clear();
    var tabA = ClusterTabScenarios.WorkloadsList();
    var tabB = ClusterTabScenarios.PodDetail();
    vm.Tabs.Add(tabA);
    vm.Tabs.Add(tabB);
    vm.SelectedTab = tabB;
    vm.IsShortcutsOpen = openShortcuts;

    return window;
}

static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont();
