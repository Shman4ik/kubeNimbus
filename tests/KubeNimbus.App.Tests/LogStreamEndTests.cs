using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// What the log pane says when a follow ends cleanly. It used to be "Stream ended — app
/// exited." every time, including when a load balancer dropped an idle connection to a
/// container that was still running — a false statement about the thing being debugged.
/// </summary>
public class LogStreamEndTests
{
    private static ContainerRun Run(ContainerRunState state, string id = "containerd://a", int restarts = 0,
        string? reason = null, int? exitCode = null) => new(state, reason, exitCode, id, restarts);

    [Test]
    public async Task The_same_run_still_running_is_a_dropped_connection_not_an_exit()
    {
        var (text, problem) = LogStreamEnd.Describe("app", Run(ContainerRunState.Running), Run(ContainerRunState.Running));

        await Assert.That(text).Contains("still running");
        await Assert.That(text).DoesNotContain("exited");
        await Assert.That(problem).IsTrue();
    }

    [Test]
    public async Task A_new_container_id_is_a_restart()
    {
        var (text, _) = LogStreamEnd.Describe(
            "app", Run(ContainerRunState.Running), Run(ContainerRunState.Running, id: "containerd://b", restarts: 1));

        await Assert.That(text).Contains("restarted");
        await Assert.That(text).Contains("Previous");
    }

    [Test]
    public async Task A_terminated_run_names_its_reason_and_exit_code()
    {
        var (text, problem) = LogStreamEnd.Describe(
            "app", Run(ContainerRunState.Running), Run(ContainerRunState.Terminated, reason: "Error", exitCode: 1));

        await Assert.That(text).IsEqualTo("Stream ended — app exited (Error, exit code 1).");
        await Assert.That(problem).IsFalse();
    }

    [Test]
    public async Task A_waiting_container_is_not_said_to_have_exited()
    {
        var (text, _) = LogStreamEnd.Describe(
            "app", null, Run(ContainerRunState.Waiting, reason: "CrashLoopBackOff"));

        await Assert.That(text).IsEqualTo("Stream ended — app is not running (CrashLoopBackOff).");
    }

    [Test]
    public async Task A_container_that_never_started_is_said_not_to_have_started()
    {
        var (text, _) = LogStreamEnd.Describe(
            "nginx", null, new ContainerRun(ContainerRunState.Waiting, "ContainerCreating", null, null, 0));

        await Assert.That(text).StartsWith("nginx has not started yet (ContainerCreating)");
        await Assert.That(text).DoesNotContain("exited");
    }

    [Test]
    public async Task A_deleted_pod_and_an_unreadable_one_say_so()
    {
        await Assert.That(LogStreamEnd.Describe("app", null, null, podGone: true).Text).Contains("pod is gone");

        var (text, problem) = LogStreamEnd.Describe("app", null, null, readError: "Forbidden");
        await Assert.That(text).Contains("Forbidden");
        await Assert.That(problem).IsTrue();
    }

    [Test]
    public async Task ContainerRunOf_reads_state_reason_exit_code_id_and_restarts_from_any_status_array()
    {
        using var doc = JsonDocument.Parse("""
            {"status":{
              "initContainerStatuses":[{"name":"init","restartCount":0,"containerID":"containerd://i",
                "state":{"terminated":{"reason":"Completed","exitCode":0}}}],
              "containerStatuses":[{"name":"app","restartCount":3,"containerID":"containerd://a",
                "state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}}
            """);

        var app = PodDetails.ContainerRunOf(doc.RootElement, "app");
        var init = PodDetails.ContainerRunOf(doc.RootElement, "init");

        await Assert.That(app).IsEqualTo(new ContainerRun(ContainerRunState.Waiting, "CrashLoopBackOff", null, "containerd://a", 3));
        await Assert.That(init).IsEqualTo(new ContainerRun(ContainerRunState.Terminated, "Completed", 0, "containerd://i", 0));
        await Assert.That(PodDetails.ContainerRunOf(doc.RootElement, "missing")).IsNull();
    }
}
