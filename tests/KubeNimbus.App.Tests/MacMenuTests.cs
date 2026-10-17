using Avalonia.Controls;
using Avalonia.Input;
using KubeNimbus.App.Platform;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The macOS menu bar as built (<see cref="MacMenu.CreateMenuBar"/>,
/// <see cref="MacAppMenu"/>), read off the <see cref="NativeMenu"/> objects the platform is
/// handed. Whether macOS shows them is the running app's half, checked on a Mac by dumping the
/// menu bar through System Events (the PR records the listing); these pin what is handed over:
/// the app menu's own rows and Avalonia's corrected standard block, Edit and Window, and the
/// tab chords.
///
/// <para>
/// <c>[NotInParallel]</c>: the scheme is process-global, and every test restores "auto".
/// </para>
/// </summary>
[NotInParallel]
public class MacMenuTests
{
    private static MacMenu.WindowActions Actions(List<string> log) => new(
        () => log.Add("palette"),
        () => log.Add("theme"),
        () => log.Add("find"),
        command => log.Add($"edit:{command}"),
        () => log.Add("minimize"),
        () => log.Add("zoom"),
        () => true,
        () => log.Add("front"));

    private static NativeMenu Menu(NativeMenu bar, string header) =>
        bar.Items.OfType<NativeMenuItem>().Single(i => i.Header == header).Menu!;

    private static NativeMenuItem Item(NativeMenu menu, string header) =>
        menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == header);

    /// <summary>
    /// The standard block as Avalonia.Native 12.1.4 appends it: Hide Others on ⌥⌘Q, one
    /// modifier from quitting, and a bare "Quit". Corrected in place, because the exporter
    /// only watches the items of the menu it exported.
    /// </summary>
    [Test]
    public async Task The_standard_block_gets_hide_others_on_option_cmd_h_and_quit_named_for_the_app()
    {
        var menu = MacAppMenu.Create("kubeNimbus", () => { }, () => { });
        var hideOthers = new NativeMenuItem("Hide Others") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta | KeyModifiers.Alt) };
        var quit = new NativeMenuItem("Quit") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
        menu.Add(hideOthers);
        menu.Add(quit);

        var changed = MacAppMenu.FixStandardItems(menu, "kubeNimbus");

        await Assert.That(changed).IsEqualTo(2);
        await Assert.That(hideOthers.Gesture).IsEqualTo(new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt));
        await Assert.That(quit.Header).IsEqualTo("Quit kubeNimbus");
        await Assert.That(MacAppMenu.FixStandardItems(menu, "kubeNimbus")).IsEqualTo(1 - 1);
    }

    [Test]
    public async Task The_app_menu_names_the_app_and_puts_settings_on_cmd_comma()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("mac");
            var log = new List<string>();
            var menu = MacAppMenu.Create("kubeNimbus", () => log.Add("about"), () => log.Add("settings"));

            var headers = menu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).Select(i => i.Header ?? "").ToList();
            await Assert.That(headers).IsEquivalentTo(["About kubeNimbus", "Settings…"]);
            await Assert.That(MacAppMenu.SettingsItem(menu)!.Gesture).IsEqualTo(new KeyGesture(Key.OemComma, KeyModifiers.Meta));

            Nimbus.Ui.Hotkeys.Initialize("windows");
            MacAppMenu.RefreshGestures(menu);
            await Assert.That(MacAppMenu.SettingsItem(menu)!.Gesture).IsEqualTo(new KeyGesture(Key.OemComma, KeyModifiers.Control));
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    [Test]
    public async Task The_bar_has_edit_and_window_menus_with_the_platform_chords()
    {
        TestObjects.RedirectStores();
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("mac");
            using var shell = new MainWindowViewModel();
            var log = new List<string>();
            var bar = MacMenu.CreateMenuBar(shell, Actions(log));

            var headers = bar.Items.OfType<NativeMenuItem>().Select(i => i.Header ?? "").ToList();
            await Assert.That(headers).IsEquivalentTo(["Cluster", "Edit", "View", "Window", "Help"]);

            var edit = Menu(bar, "Edit");
            await Assert.That(Item(edit, "Copy").Gesture).IsEqualTo(new KeyGesture(Key.C, KeyModifiers.Meta));
            await Assert.That(Item(edit, "Paste").Gesture).IsEqualTo(new KeyGesture(Key.V, KeyModifiers.Meta));
            await Assert.That(Item(edit, "Redo").Gesture).IsEqualTo(new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift));
            await Assert.That(Item(edit, "Find…").Gesture).IsEqualTo(new KeyGesture(Key.F, KeyModifiers.Meta));

            var window = Menu(bar, "Window");
            await Assert.That(Item(window, "Minimize").Gesture).IsEqualTo(new KeyGesture(Key.M, KeyModifiers.Meta));

            var cluster = Menu(bar, "Cluster");
            await Assert.That(Item(cluster, CommandCatalog.TitleFor(CommandId.NewClusterTab)).Gesture)
                .IsEqualTo(new KeyGesture(Key.T, KeyModifiers.Meta));
            await Assert.That(Item(cluster, CommandCatalog.TitleFor(CommandId.CloseClusterTab)).Gesture)
                .IsEqualTo(new KeyGesture(Key.W, KeyModifiers.Meta));
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    /// <summary>
    /// Under the Windows scheme on a Mac every Cmd chord becomes literal Ctrl, and a menu
    /// gesture is a key equivalent AppKit takes before the exec terminal sees the key: ⌃W
    /// there would close the tab instead of deleting the shell's word. So those chords are
    /// left off the menu — the window binding still answers them, and yields to the shell.
    /// The Edit menu's ⌘ stays, being the platform's text keys whatever the scheme.
    /// </summary>
    [Test]
    public async Task Under_the_windows_scheme_a_chord_the_shell_owns_is_not_a_menu_key_equivalent()
    {
        TestObjects.RedirectStores();
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("windows");
            using var shell = new MainWindowViewModel();
            var bar = MacMenu.CreateMenuBar(shell, Actions([]));

            var cluster = Menu(bar, "Cluster");
            await Assert.That(Item(cluster, CommandCatalog.TitleFor(CommandId.CloseClusterTab)).Gesture).IsNull();
            await Assert.That(Item(Menu(bar, "Edit"), "Copy").Gesture).IsEqualTo(new KeyGesture(Key.C, KeyModifiers.Meta));
            await Assert.That(MacMenu.MenuGesture(CommandId.Preferences)).IsEqualTo(new KeyGesture(Key.OemComma, KeyModifiers.Control));
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    [Test]
    public async Task Close_cluster_tab_follows_whether_there_is_a_tab_to_close()
    {
        TestObjects.RedirectStores();
        using var shell = new MainWindowViewModel();
        await shell.Initialization;
        var bar = MacMenu.CreateMenuBar(shell, Actions([]));
        var close = Item(Menu(bar, "Cluster"), CommandCatalog.TitleFor(CommandId.CloseClusterTab));

        await Assert.That(close.IsEnabled).IsFalse();
    }
}
