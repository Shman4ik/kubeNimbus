using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using Nimbus.Ui.Chrome;

namespace KubeNimbus.App.Views;

public partial class MainWindow : Window
{
    private ClusterTabViewModel? _draggingTab;
    private Point _dragStart;
    private bool _dragging;

    /// <summary>
    /// The tab header currently held down, so its "pressed" class can be cleared on
    /// release. Tracked rather than re-resolved from the release event because a drag
    /// can end over a different tab than it started on.
    /// </summary>
    private Border? _pressedTab;

    public MainWindow()
    {
        InitializeComponent();

        // One bar of chrome at the top instead of two: the command bar becomes the
        // title bar. Shared with pgNimbus — the platform rules this has to respect
        // are documented on NimbusWindowChrome itself, and three of the four fail
        // silently. 12 is this app's own horizontal breathing room in the bar.
        NimbusWindowChrome.Attach(this, CommandBar, RootLayout, inset: 12);

        KeyBindings.Add(new KeyBinding { Gesture = Hotkeys.CommandPalette, Command = new RelayOpenPaletteCommand(this) });
        PaletteShortcutLabel.Text = Hotkeys.Describe(Hotkeys.CommandPalette);

        KeyBindings.Add(new KeyBinding { Gesture = Hotkeys.ClusterSwitcher, Command = new RelayOpenSwitcherCommand(this) });
        // Says "click" explicitly: with both a hovered and a selected row visible,
        // whether the mouse needs one click or two is a real question, and a popup
        // that answers it costs one line.
        SwitcherHintLabel.Text =
            $"Click or Enter to open · ↑↓ navigate · Esc close · {Hotkeys.PrimaryLabel}+1…9 jump to tab";

        KeyBindings.Add(new KeyBinding { Gesture = Hotkeys.FilterList, Command = new RelayFocusRowFilterCommand(this) });

        KeyBindings.Add(new KeyBinding { Gesture = Hotkeys.ShortcutsHelp, Command = new RelayToggleShortcutsCommand(this) });

        // Ctrl/Cmd+1…9 jumps straight to a tab. Registered in a loop rather than
        // nine XAML KeyBindings so the modifier still comes from Hotkeys.Primary
        // (UI rule 4 — no hardcoded Ctrl gestures).
        for (var ordinal = 1; ordinal <= 9; ordinal++)
        {
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.D0 + ordinal, Hotkeys.Primary),
                Command = new RelaySelectTabCommand(this, ordinal),
            });
        }

        Opened += (_, _) =>
        {
            UpdateThemeIcon();
            ApplyBackdrop();
        };
        ActualThemeVariantChanged += (_, _) =>
        {
            UpdateThemeIcon();
            ApplyBackdrop();
        };
        Activated += (_, _) => ApplyBackdrop();
        Deactivated += (_, _) => ApplyBackdrop();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    // Windows 11 Mica backdrop: the shell base swaps between the theme-split
    // translucent ShellBackdropBrush (while the material actually renders —
    // active window, platform honors Mica/AcrylicBlur) and the opaque chrome
    // tone otherwise, matching pgNimbus's ApplyBackdrop.
    private void ApplyBackdrop()
    {
        var backdropActive = (ActualTransparencyLevel == WindowTransparencyLevel.Mica
            || ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur) && IsActive;

        var key = backdropActive ? "ShellBackdropBrush" : "SystemControlBackgroundChromeMediumLowBrush";
        if (this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush)
        {
            ShellBase.Background = brush;
        }
    }

    private void OnToggleThemeClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        Vm?.PersistTheme(app.RequestedThemeVariant == ThemeVariant.Dark ? "Dark" : "Light");
    }

    private void UpdateThemeIcon()
    {
        var key = ActualThemeVariant == ThemeVariant.Dark ? "WeatherSunnyIconGeometry" : "WeatherNightIconGeometry";
        if (this.TryFindResource(key, out var geometry) && geometry is Geometry data)
        {
            ThemeIcon.Data = data;
        }
    }

    private void OnPaletteButtonClick(object? sender, RoutedEventArgs e) => OpenPalette();

    internal void OpenPalette()
    {
        Vm?.Palette.Open();
        PaletteQueryBox.Focus();
    }

    /// <summary>
    /// Ctrl/Cmd+F. Registered on the window rather than on <see cref="ClusterTabView"/>
    /// because a KeyBinding only sees keys that already route to its control, and this
    /// one has to work with focus on the tab strip or nowhere at all. The visible
    /// cluster view is resolved from the tree: the tab content is a ContentControl
    /// bound to SelectedTab, so there is exactly one realized and visible at a time.
    /// </summary>
    internal void FocusRowFilter()
    {
        foreach (var view in this.GetVisualDescendants().OfType<ClusterTabView>())
        {
            if (view.IsEffectivelyVisible)
            {
                view.FocusRowFilter();
                return;
            }
        }
    }

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
        {
            Vm?.Palette.Close();
        }
    }

    private void OnShortcutsBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
        {
            Vm?.CloseShortcutsCommand.Execute(null);
        }
    }

    private void OnPaletteItemTapped(object? sender, TappedEventArgs e) => Vm?.Palette.ExecuteSelected();

    /// <summary>
    /// Cluster tab context menu → environment assignment. An empty Tag clears the
    /// override so the name guess applies again; anything else is a
    /// <see cref="KubeNimbus.Core.ClusterEnvironment"/> name.
    /// </summary>
    private void OnSetEnvironmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClusterTabViewModel tab, Tag: string tag } || Vm is not { } vm)
        {
            return;
        }

        vm.SetEnvironment(
            tab.Context,
            Enum.TryParse<KubeNimbus.Core.ClusterEnvironment>(tag, out var environment) ? environment : null);
    }

    private void OnSwitcherButtonClick(object? sender, RoutedEventArgs e) => OpenSwitcher();

    internal void OpenSwitcher()
    {
        // No HasContexts gate. It used to be one, and it was the second of two silent
        // ones (the button's own IsEnabled was the first): with no kubeconfig the
        // switcher refused to open, so an open demo cluster could not be reached from
        // the top bar or from Ctrl/Cmd+P on exactly the machine where it is the only
        // cluster there is. The switcher always has at least the demo row in it.
        if (Vm is not { } vm)
        {
            return;
        }

        vm.Switcher.Open();
        SwitcherQueryBox.Focus();
    }

    private void OnSwitcherBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
        {
            Vm?.Switcher.Close();
        }
    }

    /// <summary>
    /// Opens the tapped switcher row. Resolving the row from the event source rather
    /// than acting on <c>SelectedItem</c> alone matters twice: a tap on the ListBox's
    /// empty area below the last row must do nothing, and the row that opens must be
    /// the row under the cursor even if selection hasn't caught up.
    /// </summary>
    private void OnSwitcherListTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: ClusterSwitcherItemViewModel item })
        {
            return;
        }

        Vm?.Switcher.ActivateItem(item);
    }

    private void OnSwitcherPinClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ClusterSwitcherItemViewModel item } && Vm is { } vm)
        {
            vm.SetPinned(item.Context.Name, !item.IsPinned);
        }

        e.Handled = true;
    }

    private void OnSwitcherKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm?.Switcher is not { } switcher)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                switcher.Close();
                e.Handled = true;
                break;
            case Key.Enter:
                switcher.ActivateSelected();
                e.Handled = true;
                break;
            case Key.Down:
                switcher.MoveSelection(1);
                ScrollSwitcherSelectionIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                switcher.MoveSelection(-1);
                ScrollSwitcherSelectionIntoView();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Keyboard focus stays in the query box while arrows move the selection, so
    /// the ListBox never scrolls itself — it only does that for its own focused
    /// navigation. Without this, arrowing past the visible rows silently moves the
    /// selection off-screen.
    /// </summary>
    private void ScrollSwitcherSelectionIntoView()
    {
        if (Vm?.Switcher.SelectedItem is { } selected)
        {
            SwitcherList.ScrollIntoView(selected);
        }
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        var palette = Vm?.Palette;
        if (palette is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                palette.Close();
                e.Handled = true;
                break;
            case Key.Enter:
                palette.ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Down:
                palette.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                palette.MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void OnTabHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (_dragging)
        {
            return;
        }

        if (sender is Border { DataContext: ClusterTabViewModel tab } && Vm is { } vm)
        {
            vm.SelectedTab = tab;
        }
    }

    private void OnTabHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: ClusterTabViewModel tab } border && e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            _draggingTab = tab;
            _dragStart = e.GetPosition(TabStrip);
            _dragging = false;
            _pressedTab = border;
            border.Classes.Add("pressed");
            e.Pointer.Capture(border);
        }
    }

    private void OnTabHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingTab is null || Vm is not { } vm)
        {
            return;
        }

        var pos = e.GetPosition(TabStrip);
        if (!_dragging && Math.Abs(pos.X - _dragStart.X) < 6)
        {
            return;
        }

        _dragging = true;
        var currentIndex = vm.Tabs.IndexOf(_draggingTab);
        if (currentIndex < 0)
        {
            return;
        }

        for (var i = 0; i < vm.Tabs.Count; i++)
        {
            if (TabStrip.ContainerFromIndex(i) is not Control container || i == currentIndex)
            {
                continue;
            }

            var bounds = container.Bounds;
            if (pos.X >= bounds.X && pos.X <= bounds.X + bounds.Width)
            {
                vm.MoveTab(currentIndex, i);
                break;
            }
        }
    }

    private void OnTabHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        _pressedTab?.Classes.Remove("pressed");
        _pressedTab = null;
        _draggingTab = null;
        // Deferred so the Tapped gesture (which fires after PointerReleased) still sees the flag.
        Dispatcher.UIThread.Post(() => _dragging = false);
    }
}

