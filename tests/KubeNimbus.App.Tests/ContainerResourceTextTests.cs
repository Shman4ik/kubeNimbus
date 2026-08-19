using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The requests/limits text on pod detail's Usage tab, driven through the real view model.
///
/// <para>
/// Requests and limits are independently optional in Kubernetes, so there are four states
/// and every one of them has to read honestly: a container that declares neither must not
/// render as one that requested nothing, and a limit that is absent must not silently
/// become a percentage denominator of zero. None of that is visible in a screenshot of a
/// healthy pod, which is exactly how the numbers stayed hidden in a hover tooltip for as
/// long as they did.
/// </para>
/// </summary>
public class ContainerResourceTextTests
{
    /// <summary>
    /// A pod whose two containers carry different halves of a resources block, so one
    /// object covers "declared both", "request only", "limit only" and "neither"
    /// depending on which pair the caller asks for.
    /// </summary>
    private static DynamicResource Pod(string appResources, string sidecarResources)
    {
        var json = $$"""
        {
          "apiVersion": "v1",
          "kind": "Pod",
          "metadata": { "name": "app-1", "namespace": "payments", "uid": "u1",
                        "creationTimestamp": "2026-08-01T10:00:00Z" },
          "spec": {
            "containers": [
              { "name": "app"{{appResources}} },
              { "name": "sidecar"{{sidecarResources}} }
            ]
          },
          "status": {
            "phase": "Running",
            "containerStatuses": [
              { "name": "app", "ready": true, "restartCount": 0, "state": { "running": {} } },
              { "name": "sidecar", "ready": true, "restartCount": 0, "state": { "running": {} } }
            ]
          }
        }
        """;

        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private const string RequestAndLimit =
        """, "resources": { "requests": { "cpu": "250m", "memory": "256Mi" }, "limits": { "cpu": "500m", "memory": "512Mi" } }""";

    private const string RequestOnly =
        """, "resources": { "requests": { "cpu": "50m", "memory": "64Mi" } }""";

    private const string LimitOnly =
        """, "resources": { "limits": { "cpu": "100m", "memory": "128Mi" } }""";

    private const string Nothing = "";

    /// <summary>
    /// A demo-mode tab (<c>client: null</c>) — the one shape that constructs without a
    /// cluster, a socket or a metrics poll. Same helper shape as <see cref="PodOverviewTests"/>.
    /// </summary>
    private static PodDetailTabViewModel Detail(DynamicResource pod)
    {
        TestObjects.RedirectStores();
        return new PodDetailTabViewModel(null, new ResourceRowViewModel(pod), _ => { }, (_, _) => Task.CompletedTask);
    }

    private static ContainerViewModel Container(PodDetailTabViewModel detail, string name) =>
        detail.Containers.Single(c => c.Name == name);

    [Test]
    public async Task Both_halves_are_named_and_a_limit_carries_a_percentage()
    {
        var detail = Detail(Pod(RequestAndLimit, RequestOnly));
        var app = Container(detail, "app");

        // 50m of a 500m limit, 128 MiB of a 512 MiB one — the measured half is what
        // turns the declared limit into a percentage.
        app.ApplyUsage(50_000_000L, 128L * 1024 * 1024);

        await Assert.That(app.CpuResourceText).IsEqualTo("request 250m · limit 500m · 10% of limit");
        await Assert.That(app.MemoryResourceText).IsEqualTo("request 256 MiB · limit 512 MiB · 25% of limit");
    }

    /// <summary>
    /// The commonest real shape: a request and no cap. "no limit" in words, because a
    /// blank there reads as a limit this pane failed to fetch (UI rule 9) — and there is
    /// nothing to be a percentage of, so no percentage is offered.
    /// </summary>
    [Test]
    public async Task A_container_with_no_limit_says_so_and_offers_no_percentage()
    {
        var detail = Detail(Pod(RequestAndLimit, RequestOnly));
        var sidecar = Container(detail, "sidecar");
        sidecar.ApplyUsage(10_000_000L, 32L * 1024 * 1024);

        await Assert.That(sidecar.CpuResourceText).IsEqualTo("request 50m · no limit");
        await Assert.That(sidecar.MemoryResourceText).IsEqualTo("request 64 MiB · no limit");
    }

    /// <summary>The other single-sided case: a cap with nothing reserved. Still a percentage.</summary>
    [Test]
    public async Task A_container_with_a_limit_and_no_request_says_so()
    {
        var detail = Detail(Pod(LimitOnly, Nothing));
        var app = Container(detail, "app");
        app.ApplyUsage(50_000_000L, 64L * 1024 * 1024);

        await Assert.That(app.CpuResourceText).IsEqualTo("no request · limit 100m · 50% of limit");
        await Assert.That(app.MemoryResourceText).IsEqualTo("no request · limit 128 MiB · 50% of limit");
    }

    /// <summary>
    /// BestEffort — no resources block at all. One sentence rather than "no request · no
    /// limit", which is the same information twice for the state that carries the least.
    /// </summary>
    [Test]
    public async Task A_container_that_declares_neither_says_that_in_one_line()
    {
        var detail = Detail(Pod(LimitOnly, Nothing));
        var sidecar = Container(detail, "sidecar");

        await Assert.That(sidecar.CpuResourceText).IsEqualTo("no request or limit set");
        await Assert.That(sidecar.MemoryResourceText).IsEqualTo("no request or limit set");
    }

    /// <summary>
    /// The declared line does not wait for a metrics poll: it comes from the pod spec, so
    /// a cluster with no metrics-server still shows it. Only the percentage is withheld,
    /// because it is the one part that needs a measurement.
    /// </summary>
    [Test]
    public async Task The_declared_line_is_readable_before_any_usage_sample_arrives()
    {
        // Built directly rather than through the tab: a client-less tab is the demo
        // cluster, which seeds a window of stand-in polls on construction, and this is
        // the one state that has to be reached with no measurement at all.
        var app = new ContainerViewModel("app", "registry/app:1")
        {
            CpuRequestNanocores = 250_000_000L,
            CpuLimitNanocores = 500_000_000L,
            MemoryRequestBytes = 256L * 1024 * 1024,
            MemoryLimitBytes = 512L * 1024 * 1024,
        };

        await Assert.That(app.HasUsage).IsFalse();
        await Assert.That(app.CpuResourceText).IsEqualTo("request 250m · limit 500m");
        await Assert.That(app.MemoryResourceText).IsEqualTo("request 256 MiB · limit 512 MiB");

        // And the measured line is just its label until then, so the column still says
        // which measure it belongs to rather than printing an em dash as a reading.
        await Assert.That(app.CpuMeasuredText).IsEqualTo("CPU");
        await Assert.That(app.MemoryMeasuredText).IsEqualTo("MEM");
    }

    [Test]
    public async Task The_measured_line_carries_current_and_peak()
    {
        var detail = Detail(Pod(RequestAndLimit, RequestOnly));
        var app = Container(detail, "app");

        app.ApplyUsage(120_000_000L, 300L * 1024 * 1024);
        app.ApplyUsage(60_000_000L, 200L * 1024 * 1024);

        await Assert.That(app.CpuMeasuredText).IsEqualTo("CPU  60m · peak 120m");
        await Assert.That(app.MemoryMeasuredText).IsEqualTo("MEM  200 MiB · peak 300 MiB");
    }

    /// <summary>
    /// A container idling under a generous limit is the ordinary case, and rounding it to
    /// "0% of limit" reads as "measured nothing" rather than "barely touching it".
    /// </summary>
    [Test]
    public async Task A_fraction_of_a_percent_is_not_rounded_down_to_zero()
    {
        var detail = Detail(Pod(RequestAndLimit, RequestOnly));
        var app = Container(detail, "app");

        app.ApplyUsage(1_000_000L, 1024L * 1024);

        await Assert.That(app.CpuResourceText).IsEqualTo("request 250m · limit 500m · <1% of limit");
        await Assert.That(app.MemoryResourceText).IsEqualTo("request 256 MiB · limit 512 MiB · <1% of limit");
    }

    /// <summary>
    /// The three tab states are mutually exclusive, and the requests/limits section is
    /// outside all of them — which is the whole point of the change. <c>IsCollectingUsage</c>
    /// exists because the two "no charts" notices are now stacked above content rather than
    /// being panels shown instead of it, so each has to know the other is not showing.
    /// </summary>
    [Test]
    public async Task The_collecting_notice_never_shows_alongside_the_no_metrics_one()
    {
        var detail = Detail(Pod(RequestAndLimit, RequestOnly));

        // The tab is client-less here, so it opens with the demo dataset's replayed
        // polls already applied; the state under test is the one a real tab opens in.
        detail.HasUsageSamples = false;
        await Assert.That(detail.IsCollectingUsage).IsTrue();

        detail.IsMetricsUnavailable = true;
        await Assert.That(detail.IsCollectingUsage).IsFalse();

        detail.IsMetricsUnavailable = false;
        detail.HasUsageSamples = true;
        await Assert.That(detail.IsCollectingUsage).IsFalse();
    }
}
