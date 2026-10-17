using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Views;

/// <summary>
/// The macOS menu bar's window-scoped menus: Cluster, Edit, View, Window and Help. The
/// application menu (About kubeNimbus, Settings…, Hide, Quit) is the application's, not the
/// window's, and lives in <see cref="Platform.MacAppMenu"/>: it has to be set before the
/// platform finishes starting, which a window cannot do (see there for the measured cause).
///
/// <para>
/// <b>It is macOS-only, on purpose.</b> UI rule 12 says this app has one row of chrome
/// and the command bar is it; a menu bar is a second row everywhere except macOS, where
/// the OS supplies the row regardless and leaving it empty is the anomaly. Win32 has no
/// native menu exporter at all, but X11 does (the freedesktop global-menu protocol), so
/// <see cref="Attach"/> is gated on the platform rather than left to be inert. The bar
/// itself is built on every platform (<see cref="CreateMenuBar"/>) so a test can read it.
/// </para>
///
/// <para>
/// <b>Every item here is also reachable without it.</b> The ☰ menu, the command palette and
/// the window's key bindings already carry the app's commands, and Edit and Window carry the
/// platform's own verbs, so a Mac gains a familiar route and no platform gains a command.
/// </para>
///
/// <para>
/// <b>Titles and gestures come from <see cref="CommandCatalog"/></b>, never typed here,
/// and the bar is rebuilt on <c>Hotkeys.Changed</c> exactly as the window's own key
/// bindings are: a <see cref="NativeMenuItem.Gesture"/> holds the modifier it was built
/// with. A menu gesture is a key equivalent, which AppKit matches before the focused control
/// sees the key, so a chord the exec terminal's shell owns (literal Ctrl on a letter — what
/// every Cmd chord becomes under the Windows scheme) is left off the menu
/// (<see cref="MenuGesture"/>); the window binding still answers it and yields to the shell.
/// </para>
/// </summary>
internal static class MacMenu
{
    /// <summary>What the bar does that belongs to the window rather than to its view model.</summary>
    internal sealed record WindowActions(
        Action OpenPalette,
        Action ToggleTheme,
        Action FocusRowFilter,
        Action<EditCommand> Edit,
        Action Minimize,
        Action Zoom,
        Func<bool> CanZoom,
        Action BringAllToFront);

    /// <summary>
    /// Builds the menu bar and attaches it to the window. Does nothing off macOS.
    /// </summary>
    internal static void Attach(MainWindow window, MainWindowViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(vm);

        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var actions = new WindowActions(
            window.OpenPalette,
            window.ToggleTheme,
            window.FocusRowFilter,
            command => EditCommands.Execute(window, command),
            () => window.WindowState = WindowState.Minimized,
            () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized,
            () => window.CanMaximize,
            () =>
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
            });

        var state = new MenuState(vm, actions);
        void Rebuild() => NativeMenu.SetMenu(window, state.Build());

