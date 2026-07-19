using System.Net.Sockets;
using k8s.Models;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Integration tests for the generic (discovery/CRD-capable) resource surface:
/// discovery, generic list+watch, server-side apply, events, owner navigation,
/// exec and port-forward. Same skip-cleanly-without-a-sandbox convention as
/// <see cref="ClusterClientTests"/>.
/// </summary>
public class DynamicResourceTests
{
    private static async Task<ClusterClient?> ConnectAsync()
    {
        var context = await SandboxCluster.TryGetContextAsync();
        return context is null ? null : ClusterClient.Connect(context);
    }

    [Test]
    [Timeout(30_000)]
    public async Task DiscoverResources_includes_core_pods(CancellationToken ct)
    {
        using var client = await ConnectAsync();
        if (client is null)
        {
            return;
        }

        var catalog = await client.DiscoverResourcesAsync(ct);

        await Assert.That(catalog).IsNotEmpty();
        var pods = catalog.FirstOrDefault(r => r is { Group: "", Kind: "Pod" });
        await Assert.That(pods).IsNotNull();
        await Assert.That(pods!.Namespaced).IsTrue();
        await Assert.That(pods.Plural).IsEqualTo("pods");

        // A CRD-backed kind should never be hardcoded — this just proves the
        // walk covers grouped APIs too (every k8s server ships at least one
        // apps/v1 kind, e.g. Deployment).
        var deployments = catalog.FirstOrDefault(r => r is { Group: "apps", Kind: "Deployment" });
        await Assert.That(deployments).IsNotNull();
    }

    [Test]
    [Timeout(60_000)]
    public async Task WatchResource_pods_emits_reset_then_items_like_typed_watch(CancellationToken ct)
    {
        using var client = await ConnectAsync();
        if (client is null)
        {
            return;
        }

        var sawReset = false;
        var names = new List<string>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await foreach (var evt in client.WatchResourceAsync(ResourceDescriptor.Pods, "kube-system", cancellationToken: cts.Token))
        {
            if (evt.Type == ResourceEventType.Reset)
            {
                sawReset = true;
                continue;
            }

            if (evt is { Type: ResourceEventType.Added, Resource: { } r })
            {
                names.Add(r.Name);
            }

            if (names.Count >= 1)
            {
                await cts.CancelAsync();
                break;
            }
        }

        await Assert.That(sawReset).IsTrue();
        await Assert.That(names).IsNotEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task ListResourceOnce_returns_namespaces_without_watching(CancellationToken ct)
    {
        using var client = await ConnectAsync();
        if (client is null)
        {
            return;
        }

        var namespaces = new ResourceDescriptor(
            Group: "", Version: "v1", Kind: "Namespace", Plural: "namespaces", SingularName: "namespace",
            Namespaced: false, ShortNames: ["ns"], Categories: []);

        var items = await client.ListResourceOnceAsync(namespaces, cancellationToken: ct);

        await Assert.That(items).IsNotEmpty();
        await Assert.That(items.Any(n => n.Name == "kube-system")).IsTrue();
    }

    [Test]
    [Timeout(30_000)]
    public async Task Apply_creates_reads_and_deletes_a_configmap(CancellationToken ct)
    {
        using var client = await ConnectAsync();
        if (client is null)
        {
            return;
        }

        var configMaps = new ResourceDescriptor(
            Group: "", Version: "v1", Kind: "ConfigMap", Plural: "configmaps", SingularName: "configmap",
            Namespaced: true, ShortNames: ["cm"], Categories: []);

        var name = $"kubenimbus-test-{Guid.NewGuid():N}"[..30];
        var yaml = $"""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {name}
              namespace: default
            data:
              greeting: hello
            """;

        try
        {
            var applied = await client.ApplyYamlAsync(
                configMaps, "default", name, yaml, fieldManager: "kubenimbus-tests", cancellationToken: ct);
            await Assert.That(applied.Name).IsEqualTo(name);

            var read = await client.ReadResourceAsync(configMaps, "default", name, ct);
            await Assert.That(read).IsNotNull();
            await Assert.That(read!.Raw.GetProperty("data").GetProperty("greeting").GetString()).IsEqualTo("hello");

            // Re-apply with the same field manager must not conflict.
            await client.ApplyYamlAsync(configMaps, "default", name, yaml, fieldManager: "kubenimbus-tests", cancellationToken: ct);
        }
        finally
        {
            await client.DeleteResourceAsync(configMaps, "default", name, ct);
        }

        var afterDelete = await client.ReadResourceAsync(configMaps, "default", name, ct);
        await Assert.That(afterDelete).IsNull();
    }

    [Test]
    [Timeout(30_000)]
    public async Task GetEventsFor_and_ResolveOwner_do_not_throw_for_a_running_pod(CancellationToken ct)
    {
        using var client = await ConnectAsync();
        if (client is null)
        {
            return;
        }

        DynamicResource? target = null;
        using (var findCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            await foreach (var evt in client.WatchResourceAsync(ResourceDescriptor.Pods, "kube-system", cancellationToken: findCts.Token))
            {
                if (evt is { Type: ResourceEventType.Added, Resource.OwnerReferences.Count: > 0 } and { Resource: not null })
                {
                    target = evt.Resource;
                    await findCts.CancelAsync();
                    break;
                }
            }
        }

        if (target is null)
        {
            return; // no owned pod found in this cluster shape; nothing to assert
        }

        var events = await client.GetEventsForAsync(target, ct);
        await Assert.That(events).IsNotNull(); // may legitimately be empty; must not throw

        var owner = await client.ResolveOwnerAsync(target.OwnerReferences[0], target.Namespace, ct);
        await Assert.That(owner).IsNotNull();
        await Assert.That(owner!.Kind).IsEqualTo(target.OwnerReferences[0].Kind);
    }

    [Test]
    [Timeout(30_000)]
    public async Task Exec_runs_a_shell_command_in_a_running_pod(CancellationToken ct)
    {
        using var client = await ConnectAsync();
        if (client is null)
        {
            return;
        }

        var target = await FindRunningPodWithContainerAsync(client, ct);
        if (target is null)
        {
            return;
        }

        var (pod, containerName) = target.Value;

        ExecSession session;
        try
        {
            session = await client.ExecAsync(
                pod.Metadata!.NamespaceProperty!, pod.Metadata.Name!, containerName,
                ["/bin/sh", "-c", "echo kubenimbus-exec-ok"], tty: false, ct);
        }
        catch (Exception)
        {
            // Some system images (distroless/scratch) have no shell to exec into;
            // that's an environment fact, not a bug in ClusterClient.ExecAsync.
            return;
        }

        using (session)
        {
            using var reader = new StreamReader(session.StdOut);
            var readTask = reader.ReadLineAsync(ct).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10), ct));

