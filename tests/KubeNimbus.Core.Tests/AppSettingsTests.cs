using KubeNimbus.Core.Settings;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// <see cref="AppSettings.Normalized"/> is what stands between a hand-editable JSON file
/// and the running app, so what it accepts and what it rejects is a contract rather than
/// an implementation detail.
/// </summary>
public class AppSettingsTests
{
    // ------------------------------------------------------------------------ theme

    [Test]
    [Arguments("light")]
    [Arguments("dark")]
    [Arguments("system")]
    public async Task A_valid_theme_survives(string theme)
    {
        await Assert.That(new AppSettings { Theme = theme }.Normalized().Theme).IsEqualTo(theme);
    }

    /// <summary>
    /// The ThemeVariant spellings, which the command bar's toggle wrote into this file
    /// for a while: it persisted "Dark"/"Light" where everything reading the value
    /// expected "dark"/"light", so the theme was normalized straight back to "system" —
    /// i.e. to following the OS. On a machine whose OS is dark that is a toggle which
    /// enters dark and cannot leave, which is exactly how it was reported. Reading the
    /// casing costs nothing and means those files recover rather than losing the choice.
    /// </summary>
    [Test]
    [Arguments("Dark", "dark")]
    [Arguments("Light", "light")]
    [Arguments("SYSTEM", "system")]
    public async Task A_miscased_theme_is_read_rather_than_discarded(string written, string expected)
    {
        await Assert.That(new AppSettings { Theme = written }.Normalized().Theme).IsEqualTo(expected);
    }

    [Test]
    public async Task A_theme_that_is_not_a_theme_falls_back_to_the_system_one()
    {
        await Assert.That(new AppSettings { Theme = "chartreuse" }.Normalized().Theme).IsEqualTo("system");
    }

    [Test]
    [Arguments("Mac", "mac")]
    [Arguments("WINDOWS", "windows")]
    [Arguments("nonsense", "auto")]
    public async Task The_hotkey_scheme_is_canonicalized_the_same_way(string written, string expected)
    {
        await Assert.That(new AppSettings { HotkeyScheme = written }.Normalized().HotkeyScheme)
            .IsEqualTo(expected);
    }

    // ---------------------------------------------------------------- sidebar width

    [Test]
    public async Task The_sidebar_opens_at_the_default_width()
    {
        await Assert.That(new AppSettings().SidebarWidth).IsEqualTo(AppSettings.DefaultSidebarWidth);
    }

    /// <summary>
    /// A width past either bound leaves either no filter box to type in or no resource
    /// list to read, and there is no visible way back from the second one.
    /// </summary>
    [Test]
    public async Task A_sidebar_width_outside_the_bounds_is_clamped()
    {
        await Assert.That(new AppSettings { SidebarWidth = 10 }.Normalized().SidebarWidth)
            .IsEqualTo(AppSettings.MinSidebarWidth);

        await Assert.That(new AppSettings { SidebarWidth = 9000 }.Normalized().SidebarWidth)
            .IsEqualTo(AppSettings.MaxSidebarWidth);
    }

    /// <summary>
    /// A width of zero is what a JSON file with no such property deserializes to, and
    /// NaN is what a hand-edited one can produce. Neither may become the width.
    /// </summary>
    [Test]
    [Arguments(0d)]
    [Arguments(double.NaN)]
    [Arguments(double.PositiveInfinity)]
    public async Task A_sidebar_width_that_is_not_a_width_does_not_survive(double written)
    {
        var normalized = new AppSettings { SidebarWidth = written }.Normalized().SidebarWidth;

        await Assert.That(normalized).IsGreaterThanOrEqualTo(AppSettings.MinSidebarWidth);
        await Assert.That(normalized).IsLessThanOrEqualTo(AppSettings.MaxSidebarWidth);
    }

    // -------------------------------------------------------------- advanced view

    /// <summary>
    /// On by default: it governs which sidebar sections are shown, and a fresh install
    /// should be missing nothing until somebody asks for a shorter list.
    /// </summary>
    [Test]
    public async Task The_advanced_view_is_on_by_default()
    {
        await Assert.That(new AppSettings().IsAdvancedView).IsTrue();
        await Assert.That(new AppSettings().Normalized().IsAdvancedView).IsTrue();
    }
}
