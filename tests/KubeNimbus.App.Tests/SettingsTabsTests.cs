using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The preferences page is four tabs (2026-10, after pgNimbus) and reopens on the tab it
/// was left on, for the rest of the session. Its view model is rebuilt on every open, so
/// the tab has to live on the shell. The rendered half — four tabs, one height on every
/// tab, the strip not moving — needs a window and is the harness's <c>ux-preferences-tabs</c>.
/// </summary>
[NotInParallel]
public class SettingsTabsTests
{
    [Test]
    public async Task The_page_opens_on_General_the_first_time()
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        try
        {
            shell.IsPreferencesOpen = true;

            await Assert.That(shell.Preferences!.SelectedTab).IsEqualTo(0);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [Test]
    public async Task Reopening_the_page_lands_on_the_tab_it_was_left_on()
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        try
        {
            shell.IsPreferencesOpen = true;
            var first = shell.Preferences!;
            first.SelectedTab = 2;
            shell.IsPreferencesOpen = false;
            shell.IsPreferencesOpen = true;

            await Assert.That(shell.Preferences).IsNotSameReferenceAs(first);
            await Assert.That(shell.Preferences!.SelectedTab).IsEqualTo(2);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [Test]
    public async Task The_tab_is_not_persisted()
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        try
        {
            shell.IsPreferencesOpen = true;
            shell.Preferences!.SelectedTab = 3;
        }
        finally
        {
            shell.Dispose();
        }

        var next = new MainWindowViewModel();
        try
        {
            next.IsPreferencesOpen = true;

            await Assert.That(next.Preferences!.SelectedTab).IsEqualTo(0);
        }
        finally
        {
            next.Dispose();
        }
    }
}
