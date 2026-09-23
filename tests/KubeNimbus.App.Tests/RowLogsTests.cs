using System.Text.Json;
using Avalonia.Input;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Tests;

/// <summary>
/// L2 — one click to logs from the row, and logs opened full-size: which rows carry the
/// logs icon, that the icon, Shift+L and the "Open logs maximized" preference all open
/// logs through the one shared entry point, and that "maximized" is decided there and
/// nowhere else.
///
/// <para>
/// The icon's visibility on hover/selection is a style (<c>Button.rowAction</c>) and the
/// click is a pointer gesture, so both are driven for real by the screenshot harness's
/// <c>ux-row-logs</c> check; what is pinned here is the rule underneath them.
/// </para>
///
/// <para>
/// <c>[NotInParallel]</c> because the preference tests write <c>settings.json</c> behind
/// the process-global <c>AppSettingsStore.DirectoryOverride</c>; another test redirecting
/// it between the write and the open would read the preference from somewhere else — the
/// same reason <c>ClusterTabSortTests</c> carries the attribute.
/// </para>
/// </summary>
[NotInParallel]
public class RowLogsTests
{
    private static readonly ResourceDescriptor Deployments =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: []);

    private static DynamicResource Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static DynamicResource Workload(string apiVersion, string kind, string selector) => Parse($$"""
        {
          "apiVersion": "{{apiVersion}}",
          "kind": "{{kind}}",
          "metadata": { "name": "web", "namespace": "shop", "uid": "web" },
          "spec": { "selector": {{selector}} }
        }
        """);

    /// <summary>
    /// Turns the preference on for one test and off again after it. Not left to
    /// <see cref="TestObjects.RedirectStores"/>: <c>App</c>'s settings store resolves its
    /// path once, on first use, so every test in the process shares one settings.json and
    /// a preference left on would leak into the next test that opens logs.
    /// </summary>
    private static async Task WithOpenLogsMaximized(Func<Task> body)
    {
        App.Update(s => s with { OpenLogsMaximized = true });
        try
        {
            await body();
        }
        finally
        {
            App.Update(s => s with { OpenLogsMaximized = false });
        }
    }

    private static ClusterTabViewModel DemoTab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        return tab;
    }

    // ------------------------------------------------------------- which rows get it

    [Test]
    public async Task A_pod_row_has_logs()
    {
        await Assert.That(new ResourceRowViewModel(TestObjects.Pod("shop", "web-1")).HasLogs).IsTrue();
    }

    [Test]
    [Arguments("apps/v1", "Deployment", """{ "matchLabels": { "app": "web" } }""")]
    [Arguments("apps/v1", "StatefulSet", """{ "matchLabels": { "app": "db" } }""")]
    [Arguments("apps/v1", "DaemonSet", """{ "matchExpressions": [ { "key": "app", "operator": "Exists" } ] }""")]
    [Arguments("v1", "Service", """{ "app": "web" }""")]
    public async Task Anything_that_names_its_pods_has_logs(string apiVersion, string kind, string selector)
    {
        // The same evidence L and "Logs (all pods)" are gated on — never a list of kinds.
        await Assert.That(new ResourceRowViewModel(Workload(apiVersion, kind, selector)).HasLogs).IsTrue();
    }

    [Test]
    public async Task A_workload_with_an_empty_selector_has_no_logs()
    {
        // An empty selector means "every pod" to Kubernetes and is refused by
        // LabelSelector.ForPodsOf; an icon that then did nothing would be worse than none.
        await Assert.That(new ResourceRowViewModel(Workload("apps/v1", "Deployment", "{}")).HasLogs).IsFalse();
    }

    [Test]
    public async Task Kinds_without_logs_never_get_the_icon()
    {
        var configMap = Parse("""
            { "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "settings", "namespace": "shop" }, "data": { "a": "b" } }
            """);
        var node = Parse("""
            { "apiVersion": "v1", "kind": "Node", "metadata": { "name": "node-1" }, "spec": { "podCIDR": "10.0.0.0/24" } }
            """);

        // Named "Pod", served by some other API group: not a pod, and no selector.
        var impostor = Parse("""
            { "apiVersion": "example.com/v1", "kind": "Pod", "metadata": { "name": "p", "namespace": "shop" }, "spec": {} }
            """);

        await Assert.That(new ResourceRowViewModel(configMap).HasLogs).IsFalse();
        await Assert.That(new ResourceRowViewModel(node).HasLogs).IsFalse();
        await Assert.That(new ResourceRowViewModel(impostor).HasLogs).IsFalse();
    }

    [Test]
    public async Task The_rule_follows_the_object_on_an_update()
    {
        var row = new ResourceRowViewModel(Workload("apps/v1", "Deployment", """{ "matchLabels": { "app": "web" } }"""));

        row.Update(Workload("apps/v1", "Deployment", "{}"));

        await Assert.That(row.HasLogs).IsFalse();
    }

    [Test]
    public async Task The_demo_list_gives_every_pod_the_icon()
    {
        var tab = DemoTab();

        await Assert.That(tab.Rows.Count).IsGreaterThan(0);
        await Assert.That(tab.Rows.All(r => r.HasLogs)).IsTrue();
    }

    // ---------------------------------------------------------------- the row's icon

    [Test]
    public async Task Clicking_the_icon_selects_its_row_and_opens_its_logs_in_the_split()
    {
        var tab = DemoTab();
        var row = tab.Rows[^1];

        await tab.OpenRowLogsAsync(row);

        await Assert.That(tab.SelectedRow).IsSameReferenceAs(row);
        var detail = tab.SelectedInspectorTab as PodDetailTabViewModel;
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.PodName).IsEqualTo(row.Name);
        await Assert.That(detail.SelectedDetailTabIndex).IsEqualTo(0);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();
    }

    [Test]
    public async Task Shift_clicking_the_icon_opens_the_same_tab_maximized()
    {
        var tab = DemoTab();
        var row = tab.Rows[0];
        await tab.OpenRowLogsAsync(row);
        var split = tab.SelectedInspectorTab;

        await tab.OpenRowLogsAsync(row, maximized: true);

        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(split);
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(1);
        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    }

    [Test]
    public async Task The_icon_does_nothing_on_a_row_without_logs()
    {
        var tab = DemoTab();
        var configMap = new ResourceRowViewModel(Parse("""
            { "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "settings", "namespace": "shop" } }
            """));

        await tab.OpenRowLogsAsync(configMap, maximized: true);

        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(0);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();
    }

    // -------------------------------------------------------------------- Shift+L

    [Test]
    public async Task Shift_L_opens_the_pods_logs_maximized_and_L_does_not()
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows[0];

        tab.OpenLogsCommand.Execute(null);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();
        var fromL = tab.SelectedInspectorTab;

        await Assert.That(tab.OpenLogsMaximizedCommand.CanExecute(null)).IsTrue();
        tab.OpenLogsMaximizedCommand.Execute(null);

        await Assert.That(tab.IsInspectorMaximized).IsTrue();
        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(fromL);
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Shift_L_on_a_workload_opens_its_one_stream_pane_maximized()
    {
        var tab = DemoTab();
        var deployment = Demo.DemoData.Deployments[0];

        await tab.OpenLogsForAsync(new LogTarget(deployment, Deployments, "", null), maximized: true);

        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<WorkloadLogsTabViewModel>();
        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    }

    [Test]
    public async Task Shift_L_is_unavailable_without_a_row_that_has_logs()
    {
        var tab = DemoTab();
        tab.SelectedRow = null;

        await Assert.That(tab.OpenLogsMaximizedCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task Nothing_opened_means_nothing_maximized()
    {
        // A workload whose selector names nothing opens no pane; maximizing an inspector
        // over whatever else was open would be a dock change nobody asked for.
        var tab = DemoTab();

        await tab.OpenLogsForAsync(
            new LogTarget(Workload("apps/v1", "Deployment", "{}"), Deployments, "", null), maximized: true);

        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(0);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();
    }

    [Test]
    public async Task The_list_key_tells_Shift_L_from_L()
    {
        // CommandBindings.Matches compares modifiers exactly, so Shift+L can never also
        // run L's command (which would open the split and then maximize it — or not).
        var shiftL = new KeyEventArgs { Key = Key.L, KeyModifiers = KeyModifiers.Shift };
        var plainL = new KeyEventArgs { Key = Key.L, KeyModifiers = KeyModifiers.None };

        await Assert.That(CommandBindings.Matches(CommandId.PodLogsMaximized, shiftL)).IsTrue();
        await Assert.That(CommandBindings.Matches(CommandId.PodLogs, shiftL)).IsFalse();
        await Assert.That(CommandBindings.Matches(CommandId.PodLogsMaximized, plainL)).IsFalse();
    }

    // ---------------------------------------------------------------- the preference

    [Test]
    public async Task With_the_preference_on_L_opens_logs_maximized() => await WithOpenLogsMaximized(async () =>
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows[0];

        tab.OpenLogsCommand.Execute(null);

        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<PodDetailTabViewModel>();
        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    });

    [Test]
    public async Task The_preference_is_read_when_logs_open_not_when_the_tab_was_made()
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows[0];
        tab.OpenLogsCommand.Execute(null);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();

        await WithOpenLogsMaximized(async () =>
        {
            tab.RequestLogTargets();
            tab.LogTargetRows.First(r => r.Title == $"Logs: {tab.Rows[1].Name}").Execute!();

            // The palette's rows go through the same door, so they follow it too.
            await Assert.That(tab.IsInspectorMaximized).IsTrue();
        });
    }

    [Test]
    public async Task The_row_icon_follows_the_preference_on_a_plain_click() => await WithOpenLogsMaximized(async () =>
    {
        var tab = DemoTab();

        await tab.OpenRowLogsAsync(tab.Rows[0]);

        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    });

    [Test]
    public async Task Opening_logs_never_takes_a_maximized_inspector_back_to_the_split()
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows[0];
        tab.EditSelectedYamlCommand.Execute(null);
        tab.IsInspectorMaximized = true;

        tab.OpenLogsCommand.Execute(null);

        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<PodDetailTabViewModel>();
        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    }

    [Test]
    public async Task Closing_the_last_tab_returns_the_dock_to_the_split()
    {
        var tab = DemoTab();
        await tab.OpenRowLogsAsync(tab.Rows[0], maximized: true);

        await tab.CloseInspectorTabCommand.ExecuteAsync(tab.SelectedInspectorTab!);

        await Assert.That(tab.IsInspectorMaximized).IsFalse();
    }
}
