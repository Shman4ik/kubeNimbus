using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #364: the palette's Argo CD actions name and act on the Application on screen. In the
/// Applications mode that is the open page, or else the selected row; the Resources mode's
/// dashboard selection is not on screen there and must not be what a sync or a terminate hits.
/// Driven through the real shell and the real demo tab, as <see cref="ArgoDashboardTests"/> is.
/// </summary>
public class ArgoPaletteTargetTests
{
    private static (MainWindowViewModel Shell, ClusterTabViewModel Tab) Shell()
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        shell.Tabs.Add(tab);
        shell.SelectedTab = tab;
        return (shell, tab);
    }

    private static List<PaletteItem> ArgoItems(MainWindowViewModel shell)
    {
        shell.Palette.Open();
        var items = shell.Palette.FilteredItems.Where(i => i.Title.StartsWith("Argo CD:", StringComparison.Ordinal)).ToList();
        shell.Palette.Close();
        return items;
    }

    private static ApplicationRowViewModel AppRow(ClusterTabViewModel tab, string argoName) =>
        tab.Applications.Rows.First(r => r.Entry.Argo?.Name == argoName);

    /// <summary>
    /// The QA run's own sequence (check K-4): an Application with a running sync selected on
    /// the Resources mode's dashboard, then another one opened in the Applications mode. Every
    /// Argo item names the one on screen, terminate is gone (its sync is not running), and a
    /// sync and a prune land on the page's own strip, which this mode draws. Mutation-checked
    /// twice: removing the Applications-mode branch of <c>ArgoActionTarget</c> makes the items
    /// name notification-dispatcher again; removing the Applications-mode branch of
    /// <c>ArmArgoAction</c> arms the hidden Resources strip and leaves the page's empty.
    /// </summary>
    [Test]
    public async Task In_the_applications_mode_the_palette_acts_on_the_open_application()
    {
        var (shell, tab) = Shell();

        shell.Mode = ShellMode.Resources;
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.IsArgoDashboard));
        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == "notification-dispatcher");
        var resources = ArgoItems(shell);
        await Assert.That(resources.Any(i => i.Title == "Argo CD: terminate sync…")).IsTrue();
        await Assert.That(resources.All(i => i.Subtitle.StartsWith("argocd/notification-dispatcher", StringComparison.Ordinal))).IsTrue();

        shell.Mode = ShellMode.Applications;
        tab.Applications.Open(AppRow(tab, "checkout"));

        var applications = ArgoItems(shell);
        await Assert.That(applications.Select(i => i.Title)).IsEquivalentTo(
            ["Argo CD: sync", "Argo CD: sync with prune…", "Argo CD: refresh from Git"]);
        await Assert.That(applications.All(i => i.Subtitle.StartsWith("argocd/checkout", StringComparison.Ordinal))).IsTrue();

        // The action lands on the page's own strip, the one this mode draws; the Resources
        // view's strip is hidden here and must stay empty.
        var page = tab.Applications.Page!;
        applications.First(i => i.Title == "Argo CD: sync").Execute!();
        await Assert.That(tab.PendingRowAction).IsNull();
        await Assert.That(page.PendingAction!.Kind).IsEqualTo(RowActionKind.ArgoSync);
        await Assert.That(page.PendingAction!.Message).Contains("checkout");
        await Assert.That(page.PendingAction!.Message).DoesNotContain("notification-dispatcher");

        page.PendingAction = null;
        ArgoItems(shell).First(i => i.Title == "Argo CD: sync with prune…").Execute!();
        await Assert.That(tab.PendingRowAction).IsNull();
        await Assert.That(page.PendingAction!.Kind).IsEqualTo(RowActionKind.ArgoSyncPrune);
        await Assert.That(page.PendingAction!.IsQuestionVisible).IsTrue();
        await Assert.That(page.PendingAction!.Question).Contains("checkout");
    }

    /// <summary>
    /// With no page open the selected row is what is on screen, and terminate follows that
    /// row's own operation phase. The list has no strip, so running an item opens that row's
    /// page (what a click on the row does) and asks there.
    /// </summary>
    [Test]
    public async Task With_no_page_open_the_selected_row_is_the_target()
    {
        var (shell, tab) = Shell();
        tab.Applications.SelectedRow = AppRow(tab, "notification-dispatcher");

        var items = ArgoItems(shell);

        await Assert.That(items.Any(i => i.Title == "Argo CD: terminate sync…")).IsTrue();
        await Assert.That(items.All(i => i.Subtitle.StartsWith("argocd/notification-dispatcher", StringComparison.Ordinal))).IsTrue();

        items.First(i => i.Title == "Argo CD: terminate sync…").Execute!();

        await Assert.That(tab.PendingRowAction).IsNull();
        await Assert.That(tab.Applications.Page!.Entry.Argo!.Name).IsEqualTo("notification-dispatcher");
        await Assert.That(tab.Applications.Page!.PendingAction!.Kind).IsEqualTo(RowActionKind.ArgoTerminate);
        await Assert.That(tab.Applications.Page!.PendingAction!.IsQuestionVisible).IsTrue();
    }

    /// <summary>
    /// An application no Argo CD Application tracks offers no Argo item, even while the
    /// Resources mode's dashboard still has one selected.
    /// </summary>
    [Test]
    public async Task A_workload_with_no_argo_application_offers_no_argo_items()
    {
        var (shell, tab) = Shell();
        shell.Mode = ShellMode.Resources;
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.IsArgoDashboard));
        tab.SelectedArgoApplication = tab.ArgoApplications[0];
        await Assert.That(ArgoItems(shell)).IsNotEmpty();

        shell.Mode = ShellMode.Applications;
        tab.Applications.Open(tab.Applications.Rows.First(r => !r.IsArgo));

        await Assert.That(ArgoItems(shell)).IsEmpty();
        await Assert.That(tab.CanSyncSelectedArgoApplication).IsFalse();
    }
}
