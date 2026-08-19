using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Pod detail's Overview tab, driven through the real view model.
///
/// <para>
/// Two invariants here cannot be reached from Core's own tests, and neither is visible in
/// a screenshot. Probes are <em>container-scoped</em> and their selector is the container
/// strip the Environment tab already uses, so selecting a different container has to
/// re-read them — a section that kept showing the first container's probes under the
/// second container's name is the same class of bug the log stream had before it followed
/// the picker. And the rebuild is signature-guarded so a watch tick that changed nothing
/// does not throw away scroll position or a half-made text selection; a guard that also
/// swallowed a <em>changed</em> condition would be worse than no guard, because
/// conditions changing is the whole reason the section exists.
/// </para>
/// </summary>
public class PodOverviewTests
{
    private static DynamicResource Pod(string readyStatus = "True", string probePort = "8080")
    {
        var json = $$"""
        {
          "apiVersion": "v1",
          "kind": "Pod",
          "metadata": { "name": "app-1", "namespace": "payments", "uid": "u1",
                        "creationTimestamp": "2026-08-01T10:00:00Z" },
          "spec": {
            "nodeSelector": { "workload-tier": "payments" },
            "priorityClassName": "payments-critical",
            "priority": 100000,
            "tolerations": [ { "key": "dedicated", "operator": "Equal", "value": "payments",
                               "effect": "NoSchedule" } ],
            "containers": [
              { "name": "app",
                "readinessProbe": { "httpGet": { "path": "/healthz", "port": {{probePort}} } } },
              { "name": "sidecar" }
            ]
          },
          "status": {
            "phase": "Running",
            "qosClass": "Burstable",
            "conditions": [ { "type": "Ready", "status": "{{readyStatus}}",
                              "lastTransitionTime": "2026-08-01T10:00:05Z" } ],
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

    /// <summary>
    /// A demo-mode tab (<c>client: null</c>), which is the one shape that constructs
    /// without a cluster, a socket or a metrics poll.
    /// </summary>
    private static PodDetailTabViewModel Detail(DynamicResource pod)
    {
        TestObjects.RedirectStores();
        var row = new ResourceRowViewModel(pod);
        return new PodDetailTabViewModel(null, row, _ => { }, (_, _) => Task.CompletedTask);
    }

    [Test]
    public async Task The_overview_reads_conditions_placement_and_the_selected_containers_probes()
    {
        var detail = Detail(Pod());

        await Assert.That(detail.QosClass).IsEqualTo("Burstable");
        await Assert.That(detail.PriorityText).IsEqualTo("payments-critical (100000)");
        await Assert.That(detail.NodeSelector.Single().Display).IsEqualTo("workload-tier=payments");
        await Assert.That(detail.Tolerations.Single().Display).IsEqualTo("dedicated=payments:NoSchedule");
        await Assert.That(detail.Conditions.Single().Type).IsEqualTo("Ready");
        await Assert.That(detail.Conditions.Single().Health).IsEqualTo(ResourceHealth.Ok);
        await Assert.That(detail.Probes.Single().Handler).IsEqualTo("http-get http://:8080/healthz");
        await Assert.That(detail.HasProbes).IsTrue();
    }

    /// <summary>The strip is the probes' selector, exactly as it is the Environment tab's.</summary>
    [Test]
    public async Task Selecting_a_container_with_no_probes_empties_the_section()
    {
        var detail = Detail(Pod());
        await Assert.That(detail.SelectedContainer!.Name).IsEqualTo("app");

        detail.SelectedContainer = detail.Containers.Single(c => c.Name == "sidecar");

        await Assert.That(detail.Probes).IsEmpty();
        await Assert.That(detail.HasProbes).IsFalse();
    }

    /// <summary>
    /// The guard is on the fields' own text, not on "have we rendered once". A watch tick
    /// that flips Ready to False must land — that is the tick someone opened this tab for.
    /// </summary>
    [Test]
    public async Task A_watch_tick_that_changes_a_condition_is_not_swallowed_by_the_rebuild_guard()
    {
        var pod = Pod();
        var row = new ResourceRowViewModel(pod);
        TestObjects.RedirectStores();
        var detail = new PodDetailTabViewModel(null, row, _ => { }, (_, _) => Task.CompletedTask);

        await Assert.That(detail.Conditions.Single().Health).IsEqualTo(ResourceHealth.Ok);

        row.Update(Pod(readyStatus: "False"));
        // The watch's own path posts this to the UI thread (OnRowChanged); the test
        // calls it directly, the way ClusterTabRowFilterTests drives `Apply`. What is
        // under test is the rebuild guard, not the dispatch.
        detail.RefreshFromRow();

        await Assert.That(detail.Conditions.Single().Status).IsEqualTo("False");
        await Assert.That(detail.Conditions.Single().Health).IsEqualTo(ResourceHealth.Error);
    }

    /// <summary>A probe rewritten in place is a spec change, and the section has to follow it too.</summary>
    [Test]
    public async Task A_changed_probe_is_re_read()
    {
        var row = new ResourceRowViewModel(Pod());
        TestObjects.RedirectStores();
        var detail = new PodDetailTabViewModel(null, row, _ => { }, (_, _) => Task.CompletedTask);

        row.Update(Pod(probePort: "9090"));
        // The watch's own path posts this to the UI thread (OnRowChanged); the test
        // calls it directly, the way ClusterTabRowFilterTests drives `Apply`. What is
        // under test is the rebuild guard, not the dispatch.
        detail.RefreshFromRow();

        await Assert.That(detail.Probes.Single().Handler).IsEqualTo("http-get http://:9090/healthz");
    }
}
