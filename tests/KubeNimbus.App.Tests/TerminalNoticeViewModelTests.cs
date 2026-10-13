using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The terminal notice as a child of the cluster tab (ENG-13, #361), exercised on its own:
/// no tab, no cluster, no terminal. <see cref="ClusterTabTerminalTests"/> pins the wording;
/// these pin the notice's state and its two commands.
/// </summary>
public class TerminalNoticeViewModelTests
{
    [Test]
    public async Task Opening_on_the_demo_cluster_refuses_in_place_and_dismiss_clears_it()
    {
        TestObjects.RedirectStores();
        var notice = new TerminalNoticeViewModel(ClusterContext.Demo);

        await notice.OpenInTerminalCommand.ExecuteAsync(null);

        await Assert.That(notice.Notice).IsNotNull();
        await Assert.That(notice.Notice!).Contains("demo cluster");
        await Assert.That(notice.IsWarning).IsTrue();
        await Assert.That(notice.IsError).IsFalse();

        notice.DismissCommand.Execute(null);
        await Assert.That(notice.Notice).IsNull();
    }

    /// <summary>
    /// A new sentence takes its own severity: an error left by the last launch must not colour
    /// the next "Opening…" red, which is why every write goes through <c>Show</c>.
    /// </summary>
    [Test]
    public async Task A_new_sentence_replaces_the_last_ones_severity()
    {
        var notice = new TerminalNoticeViewModel(ClusterContext.Demo);
        notice.Show("No terminal could be opened.", error: true);
        await Assert.That(notice.IsError).IsTrue();

        notice.Show("Opening your terminal…");

        await Assert.That(notice.Notice).IsEqualTo("Opening your terminal…");
        await Assert.That(notice.IsError).IsFalse();
        await Assert.That(notice.IsWarning).IsFalse();
    }

    /// <summary>
    /// The connection-failure view shows the same notice, and follows it by its change
    /// notification; a notice that changed without one would leave that view stale.
    /// </summary>
    [Test]
    public async Task A_new_notice_is_announced()
    {
        var notice = new TerminalNoticeViewModel(ClusterContext.Demo);
        List<string?> changed = [];
        notice.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        notice.Show("Opened xterm on payments-prod.", warning: true);
        notice.DismissCommand.Execute(null);

        await Assert.That(changed.Count(n => n == nameof(TerminalNoticeViewModel.Notice))).IsEqualTo(2);
        await Assert.That(changed).Contains(nameof(TerminalNoticeViewModel.IsWarning));
    }
}
