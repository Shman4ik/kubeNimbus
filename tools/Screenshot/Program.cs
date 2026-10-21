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
using Nimbus.Ui.Fonts;

// Usage: dotnet run --project tools/Screenshot -- <outputDir> [scenario-substring]
// Renders every scenario in both light and dark, one PNG per (scenario, theme).
// This is the replacement for the local session's Avalonia DevTools MCP
// screenshot loop when developing in an environment with no display — see
// CLAUDE.md "Headless screenshot harness".

// `--shard=<number>/<count>` (1-based) renders only that shard's scenarios (ShardPlan): CI splits the
// list across a job matrix (#347). It may appear anywhere in the arguments.
(ShardPlan.Current, args) = ShardPlan.Parse(args);

// `--jobs=N` (or `--jobs`, one per two cores, at most four) runs that many copies of this harness,
// each rendering one shard into the same output directory: the CI matrix on one machine
// (ParallelRun). Every other argument, `--twice` included, is passed to each child unchanged.
int jobs;
(jobs, args) = ParallelRun.Parse(args);
if (jobs > 1)
{
    if (ShardPlan.Current.IsSharded || args.Contains("--stress"))
    {
        Console.Error.WriteLine("--jobs splits a full render into shards itself; it cannot be combined with --shard or --stress.");
        return 2;
    }

    return ParallelRun.Run(jobs, args);
}

// `--twice` renders every ux-* scenario a second time and fails the run when the two PNGs differ
// (#360, HarnessDeterminism). The nightly workflow passes it; a PR's smoke test does not, so it
// costs a PR nothing. It may appear anywhere in the arguments.
HarnessDeterminism.Twice = args.Contains("--twice");
args = [.. args.Where(a => a != "--twice")];

// `-- --stress` runs StressChecks instead of rendering: every data surface fed a large
// cluster's worth of objects, with its visuals, collection notifications and time checked
// against a budget. Exits non-zero when anything is over.
var stress = args.Length > 0 && args[0] == "--stress";
var outDir = !stress && args.Length > 0 ? args[0] : "screenshots";
var filter = args.Length > 1 ? args[1] : null;
if (!stress)
{
    Directory.CreateDirectory(outDir);
}

// Every local time the app prints goes through LocalTimeText.ToLocal; render them in UTC, so
// a published screenshot reads the same on every machine (#346: .NET on Windows ignores TZ,
// and the application page's exit line and timeline printed the renderer's own offset).
LocalTimeText.ToLocal = at => at.ToUniversalTime();
LocalZoneCheck.Run();

// Scenarios construct real MainWindowViewModels, which read the workspace on
// construction and save it whenever a cluster is pinned. Point that at a scratch
// directory so rendering fixtures can't read — or clobber — the developer's own
// open tabs, pins and theme.
//
// One directory per run, not one shared name under %TEMP%: two harness runs at once —
// two worktrees, two agents, a CI matrix — used to read and write each other's
// workspace.json and settings.json mid-render, so a sidebar section one run expanded
// showed up collapsed or expanded in the other's PNGs at random (ENG-10). Removed again
// when the run ends.
var scratch = Path.Combine(Path.GetTempPath(), "kubenimbus-screenshot-workspace", Guid.NewGuid().ToString("n"));
WorkspaceStore.DirectoryOverride = scratch;
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

// And the kubeconfig chain. Every scenario builds a real MainWindowViewModel, whose
// constructor reads $KUBECONFIG and ~/.kube/config and, with no saved tabs, opens one on
// the current context — i.e. connects to whatever cluster the developer running the
// harness has. That made main-window-no-kubeconfig render the developer's own pods on a
// machine with a live sandbox and the empty state everywhere else, differing between two
// runs of the same commit by whether the connect had landed before the capture (ENG-10).
// CI has no kubeconfig, which is why it never showed there. Scenarios that want contexts
// seed them by hand (SeedContexts).
Kubeconfig.EnvironmentSearchOverride = [];

// And the clock. Every time the app shows reads AppClock, and the harness stops it at the
// instant the demo dataset describes. Ages read against the wall clock made two runs of one
// commit differ on either side of midnight ("74d" against "75d"), so every re-render of the
// published screenshots differed in an Age cell (#350). BannedSymbols.Clock.txt keeps the app
// and this harness from reading the wall clock past it; ClockChecks checks the rows it rendered.
AppClock.Time = new FixedClock(KubeNimbus.App.Demo.DemoData.Now);

// And the demo's log replay, which hands out a line every 280 ms so the demo reads as a live
// follow. Every log scenario waits for its replay to end before the capture (LogSettle), so at
// that pace a single pod's stream of a few dozen lines was seconds of sleeping per capture. The
// lines, their order (ENG-53 replays a merged pane in logged order) and how the stream ends do
// not depend on the pace, so the PNGs do not either; what is left of the wait is the pane's own
// 100 ms flush. The one wait that stops part-way through a replay keeps the app's pace for its
// length (ClusterTabScenarios.DrainDemoLogs says why).
KubeNimbus.App.Demo.DemoLogs.Interval = TimeSpan.FromMilliseconds(10);

BuildAvaloniaApp().SetupWithoutStarting();

// And the transitions (#360). A brush transition (nimbusUi's .soft buttons have one) runs on the
// render clock, which in the headless platform advances with the wall clock between forced
// ticks, so a capture taken while one is in flight would land at a different fraction of it on
// every run. The differences #360 measured had another cause, the partial repaints
// HarnessDeterminism.RepaintWhole removes before each capture; HarnessDeterminism also renders
// each interaction scenario twice and fails the run on a difference.
HarnessDeterminism.StopTransitions(Application.Current!);

if (stress)
{
    var code = StressChecks.Run(
        (tab, applications) => HostInMainWindow(tab, mode: applications ? ShellMode.Applications : ShellMode.Resources), filter);
    try
    {
        Directory.Delete(scratch, recursive: true);
    }
    catch (IOException)
    {
    }

    Environment.Exit(code);
}


