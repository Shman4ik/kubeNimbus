using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Service pane's watch of its own object (#264) and its Port-forward action (FEAT-29),
/// on the demo cluster's own Services. The demo starts no watch, so the frames are posted
/// through <c>ApplyServiceEvent</c>, the entry point the pane's watch calls.
/// </summary>
public class ServicePaneDeleteAndForwardTests
{
    private static (ClusterTabViewModel Tab, ServiceDetailTabViewModel Pane) Open(string name)
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectedNamespace = "payments";
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "Service" }));
        tab.SelectedRow = tab.Rows.First(r => r.Name == name);
        tab.OpenSelectedCommand.Execute(null);
        return (tab, (ServiceDetailTabViewModel)tab.SelectedInspectorTab!);
    }

    /// <summary>What the pane's own watch delivers for a Service that exists: a list that finds it.</summary>
    private static void Listed(ServiceDetailTabViewModel pane, DynamicResource service)
    {
        pane.ApplyServiceEvent(ResourceEvent<DynamicResource>.Reset);
        pane.ApplyServiceEvent(TestObjects.Added(service));
        pane.ApplyServiceEvent(ResourceEvent<DynamicResource>.Synced);
    }

    /// <summary>
    /// The kn-qa finding: after a delete the pane read "2 pods match, none is serving — the
    /// service has nowhere to send traffic", with the header and the Overview unchanged.
    /// </summary>
    [Test]
    public async Task A_deleted_service_withdraws_its_verdict_and_says_it_was_deleted()
    {
        var (tab, pane) = Open("checkout");
        var service = tab.Rows.First(r => r.Name == "checkout").Resource;
        Listed(pane, service);
        await Assert.That(pane.IsDeleted).IsFalse();
        await Assert.That(pane.HasBackends).IsTrue();

        pane.ApplyServiceEvent(TestObjects.Deleted(service));

        await Assert.That(pane.IsDeleted).IsTrue();
        await Assert.That(pane.VerdictHeadline).IsEqualTo(ServiceDetailTabViewModel.DeletedHeadline);
        await Assert.That(pane.VerdictDetail).Contains("payments/checkout was deleted while this pane was open");
        await Assert.That(pane.VerdictLevel).IsEqualTo("warn");
        await Assert.That(pane.HasBackends).IsFalse();
        await Assert.That(pane.IsLoading).IsFalse();
        await Assert.That(pane.SummaryTooltip).StartsWith("Deleted — last seen as ");
        await Assert.That(pane.PortForwardCommand.CanExecute(null)).IsFalse();
    }

    /// <summary>A relist (after a 410 or a reconnect) that no longer finds the Service is a delete too.</summary>
    [Test]
    public async Task A_relist_that_finds_no_service_reads_as_deleted()
    {
        var (tab, pane) = Open("checkout");
        Listed(pane, tab.Rows.First(r => r.Name == "checkout").Resource);

        pane.ApplyServiceEvent(ResourceEvent<DynamicResource>.Reset);
        await Assert.That(pane.IsDeleted).IsFalse();
        pane.ApplyServiceEvent(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(pane.IsDeleted).IsTrue();
        await Assert.That(pane.VerdictHeadline).IsEqualTo(ServiceDetailTabViewModel.DeletedHeadline);
    }

    /// <summary>Created again under the same name: the pane reads it afresh and gives its verdict again.</summary>
    [Test]
    public async Task A_service_created_again_gets_its_verdict_back()
    {
        var (tab, pane) = Open("checkout");
        var service = tab.Rows.First(r => r.Name == "checkout").Resource;
        Listed(pane, service);
        pane.ApplyServiceEvent(TestObjects.Deleted(service));

        pane.ApplyServiceEvent(TestObjects.Added(service));

        await Assert.That(pane.IsDeleted).IsFalse();
        await Assert.That(pane.VerdictHeadline).IsEqualTo("1 of 2 matching pods serving");
        await Assert.That(pane.Backends.Count).IsEqualTo(2);
    }

    /// <summary>
    /// The Port-forward button opens a Service forward pane offering the service's own ports;
    /// on the demo cluster that pane is the "not available here" state, as a demo pod's is.
    /// </summary>
    [Test]
    public async Task Port_forward_opens_a_service_forward_with_the_services_ports()
    {
        var (tab, pane) = Open("checkout");
        await Assert.That(pane.PortForwardCommand.CanExecute(null)).IsTrue();

        pane.PortForwardCommand.Execute(null);

        var forward = (PortForwardTabViewModel)tab.SelectedInspectorTab!;
        await Assert.That(forward.IsService).IsTrue();
        await Assert.That(forward.IsDemo).IsTrue();
        await Assert.That(forward.RemotePortLabel).IsEqualTo("Service port");
        await Assert.That(forward.AvailablePorts.Select(p => p.Number).ToArray())
            .IsEquivalentTo(pane.Ports.Select(p => p.Port).ToArray());
        await Assert.That(forward.Title).IsEqualTo($"Forward: svc/checkout:{pane.Ports[0].Port}");
        await Assert.That(forward.StartCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task An_external_name_service_offers_no_forward()
    {
        var (_, pane) = Open("payments-db");

        await Assert.That(pane.PortForwardCommand.CanExecute(null)).IsFalse();
        await Assert.That(pane.PortForwardTooltip).Contains("DNS name");
    }
}
