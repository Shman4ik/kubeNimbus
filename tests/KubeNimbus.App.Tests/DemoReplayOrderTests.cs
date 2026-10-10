using System.Text.Json;
using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// ENG-53: the multi-pod pane's demo replay hands its lines out in the order they were
/// logged, so the merged pane reads the same however the flush ticks fall. With a timer per
/// stream, which tick a line landed in decided its place, and the merged panes' screenshots
/// (<c>cluster-tab-workload-logs*</c>, <c>applications-page-*</c>, <c>ux-logs-palette</c>)
/// differed between two runs of one build.
/// </summary>
public class DemoReplayOrderTests
{
    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: []);

    /// <summary>A demo pane whose selector matches nothing, so the test registers the sources.</summary>
    private static WorkloadLogsTabViewModel Pane()
    {
        TestObjects.RedirectStores();
        using var document = JsonDocument.Parse("""
            {"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"rg","namespace":"nowhere"},
             "spec":{"selector":{"matchLabels":{"app":"rg-nothing-matches-this"}}}}
            """);
        var workload = new DynamicResource(document.RootElement.Clone());
        return new WorkloadLogsTabViewModel(null, DeploymentDescriptor, workload, LabelSelector.ForPodsOf(workload)!);
    }

    /// <summary>The rolling deployment the demo exists to show, plus a pod sharing another's stream.</summary>
    private static readonly string[] Pods =
    [
        "payment-service-report-generator-7f9c8d6bcd-x7k2m",
        "payment-service-report-generator-7f9c8d6bcd-m4v8s",
        "payment-service-report-generator-8c1a4f2e91-tq6rn",
        "checkout-api-6b7f9d8c5-2xk4p",
    ];

    /// <summary>Replays every stream to its end, flushing the pane after every <paramref name="ticksPerFlush"/> intervals.</summary>
    private static string Replay(int ticksPerFlush)
    {
        var pane = Pane();
        var replays = new List<WorkloadLogsTabViewModel.DemoReplay>();
        for (var i = 0; i < Pods.Length; i++)
        {
            var source = pane.RegisterSource(Pods[i], "app");
            replays.Add(new WorkloadLogsTabViewModel.DemoReplay(source, DemoLogs.For(Pods[i], "app"), CancellationToken.None, i));
        }

        for (var tick = 1; replays.Count > 0; tick++)
        {
            WorkloadLogsTabViewModel.ReplayTick(replays, pane.Enqueue);
            if (tick % ticksPerFlush == 0)
            {
                pane.Flush(force: true);
            }
        }

        pane.Flush(force: true);
        return string.Join("\n", pane.LogLines.Select(l => $"{l.Source?.PodName} {l.RawLine}"));
    }

    [Test]
    public async Task The_merged_pane_reads_the_same_however_the_flush_ticks_fall()
    {
        var everyTick = Replay(ticksPerFlush: 1);

        await Assert.That(everyTick.Split('\n').Length).IsGreaterThan(20);
        await Assert.That(Replay(ticksPerFlush: 2)).IsEqualTo(everyTick);
        await Assert.That(Replay(ticksPerFlush: 3)).IsEqualTo(everyTick);
        await Assert.That(Replay(ticksPerFlush: int.MaxValue)).IsEqualTo(everyTick);
    }

    [Test]
    public async Task The_merged_pane_is_in_the_order_the_lines_were_logged()
    {
        var times = Replay(ticksPerFlush: 1).Split('\n')
            .Select(line => new LogLineViewModel(line[(line.IndexOf(' ') + 1)..], showTimestamp: false).At!.Value)
            .ToArray();

        await Assert.That(times.Zip(times.Skip(1)).All(pair => pair.First <= pair.Second)).IsTrue();
    }

    /// <summary>
    /// The replay is in time order across streams only if each stream is in time order itself;
    /// a canned stream that steps back in time would be re-sorted inside whichever flush it
    /// landed in, and the merge would depend on the ticks again.
    /// </summary>
    [Test]
    public async Task Every_demo_stream_is_in_time_order()
    {
        var backwards = new List<string>();
        foreach (var pod in DemoData.Pods)
        {
            foreach (var container in pod.Raw.GetProperty("spec").GetProperty("containers").EnumerateArray()
                         .Select(c => c.GetProperty("name").GetString()!))
            {
                var times = DemoLogs.For(pod.Name, container)
                    .Select(line => new LogLineViewModel(line, showTimestamp: false).At)
                    .OfType<DateTimeOffset>()
                    .ToArray();
                if (times.Zip(times.Skip(1)).Any(pair => pair.Second < pair.First))
                {
                    backwards.Add($"{pod.Name}/{container}");
                }
            }
        }

        await Assert.That(backwards).IsEmpty();
    }
}
