using System.Text.Json;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-55 (#171), the cluster half: FEAT-44's requests/limits text against a real API
/// server and a real <c>metrics.k8s.io</c>. The Usage tab reads a container's
/// <c>resources</c> with <see cref="Quantity.ParseBytes"/> / <see cref="Quantity.ParseCpuNanocores"/>
/// and divides the measured value by the limit with <see cref="Quantity.Percent"/>
/// (<c>PodDetailTabViewModel.ReadResourceList</c>, <c>ContainerViewModel.MemoryResourceText</c>);
/// these drive those same functions over what the cluster stored and measured.
/// </summary>
public class ContainerUsageLiveTests
{
    private static readonly ResourceDescriptor LimitRanges = new(
        "", "v1", "LimitRange", "limitranges", "limitrange", true, ["limits"], []);

    /// <summary>
    /// A container that declares nothing, in a namespace with a <c>LimitRange</c>: the
    /// admission plugin writes the defaults into the pod itself, so the pod read back
    /// carries them as its own <c>resources</c> — which is why the Usage tab shows them
    /// with no special case. Runs in a second namespace of this run's own, because a
    /// LimitRange would default every other test's pods too.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_limitrange_default_is_stored_on_the_pod_as_its_own(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var ns = $"{LiveCluster.Namespace}-lr";
        await client.ApplyYamlAsync(ResourceDescriptor.Namespaces, null, ns, $$"""
            apiVersion: v1
            kind: Namespace
            metadata:
              name: {{ns}}
              labels:
                app.kubernetes.io/part-of: kubenimbus-live-tests
                {{LiveCluster.RunLabel}}: "{{LiveCluster.RunId}}"
            """, LiveCluster.FieldManager, cancellationToken: ct);
        try
        {
            await client.ApplyYamlAsync(LimitRanges, ns, "defaults", $$"""
                apiVersion: v1
                kind: LimitRange
                metadata:
                  name: defaults
                  namespace: {{ns}}
                spec:
                  limits:
                    - type: Container
                      default: { cpu: 200m, memory: 96Mi }
                      defaultRequest: { cpu: 100m, memory: 48Mi }
                """, LiveCluster.FieldManager, cancellationToken: ct);

            // The namespace's default ServiceAccount has to exist before a pod can be created.
            DynamicResource? pod = null;
            await LiveCluster.WaitUntilAsync(async () =>
            {
                try
                {
                    pod = await client.ApplyYamlAsync(ResourceDescriptor.Pods, ns, "bare", $$"""
                        apiVersion: v1
                        kind: Pod
                        metadata:
                          name: bare
                          namespace: {{ns}}
                        spec:
                          terminationGracePeriodSeconds: 0
                          containers:
                            - name: app
                              image: {{LiveCluster.Image}}
                              imagePullPolicy: IfNotPresent
                              command: ["sleep", "3600"]
                        """, LiveCluster.FieldManager, cancellationToken: ct);
                    return true;
                }
                catch (KubernetesApiException ex) when (ex.ServerMessage?.Contains("serviceaccount") == true)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(60), "the pod to be admitted", ct);

            var read = await client.ReadResourceAsync(ResourceDescriptor.Pods, ns, "bare", ct);
            var resources = read!.Raw.GetProperty("spec").GetProperty("containers")[0].GetProperty("resources");
            await Assert.That(Quantity.ParseBytes(Str(resources, "requests", "memory"))).IsEqualTo(48L * 1024 * 1024);
            await Assert.That(Quantity.ParseBytes(Str(resources, "limits", "memory"))).IsEqualTo(96L * 1024 * 1024);
            await Assert.That(Quantity.ParseCpuNanocores(Str(resources, "requests", "cpu"))).IsEqualTo(100_000_000L);
            await Assert.That(Quantity.ParseCpuNanocores(Str(resources, "limits", "cpu"))).IsEqualTo(200_000_000L);

            // The plugin says it did so, on the object — the only trace that these were not typed.
            await Assert.That(read.Annotations["kubernetes.io/limit-ranger"]).Contains("LimitRanger plugin set");
            await Assert.That(pod).IsNotNull();
        }
        finally
        {
            await client.DeleteResourceAsync(ResourceDescriptor.Namespaces, null, ns, CancellationToken.None);
        }
    }

    /// <summary>
    /// A container holding 40 MiB of a 64 MiB limit (a file in a memory-backed volume,
    /// which counts against the container's memory and is never reclaimed): the real
    /// <c>metrics.k8s.io</c> sample divided by the declared limit is the percentage the
    /// Usage tab prints, and it is where the container actually is — between 60% and 100%,
    /// not a scale error of 1024 or 1000 either way.
    /// </summary>
    [Test]
    [Timeout(240_000)]
    public async Task A_container_near_its_memory_limit_reads_as_that_share_of_it(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        if (!await client.IsMetricsApiAvailableAsync(ct))
        {
            Skip.Test("the sandbox serves no metrics.k8s.io (k3s's metrics-server is not running)");
        }

        var name = LiveCluster.Named("near-limit");
        await LiveCluster.ApplyAsync(client, ResourceDescriptor.Pods, name, $$"""
            apiVersion: v1
            kind: Pod
            metadata:
              name: {{name}}
              namespace: {{LiveCluster.Namespace}}
              labels:
                app: {{name}}
            spec:
              terminationGracePeriodSeconds: 0
              volumes:
                - name: fill
                  emptyDir: { medium: Memory }
              containers:
                - name: app
                  image: {{LiveCluster.Image}}
                  imagePullPolicy: IfNotPresent
                  command: ["sh", "-c", "dd if=/dev/zero of=/fill/x bs=1M count=40 && trap 'exit 0' TERM; while true; do sleep 1 & wait $!; done"]
                  volumeMounts:
                    - { name: fill, mountPath: /fill }
                  resources:
                    requests: { memory: 32Mi }
                    limits: { memory: 64Mi }
            """, ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);

        var pod = (await LiveCluster.PodsOfAsync(client, name, ct)).Single();
        var limits = pod.Raw.GetProperty("spec").GetProperty("containers")[0].GetProperty("resources");
        var limit = Quantity.ParseBytes(Str(limits, "limits", "memory"));
        await Assert.That(limit).IsEqualTo(64L * 1024 * 1024);

        // metrics-server needs a scrape or two after the container starts.
        long? used = null;
        await LiveCluster.WaitUntilAsync(async () =>
        {
            var sample = await client.GetPodMetricsAsync(LiveCluster.Namespace, name, ct);
            used = sample?.Containers.SingleOrDefault(c => c.Name == "app")?.MemoryBytes;
            return used > 40L * 1024 * 1024;
        }, TimeSpan.FromSeconds(180), "a metrics sample showing the 40 MiB the container holds", ct);

        var percent = Quantity.Percent(used, limit);
        await Assert.That(percent).IsNotNull();
        await Assert.That(percent!.Value).IsGreaterThanOrEqualTo(60).And.IsLessThanOrEqualTo(100);
        Console.WriteLine($"near-limit: {Quantity.FormatMemory(used)} of {Quantity.FormatMemory(limit)} = {percent:0.#}%");
    }

    private static string? Str(JsonElement resources, string list, string key) =>
        resources.TryGetProperty(list, out var l) && l.TryGetProperty(key, out var v) ? v.GetString() : null;
}
