using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Commands;
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

        // Not an Icon= attribute in the XAML: that path cannot resolve under NativeAOT
        // and killed the published binary on every RID (see WindowIcons).
        WindowIcons.Apply(this);

        // One bar of chrome at the top instead of two: the command bar becomes the
        // title bar. Shared with pgNimbus — the platform rules this has to respect
        // are documented on NimbusWindowChrome itself, and three of the four fail
        // silently. 12 is this app's own horizontal breathing room in the bar.
        NimbusWindowChrome.Attach(this, CommandBar, RootLayout, inset: 12);

        BuildKeyBindings();

        // A press on the Applications segment while it is already selected changes no
        // selection, so the ListBox raises nothing; this is what makes it go back to the
        // list from an application page, like the page's own "‹ Applications" link.
        // Tunnel, so the mode is read before the ListBox selects on the same press: from
        // Resources the press only switches mode and the open page is kept.
        ApplicationsModeItem.AddHandler(PointerPressedEvent, OnApplicationsModePressed, RoutingStrategies.Tunnel);

        // The cluster tabs are a ListBox (#236): the arrow keys, Home, End and Delete are
        // handled here for the dock strip's reason (a focus with a navigation method draws
        // the ring), and a plain vertical wheel scrolls the row sideways (#313).
        TabStrip.AddHandler(KeyDownEvent, OnClusterTabStripKeyDown, RoutingStrategies.Tunnel);
        HorizontalWheel.Install(TabScroller);

        // The Ctrl/Cmd scheme is a user preference now, so every gesture in the window
        // has to be rebuilt when it changes — a KeyGesture built once holds the
        // modifier it was created with, and the window would answer the other
        // platform's chords until restart.
        // Pinned by the screenshot harness's ux-hotkey-scheme check (VER-19): delete this
        // subscription and the new scheme's palette chord stops opening the palette.
        Hotkeys.Changed += BuildKeyBindings;

        // The window owns its view model's lifetime — App builds one for it and nothing
        // else holds it — so the shell's own Hotkeys.Changed handler goes with the window
        // rather than staying rooted by the static event (ENG-16).
        Unloaded += (_, _) =>
        {
            Hotkeys.Changed -= BuildKeyBindings;
            Vm?.Dispose();
        };

        Opened += (_, _) =>
        {
            UpdateThemeIcon();
            ApplyBackdrop();
        };

        // The macOS menu bar, built against the view model. Hooked on DataContextChanged
        // rather than in the constructor because App sets the DataContext in an object
        // initializer, i.e. after this runs. No-op on every other platform.
        DataContextChanged += (_, _) =>
        {
            if (Vm is { } vm)
            {
                MacMenu.Attach(this, vm);

                // Whatever opened the switcher, its search box takes the keyboard — the
                // switcher is driven from that box (arrows, Enter, Esc). Only Cmd/Ctrl+P and
                // the + button used to focus it; "New cluster tab" (⌘T, the ☰ menu, the empty
                // state's "Open a tab") opened it with focus left behind, so Esc did nothing
                // and typing went to the page under it.
                vm.Switcher.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(ClusterSwitcherViewModel.IsOpen))
                    {
                        return;
                    }

                    if (vm.Switcher.IsOpen)
                    {
                        _switcherReturnFocus = FocusOutside(SwitcherCard);
                        Dispatcher.UIThread.Post(() => SwitcherQueryBox.Focus(), DispatcherPriority.Input);
                    }
                    else
                    {
                        GiveFocusBack(SwitcherCard, ref _switcherReturnFocus);
                    }
                };

                // The palette gives focus back the same way when it closes (#236): it hid the
                // search box that held the focus, so Esc left the keyboard on nothing at all.
                vm.Palette.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(CommandPaletteViewModel.IsOpen))
                    {
                        return;
                    }

                    if (vm.Palette.IsOpen)
                    {
                        _paletteReturnFocus = FocusOutside(PaletteCard);
                    }
                    else
                    {
                        GiveFocusBack(PaletteCard, ref _paletteReturnFocus);
                    }
                };
            }
        };
        ActualThemeVariantChanged += (_, _) =>
        {
            UpdateThemeIcon();
            ApplyBackdrop();
        };
        Activated += (_, _) =>
        {
            ApplyBackdrop();

            // Coming back to the window is when a kubeconfig written elsewhere — by
            // `aws eks update-kubeconfig`, or into a picked folder — should show up. The
            // view model reads file metadata only, and reloads only if something changed.
            _ = Vm?.RescanIfChangedAsync();
        };
        Deactivated += (_, _) => ApplyBackdrop();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private WeakReference<IInputElement>? _paletteReturnFocus;
    private WeakReference<IInputElement>? _switcherReturnFocus;

    /// <summary>What had focus as an overlay opened, unless it was already inside the overlay.</summary>
    private WeakReference<IInputElement>? FocusOutside(Control card) =>
        FocusManager?.GetFocusedElement() is { } focused
        && !(focused is Visual visual && (ReferenceEquals(visual, card) || card.IsVisualAncestorOf(visual)))
            ? new WeakReference<IInputElement>(focused)
            : null;

    /// <summary>
    /// The palette's and the switcher's half of UI rule 16b's contract (they are not
    /// <c>OverlayPanel</c>s): closing gives focus back to what had it when the overlay opened,
    /// if focus is still in the overlay or nowhere, and that control is still on screen. A
    /// command that moved focus on purpose (the row filter, the column chooser) is left alone.
    /// Posted, so the card has been hidden and a command run from it has had its turn first.
    /// </summary>
    private void GiveFocusBack(Control card, ref WeakReference<IInputElement>? remembered)
    {
        var target = remembered is { } weak && weak.TryGetTarget(out var element) ? element : null;
        remembered = null;
        if (target is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var focused = FocusManager?.GetFocusedElement();
            var focusIsTheOverlays = focused is null
                                     || (focused is Visual visual && (ReferenceEquals(visual, card) || card.IsVisualAncestorOf(visual)));
            if (focusIsTheOverlays && target is Visual { IsEffectivelyVisible: true } && target.Focusable)
            {
                target.Focus(NavigationMethod.Unspecified);
            }
        }, DispatcherPriority.Input);
    }

    private void OnApplicationsModePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { IsApplicationsMode: true } vm && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            vm.ShowApplicationsCommand.Execute(null);
        }
    }

    /// <summary>
    /// Builds every window-level gesture from <see cref="Core.Commands.CommandCatalog"/>,
    /// replacing whatever was there. Called at construction and again whenever the
    /// Ctrl/Cmd scheme changes.
    ///
    /// <para>
    /// The bindings are cleared first rather than added to: rebuilding on a scheme
    /// change would otherwise leave the old modifier's gestures registered alongside
    /// the new ones, so Ctrl+K would keep working after someone chose Cmd — which reads
    /// as the preference having done nothing.
    /// </para>
    ///
    /// <para>
    /// A few commands are handled here rather than resolved from the view model,
    /// because they move focus into a control rather than change state — a thing an
    /// ICommand on a view model cannot express. Their gestures still come from the
    /// catalog, which is the part that has to be stated once.
    /// </para>
    /// </summary>
    private void BuildKeyBindings()
    {
        // The clearing rebuild itself lives in CommandBindings so it can be driven from
        // a view-model test (a MainWindow needs a running Application); what stays here
        // is the half that is genuinely the window's — the commands that act on it, and
        // the two labels that spell the modifier out.
        CommandBindings.RebuildWindowBindings(
            KeyBindings,
            WindowCommandFor,
            ordinal => new RelaySelectTabCommand(this, ordinal),
            () => ExecView.IsInTerminal(FocusManager?.GetFocusedElement()));

        PaletteShortcutLabel.Text = CommandBindings.ShortcutLabel(CommandId.CommandPalette);

        // Says "click" explicitly: with both a hovered and a selected row visible,
        // whether the mouse needs one click or two is a real question, and a popup that
        // answers it costs one line.
        SwitcherHintLabel.Text =
            $"Click or Enter to open · ↑↓ navigate · Esc close · {CommandDescriptor.SpellNote("{cmd}+1…9", Hotkeys.Scheme)} jump to tab";
    }

    /// <summary>
    /// The command behind one window-level gesture. Four of them act on the window
    /// itself — they move focus into a control or open a shell overlay, which is not
    /// something an <c>ICommand</c> on a view model can express — and everything else is
    /// resolved from the view model at execute time, not now: the DataContext is set
    /// after construction, and SelectedTab-backed commands change on every tab switch.
    /// </summary>
    private System.Windows.Input.ICommand WindowCommandFor(CommandId id) => id switch
    {
        CommandId.CommandPalette => new RelayOpenPaletteCommand(this),
        CommandId.LogsPalette => new RelayOpenPaletteCommand(this, CommandPaletteViewModel.LogsPrefix),
        CommandId.ClusterSwitcher => new RelayOpenSwitcherCommand(this),
        CommandId.FilterList => new RelayFocusRowFilterCommand(this),
        CommandId.ShortcutsWindow => new RelayToggleShortcutsCommand(this),
        _ => new RelayCatalogCommand(this, id),
    };

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

    /// <summary>
    /// The command bar's light/dark toggle. It flips the <em>actual</em> variant rather
    /// than the requested one, so the first click off "follow the system" goes to the
    /// opposite of what is on screen rather than to whatever the OS is set to.
    ///
    /// <para>
    /// The chosen variant is spelled by <see cref="App.ThemeToString"/> and not by a
    /// literal here. It used to write "Dark"/"Light" — the ThemeVariant names — which
    /// <c>AppSettings.Normalized()</c> rejects and <c>App.ThemeFromString</c> reads as
    /// <see cref="ThemeVariant.Default"/>: every click set the variant correctly and
    /// then, one line later, put the app straight back onto the OS's own theme. On a
    /// machine whose OS is dark that is a toggle which can enter dark and never leave.
    /// </para>
    /// </summary>
    private void OnToggleThemeClick(object? sender, RoutedEventArgs e) => ToggleTheme();

    /// <summary>Internal so the macOS View menu drives the same path as the bar's button.</summary>
    internal void ToggleTheme()
    {
        var chosen = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;

        // SetTheme applies the variant to the Application as well as persisting it, so
        // there is deliberately no second write to RequestedThemeVariant here: two
        // writers for one value is what let the bug above hide.
        Vm?.PersistTheme(App.ThemeToString(chosen));
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

    internal void OpenPalette() => OpenPalette("");

    /// <summary>
    /// Opens the palette with <paramref name="query"/> already typed — the logs gesture
    /// opens it on <see cref="CommandPaletteViewModel.LogsPrefix"/>. The caret goes to the
    /// end, so the next keystroke narrows the query rather than landing in front of it.
    /// </summary>
    internal void OpenPalette(string query)
    {
        Vm?.Palette.Open(query);
        PaletteQueryBox.Focus();
        MovePaletteCaretToEnd();
    }

    private void MovePaletteCaretToEnd()
    {
        var length = PaletteQueryBox.Text?.Length ?? 0;
        PaletteQueryBox.SelectionStart = length;
        PaletteQueryBox.SelectionEnd = length;
        PaletteQueryBox.CaretIndex = length;
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
        foreach (var applications in this.GetVisualDescendants().OfType<ApplicationsView>())
        {
            if (applications.IsEffectivelyVisible)
            {
                applications.FocusFilter();
                return;
            }
        }

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

    // The shortcuts, preferences and About panels have no handler here on purpose:
    // Nimbus.Ui's OverlayPanel owns its own backdrop, ✕ and Esc. The palette and the
    // switcher keep theirs because they are not that control — both put keyboard
    // focus in a search box and drive a selection from the arrow keys, which is a
    // different thing from a panel you read and dismiss.

    private void OnPaletteListTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: PaletteItem item }
            || Vm?.Palette is not { } palette)
        {
            return;
        }

        palette.Execute(item);
        AfterPaletteRun(palette);
    }

    /// <summary>
    /// A row that narrows the palette instead of leaving it (the "find logs…" entry) has
    /// just rewritten the query under the caret; put the caret back at the end and keep
    /// focus in the box, so the next keystroke is part of the search.
    /// </summary>
    private void AfterPaletteRun(CommandPaletteViewModel palette)
    {
        if (palette.IsOpen)
        {
            PaletteQueryBox.Focus();
            MovePaletteCaretToEnd();
        }
    }

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
                AfterPaletteRun(palette);
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

    /// <summary>
    /// Esc and Enter from anywhere in the palette's or the switcher's card, not only from its
    /// search box (#236). Tab reaches the list, and on a row there Esc did nothing, so a
    /// keyboard user who tabbed in could not close the overlay. The search box handles its own
    /// keys first and marks them handled, so these see only the keys it did not take.
    /// </summary>
    private void OnPaletteCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm?.Palette is not { } palette)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            palette.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
        {
            palette.ExecuteSelected();
            AfterPaletteRun(palette);
            e.Handled = true;
        }
    }

    private void OnSwitcherCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm?.Switcher is not { } switcher)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            switcher.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
        {
            switcher.ActivateSelected();
            e.Handled = true;
        }
    }

    /// <summary>
    /// A click or the keys chose a cluster tab. Read one way: a null selection (the selected
    /// tab was just closed, or a drag moved it) is not a choice, and the view model picks
    /// the neighbour itself.
    /// </summary>
    private void OnClusterTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_dragging || TabStrip.SelectedItem is not ClusterTabViewModel tab || Vm is not { } vm)
        {
            return;
        }

        if (!ReferenceEquals(vm.SelectedTab, tab))
        {
            vm.SelectedTab = tab;
        }
    }

    /// <summary>
    /// Left/Right, Home and End along the cluster tabs bring that tab to the front and focus
    /// it as keyboard focus, so it draws the focus ring; Delete closes the focused tab, whose
    /// close button is a pointer target only (#236, <see cref="CommandId.CloseFocusedTab"/>).
    /// </summary>
    private void OnClusterTabStripKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || Vm is not { } vm || vm.Tabs.Count == 0
            || e.Source is not Visual source
            || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: ClusterTabViewModel focused } from)
        {
            return;
        }

        var count = vm.Tabs.Count;
        var current = TabStrip.IndexFromContainer(from);
        if (current < 0)
        {
            return;
        }

        if (CommandBindings.Matches(CommandId.CloseFocusedTab, e))
        {
            e.Handled = true;
            _ = CloseFocusedClusterTabAsync(vm, focused);
            return;
        }

        int? target = e.Key switch
        {
            Key.Left or Key.Up => Math.Max(current - 1, 0),
            Key.Right or Key.Down => Math.Min(current + 1, count - 1),
            Key.Home => 0,
            Key.End => count - 1,
            _ => null,
        };
        if (target is not { } index)
        {
            return;
        }

        e.Handled = true;
        vm.SelectedTab = vm.Tabs[index];
        if (TabStrip.ContainerFromIndex(index) is { } container)
        {
            container.Focus(NavigationMethod.Directional);
            container.BringIntoView();
        }
    }

    private async Task CloseFocusedClusterTabAsync(MainWindowViewModel vm, ClusterTabViewModel closing)
    {
        await vm.CloseTabCommand.ExecuteAsync(closing);

        // After layout, so the container of the tab now in front exists.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (vm.SelectedTab is { } front && TabStrip.ContainerFromItem(front) is { } next)
            {
                next.Focus(NavigationMethod.Directional);
            }
        }, DispatcherPriority.Loaded);
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

        // A drag moves the tab in the collection, which the list can answer by dropping
        // its selection; the drawn state is the view model's, but UI Automation reads the
        // list's, so the one in front is pushed back without replacing the binding.
        if (Vm?.SelectedTab is { } front && !ReferenceEquals(TabStrip.SelectedItem, front))
        {
            TabStrip.SetCurrentValue(ListBox.SelectedItemProperty, front);
        }

        // Deferred so the Tapped gesture (which fires after PointerReleased) still sees the flag.
        Dispatcher.UIThread.Post(() => _dragging = false);
    }
}

/// <summary>Trivial ICommand so the palette gesture can live in a KeyBinding without a ViewModel round-trip.</summary>
internal sealed class RelayOpenPaletteCommand(MainWindow window, string query = "") : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => window.OpenPalette(query);
}

/// <summary>Trivial ICommand so the cluster-switcher gesture can live in a KeyBinding.</summary>
internal sealed class RelayOpenSwitcherCommand(MainWindow window) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => window.OpenSwitcher();
}

/// <summary>
/// Runs a catalog command through the view model, resolved at execute time. Late
/// resolution is the point: the DataContext is not set when the bindings are built,
/// and the commands that hang off <c>SelectedTab</c> change on every tab switch — a
/// command captured at build time would fire at whichever tab happened to be open when
/// the window was constructed.
/// </summary>
internal sealed class RelayCatalogCommand(MainWindow window, CommandId id) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (window.DataContext is not ViewModels.MainWindowViewModel vm)
        {
            return;
        }

        var command = CommandBindings.Resolve(id, vm);
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
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
