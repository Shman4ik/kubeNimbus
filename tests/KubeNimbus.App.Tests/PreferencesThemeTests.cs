using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Theme dropdown on the preferences page and the theme the app actually has must not
/// disagree. Found by the 0.5.1 release pass: with the page open, the command bar's toggle
/// (also reachable from the palette and the macOS View menu) changed the app to dark and the
/// dropdown went on reading the theme the page had opened on, because the page's view model
/// read the setting once, at construction.
/// </summary>
[NotInParallel]
public class PreferencesThemeTests
{
    [Test]
    [Arguments("dark", 2)]
    [Arguments("light", 1)]
    [Arguments("system", 0)]
    public async Task A_theme_chosen_while_the_page_is_open_moves_the_dropdown(string chosen, int expectedIndex)
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        try
        {
            // Open on a theme that differs from every choice above, so each case moves.
            App.SetTheme(chosen == "dark" ? "light" : "dark");
            shell.IsPreferencesOpen = true;
            var page = shell.Preferences!;
            await Assert.That(page.ThemeIndex).IsNotEqualTo(expectedIndex);

            shell.PersistTheme(chosen);

            await Assert.That(page.ThemeIndex).IsEqualTo(expectedIndex);
            await Assert.That(App.LoadSettings().Theme).IsEqualTo(chosen);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [Test]
    public async Task A_page_that_opens_after_the_toggle_reads_the_stored_theme()
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        try
        {
            shell.PersistTheme("dark");
            shell.IsPreferencesOpen = true;

            await Assert.That(shell.Preferences!.ThemeIndex).IsEqualTo(2);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [Test]
    public async Task Choosing_a_theme_on_the_page_still_stores_it()
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        try
        {
            shell.IsPreferencesOpen = true;
            shell.Preferences!.ThemeIndex = 1;

            await Assert.That(App.LoadSettings().Theme).IsEqualTo("light");
        }
        finally
        {
            shell.Dispose();
        }
    }
}
