using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #373: the palette's selected-row items (logs, previous logs, exec, port-forward, Edit YAML,
/// delete and the rest) act on the Resources mode's selected row, which the Applications mode
/// hides. In the Applications mode they are not offered for that row; the open Application
/// page's own actions are offered instead, and land on the page's strip. Driven through the
/// real shell and the demo tab, as <see cref="ArgoPaletteTargetTests"/> is.
/// </summary>
[NotInParallel]
public class ApplicationsModePaletteTests
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

    private static List<PaletteItem> Items(MainWindowViewModel shell)
    {
        shell.Palette.Open();
        var items = shell.Palette.FilteredItems.ToList();
        shell.Palette.Close();
        return items;
    }

    private static string Title(CommandId id) => CommandCatalog.Get(id).Title;

    /// <summary>
    /// The issue's sequence: a pod selected in the Resources mode, then the Applications mode.
    /// No item names that pod, and none of the row items is offered at all, Delete included.
    /// Mutation-checked: removing the <c>IsApplicationsMode</c> branch in
    /// <c>BuildPaletteItems</c> offers Delete resource, Logs and Exec for the hidden pod again.
    /// </summary>
    [Test]
    public async Task In_the_applications_mode_no_item_acts_on_the_hidden_resources_row()
    {
        var (shell, tab) = Shell();

        shell.Mode = ShellMode.Resources;
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.Descriptor is { Group: "", Kind: "Pod" }));
        tab.SelectedRow = tab.Rows.First();
        var where = $"{tab.SelectedRow!.Namespace}/{tab.SelectedRow.Name}";

        var resources = Items(shell);
        await Assert.That(resources.Any(i => i.Title == Title(CommandId.DeleteResource) && i.Subtitle.StartsWith(where, StringComparison.Ordinal))).IsTrue();
        await Assert.That(resources.Any(i => i.Title == Title(CommandId.Exec))).IsTrue();

        shell.Mode = ShellMode.Applications;
        var applications = Items(shell);

        await Assert.That(applications.Where(i => i.Subtitle.StartsWith(where, StringComparison.Ordinal)).Select(i => i.Title)).IsEmpty();
        string[] rowOnly =
        [
            Title(CommandId.DeleteResource), Title(CommandId.PodLogs), Title(CommandId.PreviousLogs),
            Title(CommandId.Exec), Title(CommandId.PortForward), Title(CommandId.EditYaml), "Exec in my terminal",
        ];
        await Assert.That(applications.Where(i => rowOnly.Contains(i.Title)).Select(i => i.Title)).IsEmpty();
        await Assert.That(tab.SelectedRow).IsNotNull();
    }

    /// <summary>
    /// #380: "Unhealthy only" (and its way out) and "Reset columns" narrow or reset the Resources
    /// list, which the Applications mode hides, so there they are not offered; back in the
    /// Resources mode they are again. Mutation-checked: dropping either <c>!IsApplicationsMode</c>
    /// gate in <c>BuildPaletteItems</c> offers that item in the Applications mode again.
    /// </summary>
    [Test]
    public async Task In_the_applications_mode_the_resource_list_items_are_not_offered()
    {
        var (shell, tab) = Shell();
        shell.Mode = ShellMode.Resources;
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.Descriptor is { Group: "", Kind: "Pod" }));
        tab.SetColumnHidden(ResourceColumn.Restarts, hidden: true);

        var unhealthyOn = Title(CommandId.ToggleUnhealthyOnly);
        const string unhealthyOff = "Show every row, healthy ones too";
        var reset = Title(CommandId.ResetListColumns);

        var resources = Items(shell);
        await Assert.That(resources.Any(i => i.Title == unhealthyOn)).IsTrue();
        await Assert.That(resources.Any(i => i.Title == reset)).IsTrue();

        shell.Mode = ShellMode.Applications;
        var applications = Items(shell);
        await Assert.That(applications.Where(i => i.Title == unhealthyOn || i.Title == reset).Select(i => i.Title)).IsEmpty();

        // The way out of the mode is not offered either: it, too, changes the hidden list.
        shell.Mode = ShellMode.Resources;
        tab.IsUnhealthyOnly = true;
        await Assert.That(Items(shell).Any(i => i.Title == unhealthyOff)).IsTrue();
        shell.Mode = ShellMode.Applications;
        await Assert.That(Items(shell).Any(i => i.Title == unhealthyOff)).IsFalse();
        await Assert.That(tab.IsUnhealthyOnly).IsTrue();
        tab.ResetColumns();
    }

    /// <summary>
    /// With an Application's page open, the palette offers what the page offers — Edit YAML
    /// and Rollout restart, named after the application — and a restart asks on the page's own
    /// strip, never on the hidden Resources strip. Delete is still not offered. The subtitles
    /// carry no list key: mutation-checked by building them with <c>Catalog()</c> again, which
    /// appends " · R" and " · E".
    /// </summary>
    [Test]
    public async Task With_a_page_open_the_palette_offers_the_pages_own_actions()
    {
        var (shell, tab) = Shell();
        shell.Mode = ShellMode.Resources;
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.Descriptor is { Group: "", Kind: "Pod" }));
        tab.SelectedRow = tab.Rows.First();

        shell.Mode = ShellMode.Applications;
        // The first application whose page can restart something (a Job-only one cannot).
        ApplicationPageViewModel? page = null;
        foreach (var row in tab.Applications.Rows.ToList())
        {
            tab.Applications.Open(row);
            if (tab.Applications.Page is { CanRestart: true, CanEditYaml: true } candidate)
            {
                page = candidate;
                break;
            }
        }

        await Assert.That(page).IsNotNull();

        var items = Items(shell);
        var restart = items.Single(i => i.Title == Title(CommandId.RolloutRestart));
        // Exact text, with no key suffix: the page handles no R or E key, so the list's
        // chords must not be advertised here (UI rule 20).
        await Assert.That(restart.Subtitle).IsEqualTo($"{page!.Name} · roll its pods");
        await Assert.That(items.Single(i => i.Title == Title(CommandId.EditYaml)).Subtitle).IsEqualTo($"{page!.Name} · its workload");
        await Assert.That(items.Any(i => i.Title == Title(CommandId.DeleteResource))).IsFalse();

        restart.Execute!();
        await Assert.That(tab.PendingRowAction).IsNull();
        await Assert.That(page!.PendingAction!.Kind).IsEqualTo(RowActionKind.Restart);
    }
}