var scenarios = new (string Name, Func<Control> Build)[]
{
    ("ux-namespace-picker", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("cluster-tab-namespace-picker-several", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("ux-unhealthy-toggle", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("ux-workload-pods", () => HostInMainWindow(ClusterTabScenarios.WorkloadDetail(), height: 1000)),
    ("ux-workload-pods-sort", () => HostInMainWindow(ClusterTabScenarios.WorkloadDetail(), height: 1000)),
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
    // #320: the Usage tab at the default dock height shows the first container without a scroll.
    ("cluster-tab-pod-detail-usage-docked", () => HostInMainWindow(ClusterTabScenarios.PodDetailUsageDocked())),
    ("cluster-tab-pod-detail-usage-unavailable", () => HostInMainWindow(ClusterTabScenarios.PodDetailUsageUnavailable())),
    ("cluster-tab-pod-detail-usage-unset", () => HostInMainWindow(ClusterTabScenarios.PodDetailUsageUnset())),
    ("cluster-tab-yaml-editor", () => HostInMainWindow(ClusterTabScenarios.YamlEditor())),
    ("cluster-tab-yaml-delete-production", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDeleteProduction())),
    ("cluster-tab-yaml-editor-maximized", () => HostInMainWindow(ClusterTabScenarios.YamlEditorMaximized())),
    // #270: metadata.managedFields out of the text by default, and back with the chip pressed.
    ("cluster-tab-yaml-managed-fields-hidden", () => HostInMainWindow(ClusterTabScenarios.YamlEditorManagedFields(shown: false))),
    ("cluster-tab-yaml-managed-fields-shown", () => HostInMainWindow(ClusterTabScenarios.YamlEditorManagedFields(shown: true))),
    ("cluster-tab-yaml-diff-preview", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffPreview())),
    ("cluster-tab-yaml-diff-split", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffSplit())),
    ("cluster-tab-yaml-diff-fields", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffFields())),
    // The two view-mode × dock-state pairs the four above left out (#284).
    ("cluster-tab-yaml-diff-split-maximized", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffSplitMaximized())),
    ("cluster-tab-yaml-diff-fields-dock", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffFieldsDock())),
    ("cluster-tab-yaml-diff-no-change", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffNoChange())),
    ("cluster-tab-yaml-validation-rejected", () => HostInMainWindow(ClusterTabScenarios.YamlEditorValidationRejected())),
    ("cluster-tab-yaml-conflict", () => HostInMainWindow(ClusterTabScenarios.YamlEditorConflict())),
    // #316: the diff at the dock's default height, and a conflicting Review with its would-be diff.
    ("cluster-tab-yaml-diff-dock", () => HostInMainWindow(ClusterTabScenarios.YamlEditorDiffDock())),
    ("cluster-tab-yaml-conflict-diff", () => HostInMainWindow(ClusterTabScenarios.YamlEditorConflictDiff())),
    ("cluster-tab-yaml-secret-masked", () => HostInMainWindow(ClusterTabScenarios.YamlEditorSecretMasked())),
    ("cluster-tab-yaml-secret-revealed", () => HostInMainWindow(ClusterTabScenarios.YamlEditorSecretRevealed())),
    // FEAT-30 — a TLS Secret's certificate chain, decoded without a Reveal.
    ("cluster-tab-yaml-tls-secret", () => HostInMainWindow(ClusterTabScenarios.YamlEditorTlsSecret())),
    ("cluster-tab-exec", () => HostInMainWindow(ClusterTabScenarios.Exec())),
    ("cluster-tab-exec-fullscreen", () => HostInMainWindow(ClusterTabScenarios.ExecFullScreen())),
    ("cluster-tab-exec-fullscreen-maximized", () => HostInMainWindow(ClusterTabScenarios.ExecFullScreenMaximized())),
    ("cluster-tab-exec-no-shell", () => HostInMainWindow(ClusterTabScenarios.ExecNoShell())),
    ("cluster-tab-exec-debug-container", () => HostInMainWindow(ClusterTabScenarios.ExecDebugContainer())),
    ("cluster-tab-port-forward", () => HostInMainWindow(ClusterTabScenarios.PortForward())),
    ("cluster-tab-port-forward-idle", () => HostInMainWindow(ClusterTabScenarios.PortForwardIdle())),
    ("cluster-tab-port-forward-service", () => HostInMainWindow(ClusterTabScenarios.PortForwardService())),
    ("cluster-tab-port-forwards-list", PortForwardsListWindow),
    ("ux-port-forwards-row-menu", PortForwardsListWindow),
    ("cluster-tab-helm-releases", () => HostInMainWindow(ClusterTabScenarios.HelmReleases())),
    ("ux-helm-sort", () => HostInMainWindow(ClusterTabScenarios.HelmReleases())),
    ("ux-helm-row-menu", () => HostInMainWindow(ClusterTabScenarios.HelmReleases())),
    ("cluster-tab-helm-release-detail", () => HostInMainWindow(ClusterTabScenarios.HelmReleaseDetail())),
    ("cluster-tab-rbac-who-can", () => HostInMainWindow(ClusterTabScenarios.RbacWhoCan(), height: 1000)),
    ("cluster-tab-rbac-who-can-empty", () => HostInMainWindow(ClusterTabScenarios.RbacWhoCan(empty: true))),
    // "Signed in as" above My permissions (FEAT-56): the name and groups the server reports,
    // and the stated state of a server older than 1.27, which cannot say.
    ("cluster-tab-rbac-whoami", () => HostInMainWindow(ClusterTabScenarios.RbacMyPermissions())),
    ("cluster-tab-rbac-whoami-not-served", () => HostInMainWindow(ClusterTabScenarios.RbacMyPermissions(
        SelfSubjectIdentity.Unknown(SelfSubjectReviewOutcome.NotServed, null)))),
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
    // ENG-32: the fullest header row there is — the fleet chip and its summary, a
    // connection warning, the "n of m" caption and the unhealthy chip — at the width it
    // runs out first. Search, the chip and Refresh have to stay on screen.
    ("cluster-tab-list-unhealthy-fleet-partial-narrow",
        () => HostInMainWindow(ClusterTabScenarios.UnhealthyFleetPartial(), height: 1000, width: 1024)),
    ("cluster-tab-list-unhealthy-unavailable", () => HostInMainWindow(ClusterTabScenarios.UnhealthyUnavailable())),
    ("cluster-tab-list-unhealthy-demo", () => HostInMainWindow(ClusterTabScenarios.DemoUnhealthy())),
    // The mutating workload actions and their armed confirm strip.
    ("cluster-tab-row-action-scale", () => HostInMainWindow(ClusterTabScenarios.RowActionScale())),
    ("cluster-tab-row-action-restart", () => HostInMainWindow(ClusterTabScenarios.RowActionRestart())),
    ("cluster-tab-row-action-failed", () => HostInMainWindow(ClusterTabScenarios.RowActionFailed())),
    // The safety pass on mutating actions (security block 3): a production delete names the
    // cluster in words and colour, and the scale question warns about the number in the box.
    ("cluster-tab-row-action-delete-production", () => HostInMainWindow(ClusterTabScenarios.RowActionDeleteProduction())),
    ("cluster-tab-row-action-scale-jump", () => HostInMainWindow(ClusterTabScenarios.RowActionScaleJump())),
    ("cluster-tab-row-action-scale-zero-production",
        () => HostInMainWindow(ClusterTabScenarios.RowActionScaleZeroProduction())),
    ("cluster-tab-row-action-scaled", () => HostInMainWindow(ClusterTabScenarios.RowActionScaled())),
    ("cluster-tab-row-action-busy", () => HostInMainWindow(ClusterTabScenarios.RowActionBusy())),
    // The window's minimum width: the sentence wraps beside its buttons instead of pushing
    // them out of the card (FEAT-77), and the scale box stays at the sentence's end.
    ("cluster-tab-row-action-scale-narrow", () => HostInMainWindow(ClusterTabScenarios.RowActionScale(), width: 960)),
    ("cluster-tab-row-action-restart-narrow", () => HostInMainWindow(ClusterTabScenarios.RowActionRestart(), width: 960)),
    // "Open a terminal on this cluster" — the two outcomes the app has to state, since
    // the successful one opens a window in front of the app and needs no screenshot.
    ("cluster-tab-terminal-no-kubectl", () => HostInMainWindow(ClusterTabScenarios.TerminalNoKubectl())),
    ("cluster-tab-empty-namespace", () => HostInMainWindow(ClusterTabScenarios.EmptyNamespace())),
    ("cluster-tab-loading", () => HostInMainWindow(ClusterTabScenarios.Loading())),
    ("cluster-tab-disconnected", () => HostInMainWindow(ClusterTabScenarios.Disconnected())),
    // FEAT-51 / FEAT-53: a connect that failed, stated in the content area in both modes
    // (a plugin that is not installed, and credentials that expired), and a running
    // watch whose credential was refused, with Reconnect beside the warning.
    ("cluster-tab-connection-failed-plugin", () => HostInMainWindow(ClusterTabScenarios.ConnectionFailed("plugin"), height: 900)),
    ("cluster-tab-connection-failed-expired", () => HostInMainWindow(ClusterTabScenarios.ConnectionFailed("expired"))),
    ("applications-connection-failed",
        () => HostInMainWindow(ClusterTabScenarios.ConnectionFailed("plugin"), height: 900, mode: ShellMode.Applications)),
    ("cluster-tab-credentials-expired", () => HostInMainWindow(ClusterTabScenarios.CredentialsExpired())),
    ("cluster-tab-tls-unverified", () => HostInMainWindow(ClusterTabScenarios.TlsUnverified())),
    ("cluster-tab-plain-http", () => HostInMainWindow(ClusterTabScenarios.PlainHttp())),
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
    // The log panes' reading tools (FEAT-33/36/39) and ENG-45's never-started pods.
    ("cluster-tab-workload-logs-find", () => HostInMainWindow(ClusterTabScenarios.DemoWorkloadLogsFind(), height: 1000)),
    ("cluster-tab-workload-logs-not-started", () => HostInMainWindow(ClusterTabScenarios.DemoWorkloadLogsNotStarted())),
    // #319: a pane's state stays said while it holds lines — a snapshot, Follow off.
    ("cluster-tab-demo-pod-detail-previous", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetailPreviousSnapshot())),
    ("cluster-tab-workload-logs-stopped", () => HostInMainWindow(ClusterTabScenarios.DemoWorkloadLogsStopped())),
    ("cluster-tab-demo-pod-detail-find", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetailSearch(), height: 1000)),
    ("cluster-tab-demo-pod-detail-levels",
        () => HostInMainWindow(ClusterTabScenarios.DemoPodDetailSearch(query: "", hideInfo: true), height: 1000)),
    // The log viewer pass: regex filter with context, a pin, the error jump; a JSON line opened.
    ("cluster-tab-demo-pod-detail-grep", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetailGrep(), height: 1000)),
    ("cluster-tab-demo-pod-detail-json", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetailJson(), height: 1000)),
    // ENG-51: both log bars at the window's minimum width and just under where they used to
    // overflow, with the error/warning counts showing and, in the grep shot, the filter's
    // context chip too — the widest the bar gets (LayoutChecks.LogBarFits).
    ("ux-log-bar-pod-960", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetail(), height: 1000, width: 960)),
    ("ux-log-bar-pod-1100", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetail(), height: 1000, width: 1100)),
    ("ux-log-bar-pod-grep-960", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetailGrep(), height: 1000, width: 960)),
    ("ux-log-bar-workload-960", () => HostInMainWindow(ClusterTabScenarios.DemoWorkloadLogsFind(), height: 1000, width: 960)),
    // The same bars in a wide interface face. Linux CI draws in DejaVu Sans, which is wider
    // than Segoe UI, and the first cut fitted Windows and overflowed there by 19px. Verdana is
    // as wide on Windows; DejaVu Sans is named first so CI measures the face it ships with.
    // The capture re-applies the faces before the next scenario.
    ("ux-log-bar-pod-grep-960-wide-face", () =>
    {
        Avalonia.Application.Current!.Resources["ContentControlThemeFontFamily"] = new Avalonia.Media.FontFamily("DejaVu Sans, Verdana");
        return HostInMainWindow(ClusterTabScenarios.DemoPodDetailGrep(), height: 1000, width: 960);
    }),
    ("ux-log-bar-workload-960-wide-face", () =>
    {
        Avalonia.Application.Current!.Resources["ContentControlThemeFontFamily"] = new Avalonia.Media.FontFamily("DejaVu Sans, Verdana");
        return HostInMainWindow(ClusterTabScenarios.DemoWorkloadLogsFind(), height: 1000, width: 960);
    }),
    // Both log panes scrolled to their end: the last line stays clear of the horizontal
    // scroll bar, which Fluent draws over the content (LayoutChecks.LogEndClearsScrollBar).
    ("ux-log-end-gap-pod", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetailSearch(query: ""))),
    ("ux-log-end-gap-workload", () => HostInMainWindow(ClusterTabScenarios.DemoWorkloadLogs())),
    // The CRD printer-column pair: the same Certificate list without and with the
    // advanced view, which is where the CRD's own `priority: 1` columns live.
    ("cluster-tab-crd-printer-columns", () => HostInMainWindow(ClusterTabScenarios.DemoCrdPrinterColumns())),
    // #320: at 960px a cut header ends in an ellipsis with its name on a tooltip, and a press
    // dragged off a header does not sort (PaneTextChecks.CrdHeadersAt960).
    ("cluster-tab-crd-printer-columns-960", () => HostInMainWindow(ClusterTabScenarios.DemoCrdPrinterColumns(), width: 960)),
    // #362: a CRD column widened by a drag keeps its width across a switch to Pods and back
    // (ColumnWidthChecks.CrdWidthKeptAcrossKinds).
    ("ux-crd-column-width-kept", () => HostInMainWindow(ClusterTabScenarios.DemoCrdPrinterColumns())),
    ("cluster-tab-demo-scale-unavailable", () => HostInMainWindow(ClusterTabScenarios.DemoScaleUnavailable())),
    // FEAT-8 — a CronJob's run-now and resume on the shared strip, the created-Job state
    // with its "Open Job" follow-up, and a Job opened in the workload pane.
    ("cluster-tab-cronjob-run-now", () => HostInMainWindow(ClusterTabScenarios.CronJobRunNow())),
    ("cluster-tab-cronjob-run-now-done", () => HostInMainWindow(ClusterTabScenarios.CronJobRunNowDone())),
    ("cluster-tab-cronjob-resume", () => HostInMainWindow(ClusterTabScenarios.CronJobResume())),
    ("cluster-tab-job-detail", () => HostInMainWindow(ClusterTabScenarios.JobDetail(), height: 1000)),
    // FEAT-47 — a bound PersistentVolume naming its claim, beside one nothing has claimed.
    ("cluster-tab-persistent-volumes", () => HostInMainWindow(ClusterTabScenarios.PersistentVolumes())),
    // #308 — a namespace out of room: ResourceQuota rows full, almost full and with room.
    ("cluster-tab-resource-quotas", () => HostInMainWindow(ClusterTabScenarios.ResourceQuotas())),
    // Bundle L — a bulk delete armed and after a partial refusal (FEAT-6), the same driven by
    // pointer and key on the demo list, the column chooser (FEAT-13) and New YAML… (FEAT-12).
    ("cluster-tab-row-action-bulk-delete", () => HostInMainWindow(ListCreateChecks.BulkDeleteArmed())),
    ("cluster-tab-bulk-delete-partial", () => HostInMainWindow(ListCreateChecks.BulkDeletePartial())),
    ("ux-bulk-delete", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("ux-column-chooser", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    // #301: the chooser by keyboard, from the palette, with focus shown and given back.
    ("ux-column-chooser-keyboard", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("cluster-tab-yaml-new", () => HostInMainWindow(ListCreateChecks.NewYaml())),

    // FEAT-4 — the node surface. All on the demo cluster, which is where the node
    // dataset lives; the drain's progress states are the two the harness cannot produce
    // for real (no API server to evict through) and are written in, exactly as the
    // scale/exec/YAML fixtures are.
    ("cluster-tab-node-list", () => HostInMainWindow(ClusterTabScenarios.NodeList())),
    // FEAT-25 (#206): the capacity strip above the Nodes list — full in the shot above, and
    // here without metrics-server and under narrow RBAC, each refused half saying so in place.
    ("cluster-tab-node-capacity-no-metrics", () => HostInMainWindow(ClusterTabScenarios.NodeCapacityNoMetrics())),
    ("cluster-tab-node-capacity-narrow-rbac", () => HostInMainWindow(ClusterTabScenarios.NodeCapacityNarrowRbac())),
    ("cluster-tab-node-detail", () => HostInMainWindow(ClusterTabScenarios.NodeDetail(), height: 1000)),
    ("cluster-tab-node-detail-pods", () => HostInMainWindow(ClusterTabScenarios.NodeDetailPods(), height: 1000)),
    ("ux-node-pods-sort", () => HostInMainWindow(ClusterTabScenarios.NodeDetailPods(), height: 1000)),
    ("cluster-tab-node-detail-events", () => HostInMainWindow(ClusterTabScenarios.NodeDetailEvents(), height: 1000)),
    ("cluster-tab-node-detail-usage", () => HostInMainWindow(ClusterTabScenarios.NodeDetailUsage(), height: 1000)),
    ("cluster-tab-node-detail-cordoned", () => HostInMainWindow(ClusterTabScenarios.NodeDetailCordoned(), height: 1000)),
    ("cluster-tab-node-drain-blocked", () => HostInMainWindow(ClusterTabScenarios.NodeDrainBlocked(), height: 1000)),
    ("cluster-tab-node-drain-running", () => HostInMainWindow(ClusterTabScenarios.NodeDrainRunning(), height: 1000)),
    ("cluster-tab-node-drain-stopped", () => HostInMainWindow(ClusterTabScenarios.NodeDrainStopped(), height: 1000)),
    ("cluster-tab-demo-terminal-unavailable",
        () => HostInMainWindow(ClusterTabScenarios.DemoTerminalUnavailable())),

    // Bundle A — networking. "Why is traffic not reaching my pods": the Service pane's five
    // states (partial, no match, nothing serving, no selector, ExternalName), the Ingress
    // and NetworkPolicy panes, and the four kinds' kubectl list columns.
    ("net-service-list", () => HostInMainWindow(NetworkingScenarios.ServiceList())),
    ("net-service-detail", () => HostInMainWindow(NetworkingScenarios.ServiceDetail(), height: 1000)),
    ("net-service-overview", () => HostInMainWindow(NetworkingScenarios.ServiceOverview(), height: 1000)),
    ("net-service-no-match", () => HostInMainWindow(NetworkingScenarios.ServiceNoMatch(), height: 1000)),
    ("net-service-nothing-serving", () => HostInMainWindow(NetworkingScenarios.ServiceNothingServing(), height: 1000)),
    ("net-service-no-selector", () => HostInMainWindow(NetworkingScenarios.ServiceNoSelector(), height: 1000)),
    ("net-service-external-name", () => HostInMainWindow(NetworkingScenarios.ServiceExternalName(), height: 1000)),
    ("net-service-deleted", () => HostInMainWindow(NetworkingScenarios.ServiceDeleted(), height: 1000)),
    ("net-ingress-list", () => HostInMainWindow(NetworkingScenarios.IngressList())),
    ("net-ingress-detail", () => HostInMainWindow(NetworkingScenarios.IngressDetail(), height: 1000)),
    ("net-netpol-list", () => HostInMainWindow(NetworkingScenarios.NetworkPolicyList())),
    ("net-netpol-detail", () => HostInMainWindow(NetworkingScenarios.NetworkPolicyDetail(), height: 1000)),
    ("net-netpol-default-deny-pods", () => HostInMainWindow(NetworkingScenarios.NetworkPolicyDefaultDenyPods(), height: 1000)),
    ("net-endpointslice-list", () => HostInMainWindow(NetworkingScenarios.EndpointSliceList())),

    // Argo CD. The dashboard is the headline shot: two independent pills per row, the
    // seven counts, and the attention ordering that puts a Synced-but-Degraded
    // Application at the top.
    ("cluster-tab-argo-dashboard", () => HostInMainWindow(ClusterTabScenarios.ArgoDashboard())),
    ("cluster-tab-argo-application-detail",
        () => HostInMainWindow(ClusterTabScenarios.ArgoApplicationDetail(), height: 1000)),
    ("ux-argo-sort", () => HostInMainWindow(ClusterTabScenarios.ArgoDashboard())),
    ("ux-argo-resources-row-menu",
        () => HostInMainWindow(ClusterTabScenarios.ArgoApplicationDetail(), height: 1000)),
    ("cluster-tab-argo-sync-unavailable", () => HostInMainWindow(ClusterTabScenarios.ArgoSyncUnavailable())),
    ("cluster-tab-argo-sync-prune", () => HostInMainWindow(ClusterTabScenarios.ArgoSyncPrune())),
    // Bundle K: terminate (#193), the ApplicationSet and AppProject panes (#194), the fleet list (#195).
    ("cluster-tab-argo-terminate", () => HostInMainWindow(ArgoScenarios.Terminate())),
    ("cluster-tab-argo-applicationset-detail", () => HostInMainWindow(ArgoScenarios.ApplicationSetDetail(), height: 1000)),
    ("cluster-tab-argo-applicationset-overview", () => HostInMainWindow(ArgoScenarios.ApplicationSetDetail(0), height: 1000)),
    ("cluster-tab-argo-appproject-detail", () => HostInMainWindow(ArgoScenarios.ProjectDetail(), height: 1000)),
    ("cluster-tab-argo-fleet", () => HostInMainWindow(ArgoScenarios.Fleet())),
    ("cluster-tab-argo-refused", () => HostInMainWindow(RefusalScenarios.Argo())),
    ("cluster-tab-list-refused", () => HostInMainWindow(RefusalScenarios.List())),
    // #354: the Argo rows kept with "Live updates stopped", and every chosen namespace refused.
    ("cluster-tab-argo-watch-stopped", () => HostInMainWindow(RefusalScenarios.ArgoStopped())),
    ("cluster-tab-list-refused-namespaces", () => HostInMainWindow(RefusalScenarios.ListNamespaces())),
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
    // L3 — logs from everywhere a pod is named. The static shots are ux-workload-pods and
    // cluster-tab-node-detail-pods (a pod selected, so its row's logs icon shows),
    // the Events list with an event about a pod selected, and the stated "pod gone". The
    // ux- checks drive each list for real: the icon clicked at its edge, L, Shift+L, and
    // the hover reveal on an Argo Application's resource rows.
    ("cluster-tab-events-pod-logs", () => HostInMainWindow(ClusterTabScenarios.DemoEventsPodLogs())),
    ("cluster-tab-node-detail-pod-gone", () => HostInMainWindow(ClusterTabScenarios.NodeDetailPodGone(), height: 1000)),
    ("cluster-tab-pane-logs-narrow-workload",
        () => HostInMainWindow(ClusterTabScenarios.WorkloadDetail(), height: 1000, width: 860)),
    ("cluster-tab-pane-logs-narrow-node",
        () => HostInMainWindow(ClusterTabScenarios.NodeDetailPods(), height: 1000, width: 860)),
    ("cluster-tab-argo-resource-logs-hover",
        () => HostInMainWindow(ClusterTabScenarios.ArgoApplicationDetail(), height: 1000)),
    ("ux-pane-logs-workload", () => HostInMainWindow(ClusterTabScenarios.WorkloadDetail(), height: 1000)),
    ("ux-pane-logs-node", () => HostInMainWindow(ClusterTabScenarios.NodeDetailPods(), height: 1000)),
    ("ux-pane-logs-events", () => HostInMainWindow(ClusterTabScenarios.DemoEventsPodLogs())),
    ("ux-pane-logs-argo", () => HostInMainWindow(ClusterTabScenarios.ArgoApplicationDetail(), height: 1000)),
    ("cluster-tab-row-logs-narrow", () => HostInMainWindow(ClusterTabScenarios.DemoRowLogs(), width: 860)),
    ("cluster-tab-row-logs-deployments", () => HostInMainWindow(ClusterTabScenarios.DemoRowLogsDeployments())),
    ("cluster-tab-row-logs-none", () => HostInMainWindow(ClusterTabScenarios.DemoRowLogsNone())),
    ("cluster-tab-logs-maximized", () => HostInMainWindow(ClusterTabScenarios.DemoLogsMaximized())),
    ("ux-log-search-keys", () => HostInMainWindow(ClusterTabScenarios.DemoLogsMaximized())),
    ("ux-yaml-apply-key", () => HostInMainWindow(ClusterTabScenarios.YamlEditor())),
    // The macOS text keys (⌘↑ moving, not selecting) and the Edit menu's routing to the
    // focused control, forced on so they run on every OS (MacChecks).
    ("ux-mac-text-keys", () => HostInMainWindow(ClusterTabScenarios.YamlEditor())),
    ("ux-mac-edit-routing", () => HostInMainWindow(ClusterTabScenarios.YamlEditor())),
    ("ux-mac-edit-routing-exec", () => HostInMainWindow(ClusterTabScenarios.Exec())),
    ("palette-logs", () => LogsPalette(ClusterTabScenarios.DemoList(), "")),
    ("palette-logs-search", () => LogsPalette(ClusterTabScenarios.DemoList(), "report")),
    ("palette-logs-narrow", () => LogsPalette(ClusterTabScenarios.DemoList(), "", width: 560)),
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
    // The Applications mode: the list (every state it can be in), and the application
    // page for a crash-looping app, an app with no logs at all, a quiet one, and its two
    // armed actions. All on the demo cluster, read at DemoData.Now, so the relative
    // times in them are the same on every run.
    ("applications-list", () => HostInMainWindow(ApplicationsScenarios.List(), mode: ShellMode.Applications)),
    // The README's hero (design/screenshots/applications-list.*.png). At 1280×800 the
    // demo cluster's eleventh application sits half under the list's bottom edge once the
    // interface is drawn in Segoe UI, whose rows are taller than Inter's were, so the first
    // image a visitor sees ended on a clipped row. 880 shows every application with room
    // below the last one; the plain scenario stays at the size every other one uses.
    ("readme-applications-list", () => HostInMainWindow(ApplicationsScenarios.List(), height: 880, mode: ShellMode.Applications)),
    ("applications-list-attention", () => HostInMainWindow(ApplicationsScenarios.List(chip: ApplicationChip.NeedsAttention), mode: ShellMode.Applications)),
    ("applications-list-system", () => HostInMainWindow(ApplicationsScenarios.List(showSystem: true), mode: ShellMode.Applications)),
    ("applications-list-filtered-empty", () => HostInMainWindow(ApplicationsScenarios.List(filter: "zzz"), mode: ShellMode.Applications)),
    ("applications-namespace-picker", () => HostInMainWindow(ApplicationsScenarios.List(namespaces: ["payments", "monitoring"]), mode: ShellMode.Applications)),
    ("applications-list-sorted", () => HostInMainWindow(ApplicationsScenarios.List(namespaces: ["payments", "monitoring"], sort: ApplicationSortColumn.Restarts, descending: true), mode: ShellMode.Applications)),
    ("applications-row-menu", () => HostInMainWindow(ApplicationsScenarios.List(), mode: ShellMode.Applications)),
    ("applications-list-narrow", () => HostInMainWindow(ApplicationsScenarios.List(), width: 1024, mode: ShellMode.Applications)),
    ("applications-list-loading", () => HostInMainWindow(ApplicationsScenarios.Loading(), mode: ShellMode.Applications)),
    ("applications-list-rbac-fallback", () => HostInMainWindow(ApplicationsScenarios.RbacFallback(), mode: ShellMode.Applications)),
    ("applications-page-crashloop", () => HostInMainWindow(ApplicationsScenarios.Page("checkout"), mode: ShellMode.Applications)),
    ("applications-page-crashloop-merged", () => HostInMainWindow(ApplicationsScenarios.Page("checkout", merged: true), mode: ShellMode.Applications)),
    ("applications-page-no-logs", () => HostInMainWindow(ApplicationsScenarios.Page("fraud-detector"), mode: ShellMode.Applications)),
    ("applications-page-healthy", () => HostInMainWindow(ApplicationsScenarios.Page("redis-cache"), mode: ShellMode.Applications)),
    ("applications-page-rollout", () => HostInMainWindow(ApplicationsScenarios.Page("notification-dispatcher"), mode: ShellMode.Applications)),
    ("applications-page-selfheal", () => HostInMainWindow(ApplicationsScenarios.Page("checkout", editYaml: true), mode: ShellMode.Applications)),
    ("applications-page-restart", () => HostInMainWindow(ApplicationsScenarios.Page("checkout", restart: true), mode: ShellMode.Applications)),
    ("applications-page-sync", () => HostInMainWindow(ApplicationsScenarios.Page("checkout", sync: true), mode: ShellMode.Applications)),
    ("applications-page-sync-prune", () => HostInMainWindow(ApplicationsScenarios.Page("checkout", syncPrune: true), mode: ShellMode.Applications)),
    ("ux-applications-keys", () => HostInMainWindow(ApplicationsScenarios.List(), mode: ShellMode.Applications)),
    // VER-19 and ENG-20: keyboard contracts that need a real window (KeyboardChecks).
    ("ux-hotkey-scheme", () => BuildMainWindowContent()),
    ("ux-overlay-focus", () => BuildMainWindowContent()),
    // The preferences page's tabs: one height on every tab, the capsule strip unstyled
    // by the app, reopened on the last tab (UxInteractionChecks.PreferencesTabs).
    ("ux-preferences-tabs", () => BuildMainWindowContent()),
    ("ux-exec-keys", () => HostInMainWindow(ClusterTabScenarios.Exec())),
    // B2-5: a paste is filtered, bracketed when the shell asked, and armed when it did not (KeyboardChecks.ExecPaste).
    ("ux-exec-paste", () => HostInMainWindow(ClusterTabScenarios.Exec())),
    // ENG-4 / ENG-54: Tab and Shift+Tab round the whole window, a focus visual on every stop,
    // the dock's tabs and the sidebar as one stop each, and Ctrl+Tab out of the YAML editor
    // and the terminal (KeyboardChecks.KeyboardWalk, LeaveEditorKeys).
    ("ux-keyboard-walk", () => HostInMainWindow(ClusterTabScenarios.DemoDockTabs(), height: 1000)),
    // #297: Left/Right along a pane's segmented strip keep the focus ring (KeyboardChecks.SegmentedArrowFocus).
    ("ux-segmented-arrow-focus", () => HostInMainWindow(ClusterTabScenarios.PodDetailOverview(), height: 1000)),
    ("ux-leave-yaml-editor", () => HostInMainWindow(ClusterTabScenarios.YamlEditor())),
    ("ux-leave-exec-terminal", () => HostInMainWindow(ClusterTabScenarios.Exec())),
    // #266: a splitter drag gives focus back to the exec terminal (KeyboardChecks.DockSplitterKeepsFocus).
    ("ux-dock-splitter-focus", () => HostInMainWindow(ClusterTabScenarios.Exec())),
    // #269: ten open tabs stay on one row of the dock's strip at 1280px (LayoutChecks.DockTabsOneRow).
    ("cluster-tab-dock-many-tabs", () => HostInMainWindow(ClusterTabScenarios.DemoDockTabs(10))),
    // #236: UI Automation patterns on the sidebar headers, grid rows, menu items, namespace
    // boxes and cluster tabs; Delete on a focused dock tab; a refused managed-fields toggle
    // through UI Automation (AutomationPatternChecks).
    ("ux-automation-patterns", () => HostInMainWindow(ClusterTabScenarios.DemoList())),
    ("ux-dock-tab-delete", () => HostInMainWindow(ClusterTabScenarios.DemoDockTabs(), height: 1000)),
    ("ux-managed-fields-uia", () => HostInMainWindow(ClusterTabScenarios.YamlEditorManagedFields(shown: false))),
    // #267: a copy trims the blank cells at the end of each line (KeyboardChecks.ExecCopy).
    ("ux-exec-copy", () => HostInMainWindow(ClusterTabScenarios.Exec())),
    // #265: Ctrl+Shift+C pressed as a keyboard sends it, after Select all and after a drag (ClipboardChecks).
    ("ux-exec-copy-keys", () => HostInMainWindow(ClusterTabScenarios.Exec())),
    // #288: the log pane's ⋯ menu Copy, at the width that moves Copy into the menu (ClipboardChecks).
    ("ux-log-menu-copy-pod", () => HostInMainWindow(ClusterTabScenarios.DemoPodDetail(), height: 1000, width: 960)),
    ("cluster-tab-exec-paste-confirm", () => HostInMainWindow(ClusterTabScenarios.ExecPasteArmed())),
    // URLs and e-mail addresses in YAML are drawn as text, not AvaloniaEdit's blue links (EditorChecks).
    ("ux-yaml-editor-links", () => HostInMainWindow(ClusterTabScenarios.YamlEditor())),
    ("ux-helm-editor-links", () => HostInMainWindow(ClusterTabScenarios.HelmReleaseDetail())),
    ("main-window", () => BuildMainWindowContent()),
    ("main-window-no-kubeconfig", () => BuildNoKubeconfigContent()),
    ("main-window-shortcuts", () => BuildMainWindowContent(openShortcuts: true)),
    ("main-window-switcher", () => BuildSwitcherContent()),
    // "pro" is a subsequence of several of these and a prefix of others — the
    // ranking (prefix > contiguous > subsequence) is the point of the shot.
    ("main-window-switcher-search", () => BuildSwitcherContent("pro")),
    // #367: filtered and cleared again, every row is named and bounded where it is drawn.
    ("ux-switcher-filter-uia", () => BuildSwitcherContent()),

    // Preferences and About, which are overlays over the shell now rather than
    // windows of their own. Rendering them is not really about the picture: the
    // harness is CI's one check that a view still loads at all (a stale avares://
    // URI or a DataTemplate that stopped resolving compiles perfectly), and these
    // two views are loaded from nowhere else.
    ("main-window-preferences", () => BuildMainWindowContent(openPreferences: true)),
    // The page opens on its General tab; these are its other three tabs. The Logs and
    // metrics tab is where L2's "Open logs maximized" switch sits.
    ("main-window-preferences-appearance", () => BuildMainWindowContent(openPreferences: true, preferencesTab: 1)),
    ("main-window-preferences-logs", () => BuildMainWindowContent(openPreferences: true, preferencesTab: 2)),
    ("main-window-preferences-changes", () => BuildMainWindowContent(openPreferences: true, preferencesTab: 3)),
    ("main-window-about", () => BuildMainWindowContent(openAbout: true)),
    // The interface and code faces change open text from the page (FontChecks, rule 22).
    ("ux-font-settings", () => BuildMainWindowContent(openPreferences: true)),

    // The Microsoft Store listing's screenshots (design/store/screenshots). The Store asks
    // for 1366×768 or larger and every scenario above is 1280 wide, so these are the same
    // fixtures at 1920×1080 — which also makes them the harness's only look at a wide
    // window, where a star column that should absorb the width and does not shows up.
    ("store-applications-list", () => HostInMainWindow(ApplicationsScenarios.List(), width: 1920, height: 1080, mode: ShellMode.Applications)),
    ("store-applications-page", () => HostInMainWindow(ApplicationsScenarios.Page("checkout"), width: 1920, height: 1080, mode: ShellMode.Applications)),
    ("store-pod-detail", () => HostInMainWindow(ClusterTabScenarios.PodDetail(), width: 1920, height: 1080)),
    ("store-yaml-editor", () => HostInMainWindow(ClusterTabScenarios.YamlEditorMaximized(), width: 1920, height: 1080)),
    ("store-fleet-list", () => HostInMainWindow(ClusterTabScenarios.FleetList(), width: 1920, height: 1080)),
    ("store-cluster-switcher", () => BuildSwitcherContent(width: 1920, height: 1080)),
    ("store-exec-terminal", () => HostInMainWindow(ClusterTabScenarios.ExecFullScreenMaximized(), width: 1920, height: 1080)),
    ("store-rbac-who-can", () => HostInMainWindow(ClusterTabScenarios.RbacWhoCan(), width: 1920, height: 1080)),
    // FEAT-17 / FEAT-27: the hand-off to the machine's terminal, opened from the exec pane, and
    // node detail's node shell refused for want of kubectl.
    ("cluster-tab-exec-handoff", () => HostInMainWindow(ClusterTabScenarios.ExecHandoff())),
    ("cluster-tab-node-shell-no-kubectl", () => HostInMainWindow(ClusterTabScenarios.NodeShellNoKubectl(), height: 1000)),
};

ShardPlan.Current.Verify(scenarios.Select(s => s.Name).ToList());
var selected = 0;
for (var position = 0; position < scenarios.Length; position++)
{
    var (name, build) = scenarios[position];
    if (!ShardPlan.Current.Contains(position)
        || (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
    {
        continue;
    }

    selected++;

    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        Capture(name, theme, build);

        // #360, with --twice: an interaction scenario is rendered a second time and must come out
        // byte-identical.
        if (HarnessDeterminism.RendersTwice(name))
        {
            Capture(name, theme, build, again: true);
        }
    }
}

TooltipChecks.ThrowIfAnyDead(filtered: filter is not null);
AutomationChecks.ThrowIfAnyFailed(filtered: filter is not null);
FontChecks.ThrowIfAnyFailed(filtered: filter is not null);
SelectionChecks.ThrowIfAnyFailed(filtered: filter is not null);
ClockChecks.ThrowIfAnyFailed(filtered: filter is not null);
HarnessDeterminism.ThrowIfAnyDiffered();
HarnessTeardown.ThrowIfWindowsLeaked();
HarnessTiming.Report();
if (ShardPlan.Current.IsSharded)
{
    Console.WriteLine($"Shard {ShardPlan.Current.Number} of {ShardPlan.Current.Count}: {selected} of {scenarios.Length} scenarios.");
}

Console.WriteLine($"Wrote screenshots to {Path.GetFullPath(outDir)}");
try
{
    Directory.Delete(scratch, recursive: true);
}
catch (IOException)
{
    // Best effort: a file still held open leaves a few KB in %TEMP%, not a failed run.
}
return 0;

void Capture(string name, ThemeVariant theme, Func<Control> build, bool again = false)
{
    var themeLabel = theme == ThemeVariant.Dark ? "dark" : "light";
    var timing = new HarnessTiming.Capture(name, themeLabel);
    Application.Current!.RequestedThemeVariant = theme;
    timing.Phase("theme");

    // Every capture starts from the shipped defaults: no settings.json, no workspace.json.
    // Anything a scenario persists would otherwise reach every scenario rendered after it,
    // so a PNG depended on which scenarios ran before it — and a full run disagreed with a
    // run of that one scenario. It happened three times before the reset covered both whole
    // files: per-kind column widths and sort orders (a scenario that seeds one, see
    // SortedList, reached every later list of the same kind), the sidebar's Recent kinds
    // (every demo scenario is the same cluster, so selecting Deployments put a Recent
    // section into every demo shot after it), and the expanded sidebar sections
    // (cluster-tab-events-list expands Config on a demo tab, whose sections persist their
    // expansion, and every later scenario — the published store-* set among them —
    // rendered Config open, where the default is collapsed). Deleting the files rather
    // than clearing named fields means the next persisted preference cannot leak the same
    // way. Here rather than in the scenario, because the view reads some of this state
    // while the window is laid out, which is after the builder has returned.
    File.Delete(Path.Combine(scratch, "settings.json"));
    File.Delete(Path.Combine(scratch, "workspace.json"));

    // The faces are application resources rather than something read from the file per
    // window, so a scenario that changes one would otherwise reach every later capture.
    // Only when one did: every write to an application resource is announced to every top
    // level, the windows already closed but not yet collected among them, and re-applying
    // unchanged faces before each capture was a few seconds of a full run (HarnessTeardown).
    HarnessTeardown.ResetFonts();
    timing.Phase("reset");
    var content = build();
    timing.Phase("build");
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
    timing.Phase("show");

    if (name == "ux-namespace-picker") UxInteractionChecks.NamespacePicker(window);
    if (name == "cluster-tab-namespace-picker-several") UxInteractionChecks.NamespacePickerSeveral(window);
    if (name == "ux-applications-keys") ApplicationsChecks.Keys(window);
    if (name == "ux-workload-pods-sort") GridSortChecks.WorkloadPods(window);
    if (name == "ux-node-pods-sort") GridSortChecks.NodePods(window);
    if (name == "ux-helm-sort") GridSortChecks.ClusterTabGrid(window, "HelmGrid", "Rev", HelmReleaseComparer.Revision);
    if (name == "ux-argo-sort") GridSortChecks.ClusterTabGrid(window, "ArgoGrid", "Health", ArgoApplicationComparer.Health);
    if (name == "applications-namespace-picker") ApplicationsChecks.OpenNamespacePicker(window);
    if (name == "applications-row-menu") ApplicationsChecks.RowMenu(window);
    if (name == "ux-port-forwards-row-menu") RowMenuChecks.PortForwards(window);
    if (name == "ux-helm-row-menu") RowMenuChecks.HelmReleases(window);
    if (name == "ux-argo-resources-row-menu") RowMenuChecks.ArgoResources(window);
    if (name == "ux-hotkey-scheme") KeyboardChecks.HotkeyScheme(window);
    if (name == "ux-overlay-focus") UxInteractionChecks.OverlayTakesFocus(window);
    if (name == "ux-preferences-tabs") UxInteractionChecks.PreferencesTabs(window);
    if (name is "cluster-tab-list-unhealthy-fleet-partial-narrow" or "cluster-tab-list-unhealthy-narrow")
        LayoutChecks.ListHeaderFits(window);
    if (name == "palette-logs-narrow") LayoutChecks.PaletteFollowsWindow(window);
    if (name.StartsWith("cluster-tab-row-action-", StringComparison.Ordinal)
        && name is not ("cluster-tab-row-action-scaled" or "cluster-tab-row-action-busy"))
        LayoutChecks.ActionStripReadsAsOneBlock(window);
    if (name.StartsWith("cluster-tab-fleet-list", StringComparison.Ordinal)
        || name == "cluster-tab-list-unhealthy-fleet-partial-narrow")
        LayoutChecks.GridReachesLastColumn(window);
    if (name == "ux-exec-keys") KeyboardChecks.ExecKeys(window);
    if (name == "ux-exec-paste") KeyboardChecks.ExecPaste(window);
    if (name == "ux-keyboard-walk") KeyboardChecks.KeyboardWalk(window);
    if (name == "ux-segmented-arrow-focus") KeyboardChecks.SegmentedArrowFocus(window);
    if (name is "ux-leave-yaml-editor" or "ux-leave-exec-terminal") KeyboardChecks.LeaveEditorKeys(window);
    if (name == "ux-exec-copy") KeyboardChecks.ExecCopy(window);
    if (name == "ux-exec-copy-keys") ClipboardChecks.ExecCopyRealKeys(window);
    if (name == "ux-dock-splitter-focus") KeyboardChecks.DockSplitterKeepsFocus(window);
    if (name == "cluster-tab-dock-many-tabs")
    {
        LayoutChecks.DockTabsOneRow(window);
        AutomationPatternChecks.DockStripWheel(window);
    }

    if (name == "ux-automation-patterns") AutomationPatternChecks.Patterns(window);
    if (name == "cluster-tab-crd-printer-columns-960") PaneTextChecks.CrdHeadersAt960(window);
    if (name == "ux-crd-column-width-kept") ColumnWidthChecks.CrdWidthKeptAcrossKinds(window);
    if (name == "cluster-tab-pod-detail-usage-docked") PaneTextChecks.UsageFirstContainerVisible(window);
    if (name == "ux-dock-tab-delete") AutomationPatternChecks.DockTabDelete(window);
    if (name == "ux-managed-fields-uia") AutomationPatternChecks.ManagedFieldsToggle(window);
    if (name == "cluster-tab-exec") LayoutChecks.ExecHasNoStatusDot(window);
    // #215 / #237: one grouping device per group, nothing boxed inside the dock's card.
    if (name is "cluster-tab-node-detail-cordoned" or "cluster-tab-pod-detail-overview" or "cluster-tab-pod-detail"
        or "cluster-tab-exec" or "cluster-tab-yaml-diff-preview" or "net-service-overview" or "cluster-tab-argo-appproject-detail")
        LayoutChecks.OneGroupingDevice(window);
    if (name == "main-window-switcher") LayoutChecks.SwitcherPinsOnlyWhereUsed(window);
    if (name == "ux-switcher-filter-uia") AutomationPatternChecks.SwitcherAfterFilter(window);
    if (name == "ux-log-search-keys") KeyboardChecks.LogSearchKeys(window);
    if (name == "ux-yaml-apply-key") KeyboardChecks.YamlApplyKey(window);
    if (name == "ux-mac-text-keys") MacChecks.TextKeys(window);
    if (name == "ux-mac-edit-routing") MacChecks.EditRouting(window);
    if (name == "ux-mac-edit-routing-exec") MacChecks.EditRoutingInTerminal(window);
    if (name is "cluster-tab-exec" or "cluster-tab-port-forward" or "cluster-tab-workloads-list" or "palette-logs")
        LayoutChecks.OneLineTextBoxesCentred(window);
    if (name == "ux-yaml-editor-links") EditorChecks.YamlEditorLinks(window);
    if (name == "ux-helm-editor-links") EditorChecks.HelmReleaseLinks(window);
    if (name.StartsWith("applications-page", StringComparison.Ordinal) || name == "store-applications-page") ApplicationsChecks.SettlePage(window);
    if (name == "ux-unhealthy-toggle") UxInteractionChecks.UnhealthyToggle(window);
    if (name == "ux-logs-palette") UxInteractionChecks.LogsPalette(window);
    if (name == "ux-row-logs") UxInteractionChecks.RowLogs(window);
    if (name == "ux-pane-logs-workload") PaneLogsChecks.WorkloadDetail(window);
    if (name == "ux-pane-logs-node") PaneLogsChecks.NodeDetail(window);
    if (name == "cluster-tab-pane-logs-narrow-node") PaneLogsChecks.NodePodsFit(window);
    if (name == "ux-pane-logs-events") PaneLogsChecks.Events(window);
    if (name == "ux-pane-logs-argo") PaneLogsChecks.Argo(window);
    if (name == "cluster-tab-argo-resource-logs-hover") PaneLogsChecks.HoverArgoRow(window, "Deployment");
    if (name.StartsWith("cluster-tab-row-logs", StringComparison.Ordinal)) UxInteractionChecks.HoverRow(window, 3);
    if (name == "ux-font-settings") FontChecks.SettingsReachOpenText(window);
    if (name == "ux-bulk-delete") ListCreateChecks.BulkDelete(window);
    if (name == "cluster-tab-bulk-delete-partial") ListCreateChecks.BulkRefusalWraps(window);
    if (name == "ux-column-chooser") ListCreateChecks.ColumnChooser(window);
    if (name == "ux-column-chooser-keyboard") ListCreateChecks.ColumnChooserKeyboard(window);

    // Last, so a pane a check just opened settles too: capture when the log streams have
    // stopped moving, not whenever the builder happened to return (ENG-10).
    timing.Phase("checks");
    LogSettle.Run(window);
    timing.Phase("settle");

    // After the settle, so every line has arrived before the pane is scrolled to its end.
    if (name.StartsWith("ux-log-end-gap", StringComparison.Ordinal)) LayoutChecks.LogEndClearsScrollBar(window);

    // After the settle too: the error and warning counts join the log bar once lines arrive.
    if (name.StartsWith("ux-log-bar-", StringComparison.Ordinal)) LayoutChecks.LogBarFits(window);

    // After the settle as well: Copy moves into the ⋯ menu only once the counts have widened the bar.
    if (name == "ux-log-menu-copy-pod") ClipboardChecks.LogMenuCopy(window);

    // Every scenario, once: the pointer over each tooltip has to reach its element
    // (DESIGN.md rule 21). Reported together after the last scenario.
    // Once per scenario: the second render of an interaction scenario (#360) is only compared.
    var walk = theme == ThemeVariant.Light && !again;
    if (walk) TooltipChecks.Reach(window, name);
    if (walk) AutomationChecks.Walk(window, name);
    if (walk) FontChecks.Walk(window, name);
    if (walk) SelectionChecks.Walk(window, name);
    if (walk) ClockChecks.Walk(window, name);
    timing.Phase("walks");
    HarnessDeterminism.RepaintWhole(window);
    using var frame = window.CaptureRenderedFrame();
    timing.Phase("render");
    var path = Path.Combine(outDir, $"{name}.{themeLabel}.png");
    if (again)
    {
        using var second = new MemoryStream();
        frame?.Save(second, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        HarnessDeterminism.Compare(path, File.ReadAllBytes(path), second.ToArray());
        HarnessTeardown.Close(window);
        timing.End();
        return;
    }

    frame?.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    HarnessTeardown.Close(window);
    timing.Phase("save");
    timing.End();
    Console.WriteLine(frame is null ? $"FAILED (no frame): {path}" : $"Wrote {path}");
}

// The port-forwards list needs the window's own registry, so it is filled after the window exists.
static Control PortForwardsListWindow()
{
    var tab = ClusterTabScenarios.WorkloadsList();
    var window = (Window)HostInMainWindow(tab);
    ClusterTabScenarios.PortForwardsList(tab, ((MainWindowViewModel)window.DataContext!).PortForwards);
    return window;
}

// Hosts a single cluster tab inside a real MainWindow (command bar, tab strip,
// status bar) rather than a bare wrapper Border: ContentControl's implicit
// DataTemplate lookup walks the visual tree for a Window.DataTemplates match
// (see MainWindow.axaml), so an inspector tab only renders its real View —
// PodDetailView/YamlEditorView/etc — when hosted under the actual MainWindow.
// A bare wrapper falls back to a "ToString() in a TextBlock" placeholder.
static Control HostInMainWindow(ClusterTabViewModel tab, int height = 800, int width = 1280, ShellMode mode = ShellMode.Resources)
{
    var window = new MainWindow { Width = width, Height = height };
    var vm = new MainWindowViewModel();
    window.DataContext = vm;
    SeedContexts(vm);

    // Every cluster-tab scenario predates the Applications mode and is about the
    // Resources explorer; the Applications scenarios ask for their own mode.
    vm.Mode = mode;

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

    // Below the shell's 960px minimum only on purpose: palette-logs-narrow asks for 560 to show
    // the palette following the window (ENG-35), which the minimum would otherwise hide by
    // quietly rendering at 960 — what happened to this scenario when it asked for 800.
    window.MinWidth = Math.Min(window.MinWidth, width);
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
    // What LoadContextsAsync sets for this state: the card's heading and the status bar
    // are two properties now (ENG-29), and this shot is where they are seen together.
    vm.KubeconfigSearchPathCount = 1;
    vm.KubeconfigDiagnosis = "No kubeconfig contexts found.";
    vm.Status = "No clusters: the kubeconfig location searched does not exist.";
    vm.KubeconfigSearchPaths = string.Join(
        System.Environment.NewLine,
        "missing  C:\\Users\\reviewer\\.kube\\config   (default location)");

    return window;
}

static Control BuildMainWindowContent(bool openShortcuts = false, bool openPreferences = false, bool openAbout = false, int preferencesTab = 0)
{
    var window = new MainWindow();
    var vm = new MainWindowViewModel();
    window.DataContext = vm;
    window.Width = 1280;
    window.Height = 800;
    SeedContexts(vm);
    vm.Mode = ShellMode.Resources;

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
    vm.PreferencesTab = preferencesTab;
    vm.IsPreferencesOpen = openPreferences;
    vm.IsAboutOpen = openAbout;

    return window;
}

// The cluster switcher, open. `query` renders the searching state — one flat
// ranked list — against the grouped Open/Pinned/All layout of the empty query.
static Control BuildSwitcherContent(string? query = null, int width = 1280, int height = 800)
{
    var window = new MainWindow();
    var vm = new MainWindowViewModel();
    window.DataContext = vm;
    window.Width = width;
    window.Height = height;
    SeedContexts(vm);
    vm.Mode = ShellMode.Resources;

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
    else
    {
        // The selected row is an unpinned one (#287), so the shot carries all three pin
        // states FEAT-71 draws: pinned, unpinned and hidden, unpinned and shown because
        // the row is selected. LayoutChecks.SwitcherPinsOnlyWhereUsed needs the third.
        vm.Switcher.SelectedItem = vm.Switcher.Ordered.FirstOrDefault(i => !i.IsPinned && !i.IsCurrent)
            ?? vm.Switcher.SelectedItem;
    }

    return window;
}

static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .WithNimbusFonts();
