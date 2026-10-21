using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Applications list's row menu (2026-10-21): Sync, Sync with prune…, Refresh from Git and
/// Terminate sync… on an app Argo CD tracks, Restart…, Show in Resources and Copy name on
/// every app. Each acts on the selected row and reports on the list's own strip, without
/// opening the page. Driven through the real demo tab, as <see cref="ArgoPaletteTargetTests"/> is.
/// </summary>
public class ApplicationsRowMenuTests
{
    private static ClusterTabViewModel DemoTab()
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        shell.Tabs.Add(tab);
        shell.SelectedTab = tab;
        shell.Mode = ShellMode.Applications;
        return tab;
    }

    private static ApplicationRowViewModel ArgoRow(ClusterTabViewModel tab, string argoName) =>
        tab.Applications.Rows.First(r => r.Entry.Argo?.Name == argoName);

    /// <summary>
    /// Sync fires on the click and its result is one line on the list's strip; the why is the
    /// tooltip. On the demo cluster that line says nothing was sent, and no page opens.
    /// </summary>
    [Test]
    public async Task Sync_from_the_menu_reports_one_line_on_the_list()
    {
        var tab = DemoTab();
        var list = tab.Applications;
        list.SelectedRow = ArgoRow(tab, "checkout");

        await Assert.That(list.SelectedIsArgo).IsTrue();
        await Assert.That(list.SyncSelectedCommand.CanExecute(null)).IsTrue();

        list.SyncSelectedCommand.Execute(null);

        await Assert.That(list.Page).IsNull();
        await Assert.That(list.PendingAction!.Kind).IsEqualTo(RowActionKind.ArgoSync);
        await Assert.That(list.PendingAction!.IsQuestionVisible).IsFalse();
        await Assert.That(list.PendingAction!.Message).IsEqualTo("Nothing was sent for Application/checkout: the demo cluster has no API server.");
        await Assert.That(list.PendingAction!.MessageDetail!).Contains("checkout");
        await Assert.That(list.PendingAction!.IsDone).IsTrue();
    }

    /// <summary>Prune deletes, so it asks; Terminate is there only while Argo reports the sync running.</summary>
    [Test]
    public async Task Prune_asks_and_terminate_follows_the_phase()
    {
        var tab = DemoTab();
        var list = tab.Applications;

        list.SelectedRow = ArgoRow(tab, "checkout");
        await Assert.That(list.TerminateSelectedSyncCommand.CanExecute(null)).IsFalse();
        list.SyncSelectedWithPruneCommand.Execute(null);
        await Assert.That(list.PendingAction!.Kind).IsEqualTo(RowActionKind.ArgoSyncPrune);
        await Assert.That(list.PendingAction!.IsQuestionVisible).IsTrue();
        await Assert.That(list.PendingAction!.Question).Contains("checkout");

        list.PendingAction!.DismissCommand.Execute(null);
        list.SelectedRow = ArgoRow(tab, "notification-dispatcher");
        await Assert.That(list.TerminateSelectedSyncCommand.CanExecute(null)).IsTrue();
        list.TerminateSelectedSyncCommand.Execute(null);
        await Assert.That(list.PendingAction!.Kind).IsEqualTo(RowActionKind.ArgoTerminate);
    }

    /// <summary>An app no Argo CD Application tracks offers no Argo item, and still offers Restart… on its workload.</summary>
    [Test]
    public async Task An_app_without_argo_offers_restart_but_no_sync()
    {
        var tab = DemoTab();
        var list = tab.Applications;
        list.SelectedRow = list.Rows.First(r => r.Entry.Argo is null && ApplicationActions.CanRestart(list, r.Entry));

        await Assert.That(list.SelectedIsArgo).IsFalse();
        await Assert.That(list.SyncSelectedCommand.CanExecute(null)).IsFalse();
        await Assert.That(list.RefreshSelectedCommand.CanExecute(null)).IsFalse();
        await Assert.That(list.ShowSelectedInResourcesCommand.CanExecute(null)).IsTrue();

        list.RestartSelectedCommand.Execute(null);
        await Assert.That(list.PendingAction!.Kind).IsEqualTo(RowActionKind.Restart);
        await Assert.That(list.PendingAction!.IsQuestionVisible).IsTrue();
        await Assert.That(list.Page).IsNull();
    }

    /// <summary>Opening a page clears a finished result off the list, so going back shows no stale line.</summary>
    [Test]
    public async Task Opening_a_page_clears_a_finished_list_result()
    {
        var tab = DemoTab();
        var list = tab.Applications;
        var row = ArgoRow(tab, "checkout");
        list.SelectedRow = row;
        list.RefreshSelectedCommand.Execute(null);
        await Assert.That(list.PendingAction).IsNotNull();

        list.Open(row);

        await Assert.That(list.PendingAction).IsNull();
    }
}
