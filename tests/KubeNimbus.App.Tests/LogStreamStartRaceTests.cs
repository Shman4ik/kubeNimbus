using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The one case where a follow that ended cleanly is followed again without being asked:
/// it was opened before the container started, and the container is running now. A real
/// kubelet answers a follow in the moment between "created" and "started" with an empty
/// body that closes at once (k3s v1.33, <c>Live/WorkloadLogsLiveTests</c> in the Core
/// tests), and the multi-pod pane opens a stream the instant its watch reports a new pod —
/// so during a rollout a new pod could sit in the strip with no lines, blamed on a dropped
/// connection, until someone toggled Follow.
/// </summary>
public class LogStreamStartRaceTests
{
    private static ContainerRun Run(ContainerRunState state, string? id = "containerd://a") =>
        new(state, null, null, id, 0);

    [Test]
    public async Task A_container_that_was_not_running_when_asked_and_is_running_now_is_followed_again()
    {
        // Watched pod still "ContainerCreating" when the stream was opened.
        await Assert.That(LogStreamEnd.StartedAfterRequest(
            new ContainerRun(ContainerRunState.Waiting, "ContainerCreating", null, null, 0),
            Run(ContainerRunState.Running))).IsTrue();

        // No status for the container at all yet.
        await Assert.That(LogStreamEnd.StartedAfterRequest(null, Run(ContainerRunState.Running))).IsTrue();
    }

    [Test]
    public async Task A_run_that_was_already_running_is_not_restarted_behind_the_readers_back()
    {
        // The same run, still running: a dropped connection, which the pane states and
        // leaves to Follow (LogStreamEndTests) — restarting here would hide it.
        await Assert.That(LogStreamEnd.StartedAfterRequest(
            Run(ContainerRunState.Running), Run(ContainerRunState.Running))).IsFalse();
    }

    [Test]
    public async Task A_container_that_is_not_running_now_is_not_followed_again()
    {
        await Assert.That(LogStreamEnd.StartedAfterRequest(null, null)).IsFalse();
        await Assert.That(LogStreamEnd.StartedAfterRequest(
            Run(ContainerRunState.Waiting, null), Run(ContainerRunState.Waiting, null))).IsFalse();
        await Assert.That(LogStreamEnd.StartedAfterRequest(
            Run(ContainerRunState.Running), Run(ContainerRunState.Terminated))).IsFalse();
    }
}
