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
        await Assert.That(table.IsSynced).IsFalse();
        await Assert.That(table.IsRelisting).IsTrue();

        table.Apply(ResourceEvent<DynamicResource>.Synced);
        await Assert.That(table.Count).IsEqualTo(0);
        await Assert.That(table.IsRelisting).IsFalse();
    }

    /// <summary>
    /// #339: a relist keeps the last complete list on show until its own Synced, so a large
    /// relist is one swap rather than the old rows, then a growing subset. Mutation-checked:
    /// without the snapshot taken at Reset, <c>Applications</c> holds only "shop" mid-relist.
    /// </summary>
    [Test]
    public async Task A_relist_shows_the_previous_list_until_it_has_synced_and_then_swaps()
    {
        var table = new ArgoApplicationTable();
        table.Apply(ResourceEvent<DynamicResource>.Reset);
        table.Apply(Event(ResourceEventType.Added, "shop"));

        // The first list shows as it arrives.
        await Assert.That(table.Applications.Select(a => a.Name)).IsEquivalentTo(["shop"]);
        table.Apply(Event(ResourceEventType.Added, "ledger"));
        table.Apply(Event(ResourceEventType.Added, "search"));
        table.Apply(ResourceEvent<DynamicResource>.Synced);

        table.Apply(ResourceEvent<DynamicResource>.Reset);
        table.Apply(Event(ResourceEventType.Added, "shop", sync: "OutOfSync"));
        await Assert.That(table.Applications.Select(a => a.Name)).IsEquivalentTo(["shop", "ledger", "search"]);
        await Assert.That(table.Applications.Single(a => a.Name == "shop").Sync).IsEqualTo(ArgoSyncState.Synced);

        table.Apply(Event(ResourceEventType.Added, "ledger"));
        table.Apply(ResourceEvent<DynamicResource>.Synced);
        await Assert.That(table.Applications.Select(a => a.Name)).IsEquivalentTo(["shop", "ledger"]);
        await Assert.That(table.Applications.Single(a => a.Name == "shop").Sync).IsEqualTo(ArgoSyncState.OutOfSync);
    }

    /// <summary>
    /// #336: the fleet list folds each member's events into that member's own table: a Reset
    /// from one cluster leaves the others' rows, every row carries its cluster's descriptor, and
    /// a member whose watch ended has answered and leaves the list.
    /// </summary>
    [Test]
    public async Task The_fleet_table_keeps_each_cluster_to_itself()
    {
        var v1 = new ResourceDescriptor(ArgoCd.Group, "v1", "Application", "applications", "application", true, [], []);
        FleetTarget Target(string name, ResourceDescriptor descriptor) =>
            new(new FleetMember(name, null!, ClusterEnvironment.Development), descriptor);
        var fleet = new ArgoFleetTable([Target("east", Applications), Target("west", v1)]);

        fleet.Apply(new("east", ResourceEvent<DynamicResource>.Reset));
        fleet.Apply(new("east", Event(ResourceEventType.Added, "shop")));
        await Assert.That(fleet.HasEveryAnswer).IsFalse();
        fleet.Apply(new("east", ResourceEvent<DynamicResource>.Synced));
        fleet.Apply(new("west", Event(ResourceEventType.Added, "shop")));
        fleet.Apply(new("west", ResourceEvent<DynamicResource>.Synced));
        fleet.Apply(new("north", Event(ResourceEventType.Added, "stray")));
        await Assert.That(fleet.HasEveryAnswer).IsTrue();
        await Assert.That(fleet.Applications.Select(a => $"{a.ClusterName}:{a.Application.Name}:{a.Descriptor.Version}").Order())
            .IsEquivalentTo(["east:shop:v1alpha1", "west:shop:v1"]);

        // West relists: east is untouched, and west still shows its last list until it syncs.
        fleet.Apply(new("west", ResourceEvent<DynamicResource>.Reset));
        await Assert.That(fleet.Applications.Count).IsEqualTo(2);

        fleet.MarkEnded("west");
        await Assert.That(fleet.Applications.Select(a => a.ClusterName)).IsEquivalentTo(["east"]);
        await Assert.That(fleet.HasEveryAnswer).IsTrue();
    }
}