/// <summary>Trivial ICommand so the palette gesture can live in a KeyBinding without a ViewModel round-trip.</summary>
internal sealed class RelayOpenPaletteCommand(MainWindow window) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => window.OpenPalette();
}

/// <summary>Trivial ICommand so the cluster-switcher gesture can live in a KeyBinding.</summary>
internal sealed class RelayOpenSwitcherCommand(MainWindow window) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => window.OpenSwitcher();
}

/// <summary>Trivial ICommand backing one Ctrl/Cmd+N tab jump.</summary>
internal sealed class RelaySelectTabCommand(MainWindow window, int ordinal) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) =>
        (window.DataContext as ViewModels.MainWindowViewModel)?.SelectTabByOrdinal(ordinal);
}

/// <summary>Trivial ICommand so the Ctrl/Cmd+F gesture can live in a KeyBinding.</summary>
internal sealed class RelayFocusRowFilterCommand(MainWindow window) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => window.FocusRowFilter();
}

/// <summary>Trivial ICommand so the F1 gesture can live in a KeyBinding without a ViewModel round-trip.</summary>
internal sealed class RelayToggleShortcutsCommand(MainWindow window) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => (window.DataContext as ViewModels.MainWindowViewModel)?.ToggleShortcutsCommand.Execute(null);
}
