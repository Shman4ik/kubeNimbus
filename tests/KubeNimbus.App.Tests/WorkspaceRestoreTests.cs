using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #405: after a quit with two tabs, a launch restored the Demo tab alone and
/// <c>workspace.json</c> was rewritten with one tab. Nothing on the quit path writes the
/// workspace, so the only writer that drops a saved tab is the restore itself: a saved tab
/// whose context the launch's kubeconfig chain does not have was skipped, and the restore's
/// own save rewrote the file from the open tabs. <c>[NotInParallel]</c>: it writes and reads
/// the workspace.
/// </summary>
[NotInParallel]
public class WorkspaceRestoreTests
{
    private static readonly TabSnapshot Missing = new("sandbox", Path.Combine(Path.GetTempPath(), "kubenimbus-no-such-dir", "kubeconfig.yaml"));

    /// <summary>
    /// Mutation-checked: without <c>_unrestoredTabs</c> in <c>SaveWorkspace</c>, the file holds
    /// only the Demo tab after the restore, which is what the macOS pass found.
    /// </summary>
    [Test]
    public async Task A_saved_tab_whose_context_is_not_found_stays_in_the_workspace()
    {
        TestObjects.RedirectStores();
        WorkspaceStore.Save(new WorkspaceSettings(Theme: null, Tabs:
        [
            Missing,
            new TabSnapshot(ClusterContext.Demo.Name, ClusterContext.DemoKubeconfigPath),
        ], SelectedTabIndex: 1));

        using var shell = new MainWindowViewModel();
        await shell.Initialization;

        await Assert.That(shell.Tabs.Select(t => t.IsDemo)).IsEquivalentTo([true]);
        var saved = WorkspaceStore.Load();
        await Assert.That(saved.Tabs.Select(t => t.ContextName)).IsEquivalentTo([ClusterContext.Demo.Name, "sandbox"]);

        // A later save — the front tab changing, a mode switch — keeps it too, and the
        // selected index still counts the open tabs.
        shell.Mode = ShellMode.Resources;
        saved = WorkspaceStore.Load();
        await Assert.That(saved.Tabs.Any(t => t.ContextName == "sandbox" && t.KubeconfigPath == Missing.KubeconfigPath)).IsTrue();
        await Assert.That(saved.SelectedTabIndex).IsEqualTo(0);
    }

    /// <summary>
    /// The follow-up to #405: a tab kept saved but not reopened is said on screen (UI rule 9),
    /// by its context's name, with the kubeconfig it was saved from in the tooltip.
    /// Mutation-checked: without <c>RefreshUnrestoredTabsNotice</c> at the end of the restore,
    /// the notice stays null.
    /// </summary>
    [Test]
    public async Task A_saved_tab_not_reopened_is_named_in_the_status_bar()
    {
        TestObjects.RedirectStores();
        WorkspaceStore.Save(new WorkspaceSettings(Theme: null, Tabs:
        [
            Missing,
            new TabSnapshot(ClusterContext.Demo.Name, ClusterContext.DemoKubeconfigPath),
        ], SelectedTabIndex: 1));

        using var shell = new MainWindowViewModel();
        await shell.Initialization;

        await Assert.That(shell.UnrestoredTabsNotice)
            .IsEqualTo("1 saved tab not reopened: sandbox is not in the kubeconfig files read");
        await Assert.That(shell.UnrestoredTabsDetail).Contains(Missing.KubeconfigPath);
    }

    [Test]
    public async Task Every_saved_tab_reopened_leaves_no_notice()
    {
        TestObjects.RedirectStores();
        WorkspaceStore.Save(new WorkspaceSettings(Theme: null, Tabs:
        [
            new TabSnapshot(ClusterContext.Demo.Name, ClusterContext.DemoKubeconfigPath),
        ], SelectedTabIndex: 0));

        using var shell = new MainWindowViewModel();
        await shell.Initialization;

        await Assert.That(shell.UnrestoredTabsNotice).IsNull();
    }

    [Test]
    public async Task Several_missing_contexts_are_counted_and_named()
    {
        await Assert.That(MainWindowViewModel.UnrestoredTabsLine(["a", "b"]))
            .IsEqualTo("2 saved tabs not reopened: a and b are not in the kubeconfig files read");
        await Assert.That(MainWindowViewModel.UnrestoredTabsLine(["a", "b", "c"]))
            .IsEqualTo("3 saved tabs not reopened: a, b and c are not in the kubeconfig files read");
        await Assert.That(MainWindowViewModel.UnrestoredTabsLine([])).IsNull();
    }
}