        Rebuild();
        Nimbus.Ui.Hotkeys.Changed += Rebuild;
        vm.PropertyChanged += state.OnViewModelChanged;
        window.Unloaded += (_, _) =>
        {
            Nimbus.Ui.Hotkeys.Changed -= Rebuild;
            vm.PropertyChanged -= state.OnViewModelChanged;
        };
    }

    /// <summary>The bar, on any platform: what <see cref="Attach"/> sets on macOS.</summary>
    internal static NativeMenu CreateMenuBar(MainWindowViewModel vm, WindowActions actions) =>
        new MenuState(vm, actions).Build();

    /// <summary>
    /// The gesture a catalog command shows in the bar, or null when AppKit taking it as a key
    /// equivalent would rob the exec terminal of a key its shell reads (see the type's
    /// remarks). Under the Cmd scheme every app chord is ⌘-based and passes through.
    /// </summary>
    internal static KeyGesture? MenuGesture(CommandId id) =>
        CommandBindings.GestureFor(id) is { } gesture && !CommandBindings.ShellOwns(gesture) ? gesture : null;

    /// <summary>
    /// Holds the items whose state follows the shell — the two checkable ones and Close
    /// cluster tab — so the menu and the command bar cannot disagree while both are on
    /// screen.
    /// </summary>
    private sealed class MenuState(MainWindowViewModel vm, WindowActions actions)
    {
        private NativeMenuItem? _sidebar;
        private NativeMenuItem? _advanced;
        private NativeMenuItem? _closeTab;
        private NativeMenuItem? _zoom;

        internal NativeMenu Build()
        {
            var cluster = new NativeMenu();
            cluster.Add(Item(CommandId.NewClusterTab, () => Run(vm.AddNewTabCommand)));
            cluster.Add(Item(CommandId.ClusterSwitcher, () => Run(vm.OpenSwitcherCommand)));
            cluster.Add(Item(CommandId.OpenDemoCluster, () => Run(vm.OpenDemoClusterCommand)));
            cluster.Add(new NativeMenuItemSeparator());
            cluster.Add(Item(CommandId.OpenKubeconfigFile, () => Run(vm.OpenKubeconfigFileCommand)));
            cluster.Add(Item(CommandId.AddKubeconfigFolder, () => Run(vm.AddKubeconfigFolderCommand)));
            cluster.Add(Item(CommandId.RescanKubeconfig, () => Run(vm.ReloadContextsCommand)));
            cluster.Add(new NativeMenuItemSeparator());

            // Resolved through SelectedTab at click time rather than captured: the bar is
            // rebuilt only when the hotkey scheme changes, and the selected tab changes far
            // more often than that — a captured command would act on the wrong cluster.
            cluster.Add(Item(CommandId.OpenTerminal, () => Run(vm.SelectedTab?.Terminal.OpenInTerminalCommand)));
            cluster.Add(Item(CommandId.ReconnectCluster, () => Run(vm.SelectedTab?.ReconnectCommand)));
            cluster.Add(new NativeMenuItemSeparator());
            _closeTab = Item(CommandId.CloseClusterTab, () => Run(vm.CloseSelectedTabCommand));
            _closeTab.IsEnabled = vm.CloseSelectedTabCommand.CanExecute(null);
            cluster.Add(_closeTab);

            var edit = new NativeMenu();
            edit.Add(EditItem("Undo", EditCommand.Undo));
            edit.Add(EditItem("Redo", EditCommand.Redo));
            edit.Add(new NativeMenuItemSeparator());
            edit.Add(EditItem("Cut", EditCommand.Cut));
            edit.Add(EditItem("Copy", EditCommand.Copy));
            edit.Add(EditItem("Paste", EditCommand.Paste));
            edit.Add(EditItem("Select All", EditCommand.SelectAll));
            edit.Add(new NativeMenuItemSeparator());

            // Find is the list's search box, which is what ⌘F has always done in this app;
            // the gesture is the catalog's, so the menu cannot advertise a different chord.
            var find = new NativeMenuItem("Find…") { Gesture = MenuGesture(CommandId.FilterList) };
            find.Click += (_, _) => actions.FocusRowFilter();
            edit.Add(find);

            _sidebar = Item(CommandId.ToggleSidebar, () => vm.IsSidebarVisible = !vm.IsSidebarVisible);
            _sidebar.ToggleType = MenuItemToggleType.CheckBox;
            _sidebar.IsChecked = vm.IsSidebarVisible;

            _advanced = Item(CommandId.ToggleAdvancedView, () => vm.IsAdvancedView = !vm.IsAdvancedView);
            _advanced.ToggleType = MenuItemToggleType.CheckBox;
            _advanced.IsChecked = vm.IsAdvancedView;

            var view = new NativeMenu();
            view.Add(Item(CommandId.ShowApplications, () => Run(vm.ShowApplicationsCommand)));
            view.Add(Item(CommandId.ShowResources, () => Run(vm.ShowResourcesCommand)));
            view.Add(Item(CommandId.CommandPalette, actions.OpenPalette));
            view.Add(new NativeMenuItemSeparator());
            view.Add(_sidebar);
            view.Add(_advanced);
            view.Add(Item(CommandId.ToggleTheme, actions.ToggleTheme));

            // ⌘M is the platform's, not a catalog chord, and always ⌘: ⌃M under the Windows
            // scheme would be a key equivalent over the shell's carriage return.
            var minimize = new NativeMenuItem("Minimize") { Gesture = new KeyGesture(Key.M, KeyModifiers.Meta) };
            minimize.Click += (_, _) => actions.Minimize();
            _zoom = new NativeMenuItem("Zoom") { IsEnabled = actions.CanZoom() };
            _zoom.Click += (_, _) =>
            {
                if (actions.CanZoom())
                {
                    actions.Zoom();
                }
            };
            var front = new NativeMenuItem("Bring All to Front");
            front.Click += (_, _) => actions.BringAllToFront();

            var window = new NativeMenu();
            window.Add(minimize);
            window.Add(_zoom);
            window.Add(new NativeMenuItemSeparator());
            window.Add(front);

            var help = new NativeMenu();
            help.Add(Item(CommandId.ShortcutsWindow, () => Run(vm.ToggleShortcutsCommand)));

            // Re-read when a menu opens as well as on change, so a state nothing notified
            // about (a window that can no longer maximize) is never shown stale.
            cluster.NeedsUpdate += (_, _) => RefreshCloseTab();
            window.NeedsUpdate += (_, _) => _zoom.IsEnabled = actions.CanZoom();

            var bar = new NativeMenu();
            bar.Add(new NativeMenuItem("Cluster") { Menu = cluster });
            bar.Add(new NativeMenuItem("Edit") { Menu = edit });
            bar.Add(new NativeMenuItem("View") { Menu = view });
            bar.Add(new NativeMenuItem("Window") { Menu = window });
            bar.Add(new NativeMenuItem("Help") { Menu = help });
            return bar;
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
            else if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
            {
                RefreshCloseTab();
            }
        }

        private void RefreshCloseTab()
        {
            if (_closeTab is { } item)
            {
                item.IsEnabled = vm.CloseSelectedTabCommand.CanExecute(null);
            }
        }

        private NativeMenuItem EditItem(string header, EditCommand command)
        {
            var item = new NativeMenuItem(header) { Gesture = EditCommands.GestureFor(command) };
            item.Click += (_, _) => actions.Edit(command);
            return item;
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
            var item = new NativeMenuItem(CommandCatalog.TitleFor(id)) { Gesture = MenuGesture(id) };
            item.Click += (_, _) => run();
            return item;
        }
    }
}
