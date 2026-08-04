using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// Fallback width of one caption button, used only if the theme's own
    /// <c>CaptionButtonWidth</c> resource cannot be read. Avalonia exposes no
    /// measurement of the caption strip — <see cref="Window.WindowDecorationMargin"/>
    /// reports the title bar's height, not the buttons' width — so the reserve is
    /// derived from the same resource the buttons size themselves from.
    /// </summary>
    private const double FallbackCaptionButtonWidth = 45;

    /// <summary>Minimize, maximize/restore, close — the three the decorations template draws.</summary>
    private const int CaptionButtonCount = 3;

    /// <summary>macOS traffic lights, which sit top-<em>left</em>: 3 × 14 plus the standard insets.</summary>
    private const double MacTrafficLightsWidth = 78;

    /// <summary>The command bar's own left/right breathing room, kept when a caption reserve is added.</summary>
    private const double CommandBarInset = 12;

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

        ConfigureWindowChrome();

        KeyBindings.Add(new KeyBinding { Gesture = Hotkeys.CommandPalette, Command = new RelayOpenPaletteCommand(this) });
        PaletteShortcutLabel.Text = Hotkeys.Describe(Hotkeys.CommandPalette);

        KeyBindings.Add(new KeyBinding { Gesture = Hotkeys.ClusterSwitcher, Command = new RelayOpenSwitcherCommand(this) });
        // Says "click" explicitly: with both a hovered and a selected row visible,
        // whether the mouse needs one click or two is a real question, and a popup
        // that answers it costs one line.
        SwitcherHintLabel.Text =
            $"Click or Enter to open · ↑↓ navigate · Esc close · {Hotkeys.PrimaryLabel}+1…9 jump to tab";

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
            // The decorations don't exist yet in the constructor, so the reserve's
            // real value arrives with the first WindowDecorationMargin change. This
            // is the backstop for a platform that never raises one.
            ApplyCaptionReserve();
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

    /// <summary>
    /// Merges the command bar into the title bar, so the shell has one bar of chrome
    /// at the top instead of two. The system title bar carried a window title the
    /// command bar was printing again 32px lower, three buttons, and ~32px of height
    /// that the inspector dock — ~300px, holding logs — was paying for.
    /// <para>
    /// Windows and macOS only, deliberately. Both keep the caption buttons in a
    /// conventional corner we can leave empty, which is what every comparable app does
    /// (VS Code, Chrome, Explorer, Lens, Aptakube) — though only macOS still *draws*
    /// them itself; see below. Extending the client area on Linux hands us the whole
    /// frame instead, and client-side decorations that match GNOME look wrong on KDE
    /// and every tiling WM; we ship linux-x64/arm64, so that trade isn't worth ~32px.
    /// Linux keeps the system-decorated window and this method does nothing.
    /// </para>
    /// <para>
    /// <b>On Windows the caption buttons become ours.</b> Avalonia 12's Win32 backend
    /// answers an extended client area with <c>RequestedDrawnDecorations = TitleBar</c>
    /// and calls <c>DisableCloseButton</c> on the HWND, so the system's three buttons
    /// are switched off and the app is expected to supply them — the opposite of
    /// pre-12's <c>PreferSystemChrome</c>. They come from the
    /// <c>CommandBarWindowDecorations</c> theme in Theme.axaml, which exists so the
    /// stock Fluent one doesn't also paint a title bar panel and the window title over
    /// the command bar. macOS asks for no drawn decorations at all and keeps its own
    /// traffic lights.
    /// </para>
    /// <para>
    /// The gestures a title bar owes the user — drag, double-click to maximize, the
    /// right-click window menu, Win11 Snap Layouts — are not reimplemented here. They
    /// come from the <c>TitleBar</c> decoration role on the bar in XAML, which Win32
    /// answers as <c>HTCAPTION</c> (and from the buttons' own Minimize/Maximize/Close
    /// roles, which map to <c>HTMINBUTTON</c>/<c>HTMAXBUTTON</c>/<c>HTCLOSE</c> — that
    /// is what keeps Snap Layouts, which only appear over a real maximize button).
    /// Hand-rolling the drag from <c>BeginMoveDrag</c> would reproduce one of the four
    /// and quietly lose the rest.
    /// </para>
    /// </summary>
    private void ConfigureWindowChrome()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        ExtendClientAreaToDecorationsHint = true;

        // Caption region == the bar, so the buttons fill its height rather than a 30px
        // strip floating inside a 40px row (30 is the theme's DefaultTitleBarHeight).
        // Read from the bar itself so the two cannot drift apart.
        ExtendClientAreaTitleBarHeightHint = CommandBar.Height;

        // Windows only in practice: Avalonia's macOS backend reports it needs no drawn
        // decorations and AppKit keeps the traffic lights, while Win32 disables the
        // system buttons and asks the app for a title bar. Setting it unconditionally
        // is still right — the theme is simply never built where nothing asks for it.
        if (this.TryFindResource("CommandBarWindowDecorations", out var resource) && resource is ControlTheme decorations)
        {
            WindowDecorationsTheme = decorations;
        }

        ApplyCaptionReserve();
        ApplyOffScreenMargin();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == OffScreenMarginProperty)
            {
                ApplyOffScreenMargin();
            }
            else if (e.Property == WindowDecorationMarginProperty)
            {
                ApplyCaptionReserve();
            }
        };
    }

    /// <summary>
    /// Leaves the caption buttons their space, and takes it back the moment they are
    /// not there. Without the reserve the palette pill and the theme toggle sit under
    /// Close on Windows, and the cluster switcher sits under the traffic lights on
    /// macOS; without the taking-back, <b>full screen</b> keeps a dead 135px (or 78px)
    /// gap in a bar that no longer has any buttons in it — and on macOS the green
    /// traffic light is the ordinary way into full screen, so that is a state people
    /// reach, not a corner case.
    /// <para>
    /// <see cref="Window.WindowDecorationMargin"/> is the honest signal for "is there a
    /// caption strip over my bar right now", and it is honest on both platforms for
    /// different reasons: with drawn decorations (Windows) its top is the title bar
    /// height only while that part is enabled, and full screen disables every part;
    /// without them (macOS) it is the backend's own extended margin, which that backend
    /// zeroes in full screen. Zero either way, and zero on Linux, where we never
    /// extended in the first place.
    /// </para>
    /// </summary>
    private void ApplyCaptionReserve()
    {
        var hasCaption = WindowDecorationMargin.Top > 0;

        CommandBar.Padding = new Thickness(
            CommandBarInset + (hasCaption && OperatingSystem.IsMacOS() ? MacTrafficLightsWidth : 0),
            0,
            CommandBarInset + (hasCaption && OperatingSystem.IsWindows() ? CaptionButtonsWidth() : 0),
            0);
    }

    /// <summary>
    /// A maximized window with an extended client area is deliberately sized a few
    /// pixels larger than the work area on every edge (Windows does this so its own
    /// resize borders stay grabbable), and Avalonia reports how much in
    /// <see cref="Window.OffScreenMargin"/>. Not honoring it clips whatever is at the
    /// window's edge — which, now, is the title bar's own contents.
    /// </summary>
    private void ApplyOffScreenMargin() => RootLayout.Margin = OffScreenMargin;

    /// <summary>
    /// Width of the caption strip the decorations template draws over the right of the
    /// command bar, taken from the same <c>CaptionButtonWidth</c> resource the buttons
    /// size themselves from — so restyling the buttons moves the reserve with them
    /// instead of silently sliding the palette pill under Close.
    /// </summary>
    private double CaptionButtonsWidth()
    {
        var width = this.TryFindResource("CaptionButtonWidth", out var resource) && resource is double value
            ? value
            : FallbackCaptionButtonWidth;

        return width * CaptionButtonCount;
    }

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

/// <summary>Trivial ICommand so the F1 gesture can live in a KeyBinding without a ViewModel round-trip.</summary>
internal sealed class RelayToggleShortcutsCommand(MainWindow window) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => (window.DataContext as ViewModels.MainWindowViewModel)?.ToggleShortcutsCommand.Execute(null);
}
