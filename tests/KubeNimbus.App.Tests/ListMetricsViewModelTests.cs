using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The list's usage poller as a child of the cluster tab (ENG-13, #361), exercised on its own
/// through a stand-in host: no tab, and no request to any server. What a poll does on the
/// tab — samples on rows, the capacity strip, a re-sort — stays covered through the tab by
/// <see cref="MetricsLastSampleTests"/>, <see cref="MetricsRefusedTests"/> and
/// <c>CapacityStripTests</c>.
/// </summary>
public class ListMetricsViewModelTests
{
    private const string Reason = "pods.metrics.k8s.io is forbidden";

    private sealed class Host : IListMetricsHost
    {
        public bool AreMetricsVisible { get; set; } = true;

        public bool IsCapacityActive { get; set; }

        public List<(int Samples, string? Refused)> Applied { get; } = [];

        public int ApiGone { get; private set; }

        public void ApplyUsage(Dictionary<string, (long? Cpu, long? Memory)> byKey, CancellationToken token, string? refused) =>
            Applied.Add((byKey.Count, refused));

        public void HideUsageColumns() => AreMetricsVisible = false;

        public void OnMetricsApiGone() => ApiGone++;
    }

    [Test]
    public async Task A_refusal_hides_the_columns_says_why_and_is_remembered()
    {
        var host = new Host();
        var metrics = new ListMetricsViewModel(host);

        metrics.Refuse("Pod", Reason, CancellationToken.None, remember: true);

        await Assert.That(host.AreMetricsVisible).IsFalse();
        await Assert.That(metrics.Notice).IsEqualTo($"CPU and memory are hidden: this user may not read pod usage ({Reason}).");
        await Assert.That(metrics.IsRefused("Pod")).IsTrue();
        await Assert.That(metrics.IsRefused("Node")).IsFalse();
    }

    /// <summary>On the Nodes list the capacity strip says "usage not permitted" itself (UI rule 20).</summary>
    [Test]
    public async Task Under_the_capacity_strip_a_refusal_adds_no_notice()
    {
        var host = new Host { IsCapacityActive = true };
        var metrics = new ListMetricsViewModel(host);

        metrics.Refuse("Node", "", CancellationToken.None, remember: true);

        await Assert.That(host.AreMetricsVisible).IsFalse();
        await Assert.That(metrics.Notice).IsNull();
        await Assert.That(metrics.IsRefused("Node")).IsTrue();
    }

    [Test]
    public async Task A_refusal_without_a_reason_ends_its_sentence_plainly()
    {
        var metrics = new ListMetricsViewModel(new Host());

        metrics.Refuse("Node", "", CancellationToken.None, remember: false);

        await Assert.That(metrics.Notice).IsEqualTo("CPU and memory are hidden: this user may not read node usage.");
        await Assert.That(metrics.IsRefused("Node")).IsFalse();
    }

    [Test]
    public async Task A_refusal_for_a_list_no_longer_on_screen_changes_nothing()
    {
        var host = new Host();
        var metrics = new ListMetricsViewModel(host);

        metrics.Refuse("Pod", Reason, new CancellationToken(canceled: true), remember: true);

        await Assert.That(host.AreMetricsVisible).IsTrue();
        await Assert.That(metrics.Notice).IsNull();
        await Assert.That(metrics.IsRefused("Pod")).IsFalse();
    }

    /// <summary>
    /// #310: a remembered refusal is answered at once when the list comes back — the columns
    /// hidden again with the reason, and the empty sample set handed on as refused, not as a
    /// measurement — and nothing is asked of the server. The client points at a port nothing
    /// listens on, so a request would not be silent.
    /// </summary>
    [Test]
    public async Task A_remembered_refusal_is_answered_without_asking_again()
    {
        var host = new Host();
        var metrics = new ListMetricsViewModel(host);
        metrics.Refuse("Pod", Reason, CancellationToken.None, remember: true);
        metrics.ForgetList();
        host.AreMetricsVisible = true;
        using var client = await ClusterClient.ConnectAsync(RefusedContext());

        metrics.Start(ResourceDescriptor.Pods, [("", client)], [null], CancellationToken.None);

        await Assert.That(host.Applied.Count).IsEqualTo(1);
        await Assert.That(host.Applied[0].Samples).IsEqualTo(0);
        await Assert.That(host.Applied[0].Refused).IsEqualTo(Reason);
        await Assert.That(host.AreMetricsVisible).IsFalse();
        await Assert.That(metrics.Notice).Contains(Reason);
    }

    [Test]
    public async Task No_poll_starts_while_the_columns_are_off_or_with_no_cluster_in_scope()
    {
        var host = new Host { AreMetricsVisible = false };
        var metrics = new ListMetricsViewModel(host);
        metrics.Refuse("Pod", Reason, CancellationToken.None, remember: true);
        metrics.ForgetList();
        using var client = await ClusterClient.ConnectAsync(RefusedContext());

        // Off: even the remembered answer is not given again.
        metrics.Start(ResourceDescriptor.Pods, [("", client)], [null], CancellationToken.None);
        host.AreMetricsVisible = true;
        metrics.Start(ResourceDescriptor.Pods, [], [null], CancellationToken.None);

        await Assert.That(host.Applied).IsEmpty();
        await Assert.That(metrics.Notice).IsNull();
    }

    /// <summary>A refusal is about the user, so only a new connect forgets it (#310).</summary>
    [Test]
    public async Task A_connect_forgets_the_refusals_and_probes_again()
    {
        var metrics = new ListMetricsViewModel(new Host()) { IsApiAvailable = true };
        metrics.Refuse("Pod", Reason, CancellationToken.None, remember: true);

        metrics.ForgetList();
        await Assert.That(metrics.IsRefused("Pod")).IsTrue();

        await metrics.DetectAsync(null);
        await Assert.That(metrics.IsRefused("Pod")).IsFalse();
        await Assert.That(metrics.IsApiAvailable).IsFalse();
    }

    /// <summary>ENG-55 (#252): a row that arrives after a poll takes its sample, until the list goes.</summary>
    [Test]
    public async Task The_last_poll_serves_late_rows_until_the_list_is_forgotten()
    {
        var metrics = new ListMetricsViewModel(new Host());
        var pod = TestObjects.Pod("payments", "checkout-0");
        metrics.KeepLastPoll(new() { [pod.Key] = (250_000_000, 64L * 1024 * 1024) });

        var early = new ResourceRowViewModel(pod);
        var stranger = new ResourceRowViewModel(TestObjects.Pod("payments", "checkout-1"));
        metrics.ApplyLastSample(pod.Key, early);
        metrics.ApplyLastSample(stranger.Key, stranger);

        metrics.ForgetList();
        var late = new ResourceRowViewModel(pod);
        metrics.ApplyLastSample(pod.Key, late);

        await Assert.That(early.CpuText).IsNotEqualTo("—");
        await Assert.That(stranger.CpuText).IsEqualTo("—");
        await Assert.That(late.CpuText).IsEqualTo("—");
    }

    private static ClusterContext RefusedContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kubenimbus-app-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "kubeconfig.yaml");
        File.WriteAllText(path, """
            apiVersion: v1
            kind: Config
            clusters:
              - name: refused
                cluster:
                  server: http://127.0.0.1:1
            contexts:
              - name: refused
                context:
                  cluster: refused
                  user: tester
            current-context: refused
            users:
              - name: tester
                user:
                  token: not-a-credential
            """);
        return new ClusterContext("refused", "refused", null, "tester", path);
    }
}