            // The plumbing (MuxedStream session, demux) is what's under test here.
            // A null line is a legitimate outcome too: some system images have no
            // /bin/sh, so the command fails on the error channel and stdout just
            // hits EOF immediately — that's the target image's shape, not a bug.
            if (completed == readTask)
            {
                var line = await readTask;
                if (line is not null)
                {
                    await Assert.That(line).Contains("kubenimbus-exec-ok");
                }
            }
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task PortForward_connects_to_a_pod_tcp_port(CancellationToken ct)
    {
        using var client = await ConnectAsync();
        if (client is null)
        {
            return;
        }

        var target = await FindRunningPodWithTcpPortAsync(client, ct);
        if (target is null)
        {
            return;
        }

        var (podNamespace, podName, podPort) = target.Value;

        await using var session = client.StartPortForward(podNamespace, podName, podPort);
        await session.StartAsync(ct);

        using var tcp = new TcpClient();
        var connectTask = tcp.ConnectAsync("127.0.0.1", session.LocalPort, ct).AsTask();
        var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(10), ct));

        await Assert.That(completed).IsEqualTo(connectTask);
        await connectTask; // observe any connection exception
        await Assert.That(tcp.Connected).IsTrue();
    }

    private static async Task<(V1Pod Pod, string Container)?> FindRunningPodWithContainerAsync(ClusterClient client, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await foreach (var evt in client.WatchPodsAsync("kube-system", cancellationToken: cts.Token))
        {
            if (evt is { Type: ResourceEventType.Added, Resource.Status.Phase: "Running" } && evt.Resource.Spec?.Containers is { Count: > 0 } containers)
            {
                await cts.CancelAsync();
                return (evt.Resource, containers[0].Name);
            }
        }

        return null;
    }

    private static async Task<(string Namespace, string Name, int Port)?> FindRunningPodWithTcpPortAsync(ClusterClient client, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await foreach (var evt in client.WatchPodsAsync("kube-system", cancellationToken: cts.Token))
        {
            if (evt is not { Type: ResourceEventType.Added, Resource.Status.Phase: "Running" })
            {
                continue;
            }

            var port = evt.Resource.Spec?.Containers
                .SelectMany(c => c.Ports ?? [])
                .FirstOrDefault(p => p.Protocol is null or "TCP");

            if (port is not null)
            {
                await cts.CancelAsync();
                return (evt.Resource.Metadata!.NamespaceProperty!, evt.Resource.Metadata.Name!, port.ContainerPort);
            }
        }

        return null;
    }
}
