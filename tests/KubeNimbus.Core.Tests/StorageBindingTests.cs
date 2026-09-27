using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>FEAT-47: the other end of a PV ↔ PVC binding, read off the object.</summary>
public class StorageBindingTests
{
    private static readonly ResourceDescriptor Claims =
        new("", "v1", "PersistentVolumeClaim", "persistentvolumeclaims", "persistentvolumeclaim", true, ["pvc"], []);

    private static readonly ResourceDescriptor Volumes =
        new("", "v1", "PersistentVolume", "persistentvolumes", "persistentvolume", false, ["pv"], []);

    private static DynamicResource Parse(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    [Test]
    public async Task A_bound_claim_names_its_cluster_scoped_volume()
    {
        var claim = Parse("""{"kind":"PersistentVolumeClaim","metadata":{"name":"data-0","namespace":"shop"},"spec":{"volumeName":"pvc-123"}}""");

        var bound = StorageBinding.For(Claims, claim);

        await Assert.That(bound).IsEqualTo(new BoundObject("PersistentVolume", null, "pvc-123"));
        await Assert.That(bound!.Description).IsEqualTo("volume pvc-123");
    }

    [Test]
    public async Task A_bound_volume_names_its_claim_with_the_claims_namespace()
    {
        var volume = Parse("""{"kind":"PersistentVolume","metadata":{"name":"pvc-123"},"spec":{"claimRef":{"kind":"PersistentVolumeClaim","namespace":"shop","name":"data-0"}}}""");

        var bound = StorageBinding.For(Volumes, volume);

        await Assert.That(bound).IsEqualTo(new BoundObject("PersistentVolumeClaim", "shop", "data-0"));
        await Assert.That(bound!.Description).IsEqualTo("claim shop/data-0");
    }

    [Test]
    public async Task A_pending_claim_and_an_available_volume_are_bound_to_nothing()
    {
        await Assert.That(StorageBinding.For(Claims, Parse("""{"spec":{"storageClassName":"fast"}}"""))).IsNull();
        await Assert.That(StorageBinding.For(Volumes, Parse("""{"spec":{"capacity":{"storage":"1Gi"}}}"""))).IsNull();
        await Assert.That(StorageBinding.For(Claims, Parse("""{"spec":{"volumeName":""}}"""))).IsNull();
    }

    /// <summary>The same fields on any other kind mean nothing here — the kind is part of the rule.</summary>
    [Test]
    public async Task Another_kind_with_the_same_field_is_not_a_binding()
    {
        var pod = new ResourceDescriptor("", "v1", "Pod", "pods", "pod", true, [], []);

        await Assert.That(StorageBinding.For(pod, Parse("""{"spec":{"volumeName":"x"}}"""))).IsNull();
    }
}
