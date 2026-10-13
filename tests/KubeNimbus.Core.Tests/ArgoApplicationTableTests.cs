using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// #303: the Argo dashboard folds a list+watch of Applications, so a sync started elsewhere
/// shows without a reload. The live half is <c>Live/ArgoLiveTests</c>.
/// </summary>
public class ArgoApplicationTableTests
{
    private static readonly ResourceDescriptor Applications =
        new(ArgoCd.Group, "v1alpha1", "Application", "applications", "application", true, ["app"], []);

    internal static ResourceEvent<DynamicResource> Event(
        ResourceEventType type, string name, string sync = "Synced", string health = "Healthy", string phase = "")
    {
        var json = $$"""
            {"apiVersion":"argoproj.io/v1alpha1","kind":"Application",
             "metadata":{"name":"{{name}}","namespace":"argocd"},
             "spec":{"project":"default"},
             "status":{"sync":{"status":"{{sync}}"},"health":{"status":"{{health}}"},
                       "operationState":{"phase":"{{phase}}"} } }
            """;
        using var document = JsonDocument.Parse(json);
        return new ResourceEvent<DynamicResource>(type, DynamicResource.FromListItem(document.RootElement.Clone(), Applications));
    }

    [Test]
    public async Task A_modified_application_replaces_its_row_and_leaves_the_others_alone()
    {
        var table = new ArgoApplicationTable();
        table.Apply(ResourceEvent<DynamicResource>.Reset);
        table.Apply(Event(ResourceEventType.Added, "shop"));
        table.Apply(Event(ResourceEventType.Added, "ledger"));
        table.Apply(ResourceEvent<DynamicResource>.Synced);
        var ledger = table.Applications.Single(a => a.Name == "ledger");

        table.Apply(Event(ResourceEventType.Modified, "shop", sync: "OutOfSync", phase: "Running"));

        await Assert.That(table.Count).IsEqualTo(2);
        var shop = table.Applications.Single(a => a.Name == "shop");
        await Assert.That(shop.Sync).IsEqualTo(ArgoSyncState.OutOfSync);
        await Assert.That(shop.OperationPhase).IsEqualTo("Running");
        await Assert.That(ReferenceEquals(table.Applications.Single(a => a.Name == "ledger"), ledger)).IsTrue();
    }

    [Test]
    public async Task A_deleted_application_leaves_and_a_reset_starts_over()
    {
        var table = new ArgoApplicationTable();
        table.Apply(Event(ResourceEventType.Added, "shop"));
        table.Apply(Event(ResourceEventType.Added, "ledger"));
        table.Apply(ResourceEvent<DynamicResource>.Synced);

        table.Apply(Event(ResourceEventType.Deleted, "shop"));
        await Assert.That(table.Applications.Select(a => a.Name)).IsEquivalentTo(["ledger"]);
        await Assert.That(table.IsSynced).IsTrue();

        table.Apply(ResourceEvent<DynamicResource>.Reset);
        await Assert.That(table.Count).IsEqualTo(0);
        await Assert.That(table.IsSynced).IsFalse();
    }
}
