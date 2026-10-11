namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// FEAT-12's New YAML… against a real API server. The editor sends a pasted manifest as it
/// was typed — without a <c>metadata.namespace</c> when it has none — to the path of the
/// namespace it chose by default, so whether the server accepts that, and where the object
/// lands, is the server's answer and not something a stand-in can give.
/// </summary>
public class NewManifestLiveTests
{
    [Test]
    [Timeout(120_000)]
    public async Task A_manifest_without_a_namespace_is_created_in_the_namespace_of_the_path(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("new-yaml");
        var yaml = $$"""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{name}}
            data:
              mode: fast
            """;
        var identity = NewManifest.Read(yaml, out var problem);
        await Assert.That(problem).IsNull();
        await Assert.That(identity!.Namespace).IsNull();

        // The preview first, as the editor does: a create, with the namespace filled in by
        // the server from the path.
        var preview = await client.PreviewApplyAsync(
            ResourceDescriptor.ConfigMaps, LiveCluster.Namespace, name, yaml, LiveCluster.FieldManager, cancellationToken: ct);
        await Assert.That(preview.Diff.IsCreate).IsTrue();
        await Assert.That(preview.Previewed.Namespace).IsEqualTo(LiveCluster.Namespace);

        var applied = await client.ApplyYamlAsync(
            ResourceDescriptor.ConfigMaps, LiveCluster.Namespace, name, yaml, LiveCluster.FieldManager, cancellationToken: ct);
        await Assert.That(applied.Namespace).IsEqualTo(LiveCluster.Namespace);
        await Assert.That(await client.ReadResourceAsync(ResourceDescriptor.ConfigMaps, LiveCluster.Namespace, name, ct)).IsNotNull();
    }

    /// <summary>
    /// A manifest naming a namespace other than the path's is refused by the server — which
    /// is why the editor puts the manifest's own namespace in the path when it has one,
    /// rather than the default.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_manifest_whose_namespace_disagrees_with_the_path_is_refused(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("new-yaml-mismatch");
        var yaml = $$"""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{name}}
              namespace: kube-system
            """;

        await Assert.ThrowsAsync<KubernetesApiException>(async () =>
            await client.ApplyYamlAsync(
                ResourceDescriptor.ConfigMaps, LiveCluster.Namespace, name, yaml, LiveCluster.FieldManager, cancellationToken: ct));
        await Assert.That(await client.ReadResourceAsync(ResourceDescriptor.ConfigMaps, "kube-system", name, ct)).IsNull();
    }
}
