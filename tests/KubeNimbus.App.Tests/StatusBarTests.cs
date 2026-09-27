using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The shell's status bar is shown only while the selected tab has something to say
/// (<see cref="ClusterTabViewModel.IsStatusWorthShowing"/>). A healthy tab's "Connected"
/// line is not worth a row; anything else is.
/// </summary>
public class StatusBarTests
{
    [Test]
    public async Task A_connected_tab_hides_the_bar_until_there_is_something_to_say()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);

        // Before connecting, "Not connected." is news.
        await Assert.That(tab.IsStatusWorthShowing).IsTrue();

        tab.ConnectCommand.Execute(null);
        await Assert.That(tab.Status).IsEqualTo(ClusterTabViewModel.DemoStatus);
        await Assert.That(tab.IsStatusWorthShowing).IsFalse();

        // A warning brings the bar back, and so does any other status.
        tab.ConnectionWarning = "Watch lost its connection.";
        await Assert.That(tab.IsStatusWorthShowing).IsTrue();
        tab.ConnectionWarning = null;
        await Assert.That(tab.IsStatusWorthShowing).IsFalse();

        tab.Status = "Watch ended: gone";
        await Assert.That(tab.IsStatusWorthShowing).IsTrue();
    }
}
