using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App.Tests;

/// <summary>
/// "Everything" against a pod with a long history: one flush receives far more lines than
/// the scrollback keeps. The pane used to parse, show and then trim every one of them, the
/// trim a <c>RemoveAt(0)</c> per line on the rendered collection — quadratic, on the UI
/// thread, and it froze the window. What is pinned here is the outcome (the newest lines,
/// exactly the cap, the notice) and the shape of the work (a handful of collection
/// notifications per flush, not one per line).
/// </summary>
public class LogBurstTests
{
    private const int Cap = AppSettings.DefaultLogBufferLines;

    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: []);

    private static DynamicResource Object(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
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

    private static PodDetailTabViewModel Detail()
    {
        TestObjects.RedirectStores();
        var pod = Object("""
            {
              "apiVersion": "v1", "kind": "Pod",
              "metadata": { "name": "checkout-7f9c-x7k2m", "namespace": "payments",
                            "annotations": { "kubectl.kubernetes.io/default-container": "model-server" } },
              "spec": { "nodeName": "worker-1", "containers": [ { "name": "istio-proxy" }, { "name": "model-server" } ] },
              "status": { "phase": "Running" }
            }
            """);
        return new PodDetailTabViewModel(null, new ResourceRowViewModel(pod), _ => { }, (_, _) => Task.CompletedTask);
    }

    private static string Stamp(int i) =>
        new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(i).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    [Test]
    public async Task A_burst_far_past_the_cap_keeps_the_newest_lines_with_a_few_notifications()
    {
        var pane = Pane();
        var source = pane.RegisterSource("api-a", "app");
        pane.Enqueue($"{Stamp(0)} before the burst", source);
        pane.Flush(force: true);

        var notifications = 0;
        pane.LogLines.CollectionChanged += (_, _) => notifications++;

        const int burst = 200_000;
        for (var i = 1; i <= burst; i++)
        {
            pane.Enqueue($"{Stamp(i)} line {i}", source);
        }

        var clock = Stopwatch.StartNew();
        pane.Flush(force: true);
        clock.Stop();

        await Assert.That(pane.LogLines.Count).IsEqualTo(Cap);
        await Assert.That(pane.LogLines[0].Message).IsEqualTo($"line {burst - Cap + 1}");
        await Assert.That(pane.LogLines[^1].Message).IsEqualTo($"line {burst}");
        await Assert.That(pane.TrimNotice!).Contains("Older lines were trimmed");
        await Assert.That(source.LineCount).IsEqualTo(burst + 1);
        await Assert.That(notifications).IsLessThanOrEqualTo(2);
        // Generous: the quadratic version took minutes here, the linear one milliseconds.
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Dropping_a_chatty_pods_backlog_does_not_drop_a_quiet_pods_newer_lines()
    {
        var pane = Pane();
        var chatty = pane.RegisterSource("api-a", "app");
        var quiet = pane.RegisterSource("api-b", "app");

        for (var i = 0; i < Cap * 3; i++)
        {
            pane.Enqueue($"{Stamp(i)} chatty {i}", chatty);
        }

        // Later than most of the chatty pod's lines, so they belong in the kept window.
        pane.Enqueue($"{Stamp(Cap * 3 - 10)} quiet one", quiet);
        pane.Enqueue($"{Stamp(Cap * 3 - 5)} quiet two", quiet);
        pane.Flush(force: true);

        var messages = pane.LogLines.Select(l => l.Message).ToList();
        await Assert.That(messages.Count).IsEqualTo(Cap);
        await Assert.That(messages).Contains("quiet one");
        await Assert.That(messages).Contains("quiet two");
        await Assert.That(messages[^1]).IsEqualTo($"chatty {Cap * 3 - 1}");
    }

    [Test]
    public async Task A_steady_stream_past_the_cap_trims_in_one_notification_per_flush()
    {
        var pane = Pane();
        var source = pane.RegisterSource("api-a", "app");
        for (var i = 0; i < Cap; i++)
        {
            pane.Enqueue($"{Stamp(i)} line {i}", source);
        }

        pane.Flush(force: true);

        var actions = new List<NotifyCollectionChangedAction>();
        pane.LogLines.CollectionChanged += (_, e) => actions.Add(e.Action);
        for (var i = Cap; i < Cap + 500; i++)
        {
            pane.Enqueue($"{Stamp(i)} line {i}", source);
        }

        pane.Flush(force: true);

        await Assert.That(pane.LogLines.Count).IsEqualTo(Cap);
        await Assert.That(pane.LogLines[0].Message).IsEqualTo("line 500");
        await Assert.That(pane.LogLines[^1].Message).IsEqualTo($"line {Cap + 499}");
        await Assert.That(actions).IsEquivalentTo([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add]);
    }

    [Test]
    public async Task Pod_detail_keeps_the_newest_lines_of_a_burst_past_the_cap()
    {
        var pane = Detail();
        var notifications = 0;
        pane.LogLines.CollectionChanged += (_, _) => notifications++;

        const int burst = 200_000;
        for (var i = 0; i < burst; i++)
        {
            pane.Enqueue($"{Stamp(i)} line {i}");
        }

        var clock = Stopwatch.StartNew();
        pane.FlushLogLines();
        clock.Stop();

        await Assert.That(pane.LogLines.Count).IsEqualTo(Cap);
        await Assert.That(pane.LogLines[0].Message).IsEqualTo($"line {burst - Cap}");
        await Assert.That(pane.LogLines[^1].Message).IsEqualTo($"line {burst - 1}");
        await Assert.That(pane.TrimNotice!).Contains("Older lines were trimmed");
        await Assert.That(notifications).IsLessThanOrEqualTo(2);
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }
}
