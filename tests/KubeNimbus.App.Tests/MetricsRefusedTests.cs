using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #310: under narrow RBAC (a 403 on metrics.k8s.io) the list's CPU/Memory columns used to
/// show dashes forever while the poll asked again every interval. A refusal now hides the
/// columns the way a cluster without metrics-server does, says why in the status bar, and
/// is remembered for the connection so the poll does not ask again. The live half — that a
/// real 403 reaches the poll as the <c>HttpRequestException</c> it catches — is
/// <c>ClusterCapacityLiveTests</c> in the Core tests.
/// </summary>
[NotInParallel]
public class MetricsRefusedTests
{
    private const string Reason =
        "pods.metrics.k8s.io is forbidden: User \"system:serviceaccount:team:viewer\" cannot list resource \"pods\" in API group \"metrics.k8s.io\"";

    [Test]
    public async Task A_refused_poll_hides_the_columns_and_says_why()
    {
        var tab = TestObjects.Tab();
        tab.AreMetricsVisible = true;

        tab.Metrics.Refuse("Pod", Reason, CancellationToken.None, remember: true);

        await Assert.That(tab.AreMetricsVisible).IsFalse();
        await Assert.That(tab.AreUsageColumnsVisible).IsFalse();
        await Assert.That(tab.Metrics.Notice).IsEqualTo($"CPU and memory are hidden: this user may not read pod usage ({Reason}).");
        await Assert.That(tab.IsStatusWorthShowing).IsTrue();
        await Assert.That(tab.Metrics.IsRefused("Pod")).IsTrue();
        await Assert.That(tab.Metrics.IsRefused("Node")).IsFalse();
    }

    [Test]
    public async Task The_reason_goes_with_the_list_it_was_said_about()
    {
        var tab = TestObjects.Tab();
        tab.AreMetricsVisible = true;
        tab.Metrics.Refuse("Pod", Reason, CancellationToken.None, remember: true);

        // Another namespace is another list: its notice is its own (and the refusal, which is
        // about the user and not the list, is still remembered).
        tab.SelectedNamespace = "ledger";

        await Assert.That(tab.Metrics.Notice).IsNull();
        await Assert.That(tab.Metrics.IsRefused("Pod")).IsTrue();
    }

    [Test]
    public async Task A_refusal_for_a_list_no_longer_on_screen_changes_nothing()
    {
        var tab = TestObjects.Tab();
        tab.AreMetricsVisible = true;

        tab.Metrics.Refuse("Pod", Reason, new CancellationToken(canceled: true), remember: true);

        await Assert.That(tab.AreMetricsVisible).IsTrue();
        await Assert.That(tab.Metrics.Notice).IsNull();
        await Assert.That(tab.Metrics.IsRefused("Pod")).IsFalse();
    }
}
