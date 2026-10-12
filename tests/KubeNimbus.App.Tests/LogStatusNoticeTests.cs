using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #319: the snapshot, Previous and Cleared messages stay on screen while the pane holds
/// lines. They used to be drawn only as the empty pane's placeholder, so a snapshot loaded
/// over earlier lines looked exactly like a live tail (found by the 0.7.0 QA run, G-7/G-8).
/// </summary>
public class LogStatusNoticeTests
{
    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: []);

    private static DynamicResource Object(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static PodDetailTabViewModel Detail()
    {
        TestObjects.RedirectStores();
        var pod = Object("""
            {
              "apiVersion": "v1", "kind": "Pod",
              "metadata": { "name": "checkout-1", "namespace": "payments" },
              "spec": { "nodeName": "worker-1", "containers": [ { "name": "model-server" } ] },
              "status": { "phase": "Running" }
            }
            """);
        return new PodDetailTabViewModel(null, new ResourceRowViewModel(pod), _ => { }, (_, _) => Task.CompletedTask);
    }

    private static WorkloadLogsTabViewModel Pane()
    {
        TestObjects.RedirectStores();
        var workload = Object("""
            {
              "apiVersion": "apps/v1", "kind": "Deployment",
              "metadata": { "name": "api", "namespace": "nowhere" },
              "spec": { "selector": { "matchLabels": { "app": "api-nothing-matches-this" } } }
            }
            """);
        return new WorkloadLogsTabViewModel(null, DeploymentDescriptor, workload, LabelSelector.ForPodsOf(workload)!);
    }

    [Test]
    public async Task Pod_detail_says_a_snapshot_is_a_snapshot_while_it_shows_lines()
    {
        var detail = Detail();
        detail.Enqueue("2026-08-17T10:00:01.000Z GET /healthz 200");
        detail.FlushLogLines();
        await Assert.That(detail.LogLines.Count).IsEqualTo(1);
        await Assert.That(detail.HasLogStatusNotice).IsFalse();

        detail.LogStatus = "Previous logs loaded — this is a snapshot, not a live stream.";

        await Assert.That(detail.HasLogPlaceholder).IsFalse();
        await Assert.That(detail.LogStatusNotice).IsEqualTo("Previous logs loaded — this is a snapshot, not a live stream.");
    }

    [Test]
    public async Task Pod_detail_keeps_saying_lines_were_cleared_once_new_ones_arrive()
    {
        var detail = Detail();
        detail.Enqueue("2026-08-17T10:00:01.000Z first");
        detail.Enqueue("2026-08-17T10:00:02.000Z second");
        detail.FlushLogLines();
        detail.ClearLogsCommand.Execute(null);

        // Empty: the placeholder says it, and the bar does not repeat it (UI rule 20).
        await Assert.That(detail.LogPlaceholder!).StartsWith("Cleared 2 lines");
        await Assert.That(detail.HasLogStatusNotice).IsFalse();

        detail.Enqueue("2026-08-17T10:00:03.000Z third");
        detail.FlushLogLines();

        await Assert.That(detail.HasLogPlaceholder).IsFalse();
        await Assert.That(detail.LogStatusNotice).IsEqualTo("Cleared 2 lines; the lines shown arrived since.");
    }

    [Test]
    public async Task The_multi_pod_pane_says_a_snapshot_is_a_snapshot_while_it_shows_lines()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        var b = pane.RegisterSource("api-b", "app");
        pane.Enqueue("2026-08-17T10:00:01.000Z GET /healthz 200", a);
        pane.Flush(force: true);
        await Assert.That(pane.HasLogStatusNotice).IsFalse();

        a.State = LogSourceState.Loaded;
        a.StatusMessage = "Selected range loaded — snapshot, not a live stream.";
        await Assert.That(pane.HasLogStatusNotice).IsFalse(); // api-b is still streaming

        b.State = LogSourceState.Loaded;
        b.StatusMessage = a.StatusMessage;
        await Assert.That(pane.LogStatusNotice).IsEqualTo("Selected range loaded — snapshot, not a live stream.");
        await Assert.That(pane.HasLogPlaceholder).IsFalse();
    }

    [Test]
    public async Task The_multi_pod_pane_keeps_saying_lines_were_cleared_once_new_ones_arrive()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        pane.Enqueue("2026-08-17T10:00:01.000Z first", a);
        pane.Flush(force: true);
        pane.ClearLogsCommand.Execute(null);
        await Assert.That(pane.HasLogStatusNotice).IsFalse();

        pane.Enqueue("2026-08-17T10:00:02.000Z second", a);
        pane.Flush(force: true);

        await Assert.That(pane.LogStatusNotice).IsEqualTo("Cleared 1 line; the lines shown arrived since.");
    }
}
