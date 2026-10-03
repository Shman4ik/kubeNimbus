using KubeNimbus.App.ViewModels;
using Nimbus.Ui.Fonts;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The preferences page's two font choices (DESIGN.md rule 22): what each one opens on and
/// what it stores. That the resources reach text already on screen needs a laid-out window,
/// so it is the harness's <c>ux-font-settings</c> check (<c>FontChecks</c>), not a test here.
/// </summary>
[NotInParallel]
public class PreferencesFontTests
{
    [Test]
    public async Task A_fresh_page_opens_on_the_system_face_and_the_bundled_code_face()
    {
        await WithPage(async page =>
        {
            await Assert.That(page.InterfaceFontIndex).IsEqualTo(0);
            await Assert.That(page.SelectedCodeFont).IsEqualTo(CodeFontOption.Bundled);
            await Assert.That(page.CodeFonts[0]).IsEqualTo(CodeFontOption.Bundled);
        });
    }

    [Test]
    public async Task Choosing_an_interface_face_stores_it()
    {
        await WithPage(async page =>
        {
            page.InterfaceFontIndex = 1;
            await Assert.That(App.LoadSettings().InterfaceFont).IsEqualTo("inter");

            page.InterfaceFontIndex = 0;
            await Assert.That(App.LoadSettings().InterfaceFont).IsEqualTo("system");
        });
    }

    /// <summary>A stored "inter" opens on Inter; "auto" and "system" both open on System.</summary>
    [Test]
    [Arguments("inter", 1)]
    [Arguments("system", 0)]
    [Arguments("auto", 0)]
    public async Task The_page_opens_on_the_stored_interface_face(string stored, int expectedIndex)
    {
        await WithPage(
            async page => await Assert.That(page.InterfaceFontIndex).IsEqualTo(expectedIndex),
            before: () => App.Update(s => s with { InterfaceFont = stored }));
    }

    [Test]
    public async Task Choosing_a_code_face_stores_it_and_the_bundled_one_stores_null()
    {
        await WithPage(async page =>
        {
            var chosen = new CodeFontOption("Some Mono");
            page.SelectedCodeFont = chosen;
            await Assert.That(App.LoadSettings().CodeFont).IsEqualTo("Some Mono");

            page.SelectedCodeFont = CodeFontOption.Bundled;
            await Assert.That(App.LoadSettings().CodeFont).IsNull();
        });
    }

    /// <summary>
    /// A family chosen on another machine, or since uninstalled, stays listed and selected,
    /// so the box shows what is stored; the face falls back to the bundled one at draw time.
    /// Rebuilding the list when the installed families arrive is not a choice, so it writes
    /// nothing.
    /// </summary>
    [Test]
    public async Task A_stored_face_that_is_not_installed_stays_listed_and_selected()
    {
        await WithPage(
            async page =>
            {
                await Assert.That(page.SelectedCodeFont?.Name).IsEqualTo("Gone Mono");
                await Assert.That(page.CodeFonts.Count(option => option.Name == "Gone Mono")).IsEqualTo(1);

                await page.LoadInstalledCodeFontsAsync();

                await Assert.That(page.SelectedCodeFont?.Name).IsEqualTo("Gone Mono");
                await Assert.That(page.CodeFonts[0]).IsEqualTo(CodeFontOption.Bundled);
                await Assert.That(App.LoadSettings().CodeFont).IsEqualTo("Gone Mono");
            },
            before: () => App.Update(s => s with { CodeFont = "Gone Mono" }));
    }

    /// <summary>Each row previews its face, with the bundled one behind it for a name that does not resolve.</summary>
    [Test]
    public async Task Each_row_is_drawn_in_the_face_it_offers()
    {
        await Assert.That(CodeFontOption.Bundled.Family.Name).IsEqualTo(NimbusFonts.Mono(null).Name);
        await Assert.That(new CodeFontOption("Some Mono").Family.Name).IsEqualTo(NimbusFonts.Mono("Some Mono").Name);
        await Assert.That(CodeFontOption.Bundled.Label).Contains("JetBrains Mono");
    }

    private static async Task WithPage(Func<PreferencesViewModel, Task> test, Action? before = null)
    {
        // The installed list is read once per process; finishing that read first makes the
        // page's own load complete inside its constructor rather than on a pool thread
        // halfway through the test.
        try
        {
            await MonospaceFonts.InstalledAsync();
        }
        catch (Exception)
        {
            // A machine whose fonts cannot be listed still gets a page; that is the point.
        }

        TestObjects.RedirectStores();
        before?.Invoke();
        var shell = new MainWindowViewModel();
        try
        {
            shell.IsPreferencesOpen = true;
            await test(shell.Preferences!);
        }
        finally
        {
            shell.Dispose();
        }
    }
}
