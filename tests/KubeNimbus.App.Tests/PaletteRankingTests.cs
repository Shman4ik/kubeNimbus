using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The palette ranks a title match above a subtitle match. Found on the 2026-10-17 macOS
/// pass: "Nodes" put "Advanced view: show only everyday kinds" first, because its subtitle
/// lists Nodes, and Enter then turned the Advanced view off instead of opening the kind.
/// </summary>
[NotInParallel]
public class PaletteRankingTests
{
    private static PaletteItem Row(string title, string subtitle = "") => new(title, subtitle, "", () => { });

    [Test]
    [Arguments("Nodes")]
    [Arguments("Services")]
    [Arguments("nodes")]
    public async Task A_kind_named_by_the_query_is_the_top_match_in_the_real_shell(string query)
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        shell.Tabs.Add(tab);
        shell.SelectedTab = tab;
        shell.Mode = ShellMode.Resources;
        shell.IsAdvancedView = true;

        shell.Palette.Open(query);
        var top = shell.Palette.SelectedItem;
        var advancedStillMatches = shell.Palette.FilteredItems.Any(i => i.Title.StartsWith("Advanced view", StringComparison.Ordinal));
        shell.Palette.Close();

        // The precondition the bug needs: the Advanced view row still matches on its subtitle.
        await Assert.That(advancedStillMatches).IsTrue();
        await Assert.That(top?.Title).IsEqualTo(query, StringComparer.OrdinalIgnoreCase);
    }

    [Test]
    public async Task Title_rank_orders_exact_prefix_word_start_contains_then_subtitle()
    {
        var items = new[]
        {
            Row("Toggle", "mentions pods"),
            Row("Mypods"),
            Row("Logs: pods-api"),
            Row("Pods and more"),
            Row("Pods"),
        };
        var palette = new CommandPaletteViewModel(() => items);

        palette.Open("pods");
        var order = palette.FilteredItems.Select(i => i.Title).ToList();

        await Assert.That(order).IsEquivalentTo(
            new[] { "Pods", "Pods and more", "Logs: pods-api", "Mypods", "Toggle" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Within_a_rank_the_source_order_stands()
    {
        var items = new[] { Row("Deployments", "apps"), Row("Deployments", "other group") };
        var palette = new CommandPaletteViewModel(() => items);

        palette.Open("deploy");

        await Assert.That(palette.FilteredItems.Select(i => i.Subtitle).ToList())
            .IsEquivalentTo(new[] { "apps", "other group" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
