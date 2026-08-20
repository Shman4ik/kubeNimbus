using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Views;

/// <summary>
/// The macOS menu bar. macOS puts a menu bar at the top of the screen whether an app
/// asks for one or not, so the only question is whether it carries this app's commands
/// or Avalonia's placeholder — and until this existed it carried the placeholder, whose
/// app menu is a lone "About Avalonia" item. That is why the app introduced itself under
/// the framework's name, and it is the same gap pgNimbus's native menu closes.
///
/// <para>
/// <b>It is macOS-only, on purpose.</b> UI rule 12 says this app has one row of chrome
/// and the command bar is it; a menu bar is a second row everywhere except macOS, where
/// the OS supplies the row regardless and leaving it empty is the anomaly. Win32 has no
/// native menu exporter at all, but X11 does (the freedesktop global-menu protocol), so
/// this is gated on the platform rather than left to be inert.
/// </para>
///
/// <para>
/// <b>Every item here is also reachable without it.</b> The ☰ menu and the command
/// palette already carry all of them, so a Mac gains a familiar route to commands it
/// already had, and no platform gains a command it did not — which is what keeps this
/// compatible with UI rule 1.
/// </para>
///
/// <para>
/// <b>Titles and gestures come from <see cref="CommandCatalog"/></b>, never typed here,
/// and the menu is rebuilt on <c>Hotkeys.Changed</c> exactly as the window's own key
/// bindings are: a <see cref="NativeMenuItem.Gesture"/> holds the modifier it was built
/// with, so a menu built once keeps printing the other platform's chord after someone
/// changes the scheme.
/// </para>
/// </summary>
internal static class MacMenu
{
    /// <summary>
    /// Builds the menu bar and attaches it to the window and to the application.
    /// Does nothing off macOS.
    /// </summary>
    internal static void Attach(MainWindow window, MainWindowViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(vm);

        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var state = new MenuState(window, vm);
        state.Build();

        void Rebuild() => state.Build();

        Nimbus.Ui.Hotkeys.Changed += Rebuild;
        vm.PropertyChanged += state.OnViewModelChanged;
        window.Unloaded += (_, _) =>
        {
            Nimbus.Ui.Hotkeys.Changed -= Rebuild;
            vm.PropertyChanged -= state.OnViewModelChanged;
        };
    }

    /// <summary>
    /// Holds the two checkable items between rebuilds so the shell's own toggles and the
    /// menu cannot disagree while both are on screen — the same rule the preferences page
    /// follows for the settings it proxies.
    /// </summary>
    private sealed class MenuState(MainWindow window, MainWindowViewModel vm)
    {
        private NativeMenuItem? _sidebar;
        private NativeMenuItem? _advanced;

        internal void Build()
        {
            // The application-level menu is the one macOS titles with the app's own name
            // (Application.Name — see App.axaml). Setting it replaces Avalonia's default.
            // Hide, Hide Others and Quit are appended by AppKit itself, so adding our own
            // would double them.
            if (Application.Current is { } app)
            {
                var appMenu = new NativeMenu();
                appMenu.Add(Item(CommandId.About, () => vm.ShowAboutCommand.Execute(null)));
                appMenu.Add(new NativeMenuItemSeparator());
                appMenu.Add(Item(CommandId.Preferences, () => vm.ShowPreferencesCommand.Execute(null)));
                NativeMenu.SetMenu(app, appMenu);
            }

            var cluster = new NativeMenu();
            cluster.Add(Item(CommandId.NewClusterTab, () => vm.AddNewTabCommand.Execute(null)));
            cluster.Add(Item(CommandId.ClusterSwitcher, () => vm.OpenSwitcherCommand.Execute(null)));
            cluster.Add(Item(CommandId.OpenDemoCluster, () => vm.OpenDemoClusterCommand.Execute(null)));
            cluster.Add(new NativeMenuItemSeparator());
            cluster.Add(Item(CommandId.OpenKubeconfigFile, () => vm.OpenKubeconfigFileCommand.Execute(null)));
            cluster.Add(Item(CommandId.RescanKubeconfig, () => vm.ReloadContextsCommand.Execute(null)));
            cluster.Add(new NativeMenuItemSeparator());

            // Resolved through SelectedTab at click time rather than captured: the menu
            // is rebuilt only when the hotkey scheme changes, and the selected tab
            // changes far more often than that — a captured command would act on the
            // wrong cluster, or on a closed one.
            cluster.Add(Item(CommandId.OpenTerminal, () => Run(vm.SelectedTab?.OpenInTerminalCommand)));
            cluster.Add(Item(CommandId.CloseClusterTab, () =>
            {
                if (vm.SelectedTab is { } tab && vm.CloseTabCommand.CanExecute(tab))
                {
                    vm.CloseTabCommand.Execute(tab);
                }
            }));

            _sidebar = Item(CommandId.ToggleSidebar, () => vm.IsSidebarVisible = !vm.IsSidebarVisible);
            _sidebar.ToggleType = MenuItemToggleType.CheckBox;
            _sidebar.IsChecked = vm.IsSidebarVisible;

            _advanced = Item(CommandId.ToggleAdvancedView, () => vm.IsAdvancedView = !vm.IsAdvancedView);
            _advanced.ToggleType = MenuItemToggleType.CheckBox;
            _advanced.IsChecked = vm.IsAdvancedView;

            var view = new NativeMenu();
            view.Add(Item(CommandId.CommandPalette, window.OpenPalette));
            view.Add(new NativeMenuItemSeparator());
            view.Add(_sidebar);
            view.Add(_advanced);
            view.Add(Item(CommandId.ToggleTheme, window.ToggleTheme));

            var help = new NativeMenu();
            help.Add(Item(CommandId.ShortcutsWindow, () => vm.ToggleShortcutsCommand.Execute(null)));

            var bar = new NativeMenu();
            bar.Add(new NativeMenuItem("Cluster") { Menu = cluster });
            bar.Add(new NativeMenuItem("View") { Menu = view });
            bar.Add(new NativeMenuItem("Help") { Menu = help });

            NativeMenu.SetMenu(window, bar);
        }

        internal void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarVisible) && _sidebar is { } sidebar)
            {
                sidebar.IsChecked = vm.IsSidebarVisible;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsAdvancedView) && _advanced is { } advanced)
            {
                advanced.IsChecked = vm.IsAdvancedView;
            }
        }

        private static void Run(System.Windows.Input.ICommand? command)
        {
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
        }

        /// <summary>
        /// One menu row. The action is a plain <c>Click</c> handler rather than a bound
        /// <c>Command</c> because half of these belong to the selected tab or to the
        /// window itself, and a NativeMenuItem built in code has no binding to keep a
        /// captured command current.
        /// </summary>
        private static NativeMenuItem Item(CommandId id, Action run)
        {
            var item = new NativeMenuItem(CommandCatalog.TitleFor(id))
            {
                Gesture = CommandBindings.GestureFor(id),
            };

            item.Click += (_, _) => run();
            return item;
        }
    }
}
