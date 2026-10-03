using System.Text;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// The exec pane's answer to an image with no shell, against a real API server and a
/// real runtime: every shell is refused with the runtime's own "no such file" sentence
/// (which is what the pane reads as "no shell" rather than as any other failure), the
/// ephemeral-container patch is one the server accepts, the container starts, and from
/// inside it the target's processes and files are visible. A stand-in API server can pin
/// the request; only a cluster can say the request does what it is for.
/// </summary>
public class DebugContainerLiveTests
{
    /// <summary>
    /// The pause image: a single static binary and nothing else, so no shell — the
    /// distroless case in its smallest form. k3s already has it, as its own sandbox image.
    /// </summary>
    private const string ShellLessImage = "docker.io/rancher/mirrored-pause:3.6";

    private static string PodYaml(string name) => $"""
        apiVersion: v1
        kind: Pod
        metadata:
          name: {name}
          namespace: {LiveCluster.Namespace}
        spec:
          terminationGracePeriodSeconds: 2
          containers:
            - name: app
              image: {ShellLessImage}
              imagePullPolicy: IfNotPresent
        """;

    [Test]
    [Timeout(240_000)]
    public async Task A_shell_less_pod_gets_a_debug_container_that_sees_its_processes(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("noshell");
        await LiveCluster.ApplyAsync(client, ResourceDescriptor.Pods, name, PodYaml(name), ct);
        await LiveCluster.WaitUntilAsync(
            async () => await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct) is { } pod
                        && LiveCluster.IsReady(pod),
            TimeSpan.FromSeconds(90), $"pod {name} ready", ct);

        await Assert.That(await client.GetPodOperatingSystemAsync(LiveCluster.Namespace, name, ct))
            .IsEqualTo(PodOperatingSystem.Linux);

        foreach (var shell in ExecShells.LinuxShells)
        {
            using var session = await client.ExecAsync(LiveCluster.Namespace, name, "app", [shell], tty: true, ct);
            var refusal = await session.ReadTerminalStatusAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            await Assert.That(ExecShells.IsMissingExecutable(refusal)).IsTrue();
        }

        var added = await client.AddDebugContainerAsync(LiveCluster.Namespace, name, "app", LiveCluster.Image, cancellationToken: ct);
        await Assert.That(added.Name).StartsWith(DebugContainers.NamePrefix);

        var state = await client.WaitForDebugContainerAsync(
            LiveCluster.Namespace, name, added.Name, TimeSpan.FromSeconds(120), cancellationToken: ct);
        await Assert.That(state.Phase).IsEqualTo(DebugContainerPhase.Running);

        // PID 1 in the debug container's view is the target's own process, and its root
        // filesystem is reachable — the two things the pane's connected line promises.
        using (var session = await client.ExecAsync(
                   LiveCluster.Namespace, name, added.Name,
                   ["sh", "-c", "tr '\\0' ' ' < /proc/1/cmdline; ls /proc/1/root/ > /dev/null && echo root-readable"],
                   tty: false, ct))
        {
            var status = session.ReadTerminalStatusAsync(ct);
            var stdout = await ReadAllAsync(session.StdOut, ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            await Assert.That(await status.WaitAsync(TimeSpan.FromSeconds(30), ct)).IsNull();
            await Assert.That(stdout).Contains("/pause");
            await Assert.That(stdout).Contains("root-readable");
        }

        // A second "Start debug container" opens this one instead of adding another.
        var after = await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct);
        await Assert.That(DebugContainers.FindReusable(after!.Raw, "app", LiveCluster.Image)).IsEqualTo(added.Name);
    }

    private static async Task<string> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
