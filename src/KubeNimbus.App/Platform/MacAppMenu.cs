using Avalonia.Controls;
using Avalonia.Input;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Platform;

/// <summary>
/// The macOS application menu — the one titled "kubeNimbus" — and the corrections to the
/// standard block Avalonia appends to it. Ported from pgNimbus (<c>MacAppMenu</c> and the
/// app menu its <c>App.axaml</c> declares).
///
/// <para>
/// <b>Why this has to be set before the platform finishes starting.</b> Avalonia.Native
/// builds the application-menu exporter in an <c>AfterSetup</c> callback, which runs after
/// <c>Application.Initialize</c> has loaded the XAML and before
/// <c>OnFrameworkInitializationCompleted</c>. The exporter reads
/// <c>NativeMenu.GetMenu(Application)</c> once, in its constructor; finding nothing, it
/// creates its own placeholder ("About Avalonia"), stores it on the application and exports
/// that. It never subscribes to the property afterwards. So a menu set later — which is what
/// kubeNimbus did, from the main window's <c>DataContextChanged</c> — is written to an
/// attached property nothing reads, and the placeholder stays on screen for the life of the
/// process. Setting it from <c>Initialize</c> (see <c>App.Initialize</c>) is in time; what the
/// exporter does watch afterwards is the items of the menu it exported, so later changes are
/// made to those items in place.
/// </para>
///
/// <para>
/// Avalonia.Native then appends Services, Hide, Hide Others, Show All and Quit to it itself
/// (<c>AvaloniaNativeMenuExporter.PopulateStandardOSXMenuItems</c>), and two of them are
/// wrong as of 12.1.4: Hide Others is bound to ⌥⌘Q instead of ⌥⌘H — one modifier away from
/// quitting — and Quit reads "Quit" where every Mac app says "Quit &lt;name&gt;". Turning the
/// block off and building our own is not an option: the Hide / Hide Others / Show All
/// commands and the Services submenu flag are internal to Avalonia.Native. Its items are
/// ordinary <see cref="NativeMenuItem"/>s in our own menu, though, and the exporter watches
/// their Header and Gesture, so they are corrected in place
/// (<see cref="FixStandardItems"/>). Items are found by the headers Avalonia gives them, so if
/// a later Avalonia fixes either one, this finds nothing to change.
/// </para>
/// </summary>
internal static class MacAppMenu
{
    /// <summary>The header macOS uses for the preferences item since Ventura.</summary>
    internal const string SettingsHeader = "Settings…";

    /// <summary>The standard Hide Others gesture, ⌥⌘H.</summary>
    internal static KeyGesture HideOthersGesture { get; } = new(Key.H, KeyModifiers.Meta | KeyModifiers.Alt);

    /// <summary>
    /// The app menu's own rows: About kubeNimbus, then Settings… on the catalog's preferences
    /// chord. Built on every platform so a test can read it; only macOS is given it.
    /// </summary>
    internal static NativeMenu Create(string appName, Action about, Action settings)
    {
        ArgumentNullException.ThrowIfNull(about);
        ArgumentNullException.ThrowIfNull(settings);

        var aboutItem = new NativeMenuItem($"About {appName}");
        aboutItem.Click += (_, _) => about();

        var settingsItem = new NativeMenuItem(SettingsHeader) { Gesture = CommandBindings.GestureFor(CommandId.Preferences) };
        settingsItem.Click += (_, _) => settings();

        var menu = new NativeMenu();
        menu.Add(aboutItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(settingsItem);
        return menu;
    }

    /// <summary>
    /// Re-reads Settings…'s gesture from the catalog, for a hotkey-scheme change. In place,
    /// because the exporter only watches the menu it exported (see the type's remarks).
    /// </summary>
    internal static void RefreshGestures(NativeMenu? appMenu)
    {
        if (SettingsItem(appMenu) is { } item)
        {
            item.Gesture = CommandBindings.GestureFor(CommandId.Preferences);
        }
    }

    /// <summary>The app menu's Settings… item, if it has one.</summary>
    internal static NativeMenuItem? SettingsItem(NativeMenu? appMenu) =>
        appMenu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == SettingsHeader);

    /// <summary>
    /// Rebinds Hide Others to ⌥⌘H and names the app in the Quit item. Returns the number of
    /// items changed.
    /// </summary>
    internal static int FixStandardItems(NativeMenu? appMenu, string appName)
    {
        if (appMenu is null)
        {
            return 0;
        }

        var changed = 0;
        foreach (var item in appMenu.Items.OfType<NativeMenuItem>())
        {
            if (item.Header == "Hide Others" && !HideOthersGesture.Equals(item.Gesture))
            {
                item.Gesture = HideOthersGesture;
                changed++;
            }
            else if (item.Header == "Quit")
            {
                item.Header = $"Quit {appName}";
                changed++;
            }
        }

        return changed;
    }
}
