namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// #261: the rule that decides which live-test namespaces a run may delete. No cluster
/// needed — it is the guard that keeps one run's sweep away from another run's namespace.
/// </summary>
public class LiveNamespaceSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_run_never_sweeps_its_own_namespace_however_old()
    {
        await Assert.That(LiveCluster.IsAbandoned(LiveCluster.Namespace, Now.AddDays(-3), Now)).IsFalse();
    }

    [Test]
    public async Task Another_runs_namespace_is_swept_only_once_it_is_older_than_any_run_lasts()
    {
        await Assert.That(LiveCluster.IsAbandoned("kn-live-abcdef", Now.AddMinutes(-30), Now)).IsFalse();
        await Assert.That(LiveCluster.IsAbandoned("kn-live-abcdef", Now - LiveCluster.AbandonedAfter, Now)).IsFalse();
        await Assert.That(LiveCluster.IsAbandoned("kn-live-abcdef", Now.AddHours(-3), Now)).IsTrue();
    }

    [Test]
    public async Task A_namespace_with_no_creation_time_is_left_alone()
    {
        await Assert.That(LiveCluster.IsAbandoned("kn-live-abcdef", null, Now)).IsFalse();
    }

    [Test]
    public async Task Each_run_gets_a_namespace_of_its_own()
    {
        await Assert.That(LiveCluster.Namespace).IsEqualTo($"kn-live-{LiveCluster.RunId}");
        await Assert.That(LiveCluster.Namespace).IsNotEqualTo("bundle-f");
    }
}
