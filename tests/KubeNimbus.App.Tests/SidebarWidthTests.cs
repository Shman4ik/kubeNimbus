using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The sidebar's width is shell-owned and dragged per tab, so it travels in both
/// directions: the shell stamps it onto each tab, and a drag on one tab's splitter has
/// to reach the shell — which is what persists it and mirrors it onto the others.
///
/// <para>
/// The write-back is the half worth pinning, because losing it fails invisibly: the
/// column resizes, the tab's own property follows, and the width is simply gone on the
/// next tab switch or restart. A headless drag probe is what caught it; nothing a
/// screenshot renders would have.
/// </para>
/// </summary>
public class SidebarWidthTests
{
    [Test]
    public async Task A_new_tab_opens_at_the_default_width()
    {
        var tab = TestObjects.Tab();

        await Assert.That(tab.SidebarWidth).IsEqualTo(AppSettings.DefaultSidebarWidth);
    }

    /// <summary>
    /// The write-back the view calls when a drag ends. Same shape as
    /// <c>AdvancedViewChanged</c>: a tab still knows nothing about its siblings.
    /// </summary>
    [Test]
    public async Task A_width_change_reaches_the_shell()
    {
        var tab = TestObjects.Tab();
        double? reported = null;
        tab.SidebarWidthChanged = value => reported = value;

        tab.SidebarWidth = 300;

        await Assert.That(reported).IsEqualTo(300);
    }

    /// <summary>
    /// Stamping a tab with the shell's current width must not echo back as a fresh
    /// drag. The shell assigns the value before wiring the callback for exactly this
    /// reason, and an unchanged assignment raises nothing either way.
    /// </summary>
    [Test]
    public async Task Restamping_the_same_width_does_not_echo()
    {
        var tab = TestObjects.Tab();
        tab.SidebarWidth = 300;

        var echoes = 0;
        tab.SidebarWidthChanged = _ => echoes++;
        tab.SidebarWidth = 300;

        await Assert.That(echoes).IsEqualTo(0);
    }
}
