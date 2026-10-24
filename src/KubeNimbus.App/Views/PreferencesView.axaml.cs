using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace KubeNimbus.App.Views;

/// <summary>
/// The user-preferences page, hosted in the shell's preferences <c>OverlayPanel</c>.
/// Opened from the command bar's cog, the app menu or the command palette; every
/// control applies its change immediately, so there is no OK/Cancel and Esc simply
/// dismisses it (the overlay owns that).
/// <para>
/// The page used to be a non-modal window, on the argument that you leave it open
/// while trying a setting against a live cluster. That is the one thing an overlay
/// cannot do, and it went because the family has one answer to "this needs its own
/// surface" now — the alternative was preferences looking like a different app than
/// the cheat sheet two menu items above it. Immediate-apply is what makes it
/// affordable: the change is already made when you dismiss.
/// </para>
/// </summary>
public partial class PreferencesView : UserControl
{
    /// <summary>
    /// The page's height whichever tab is showing. The overlay's card sizes to its
    /// content and is centred, so a page sized by its tab would grow and shrink on
    /// every switch and move the tab strip under the pointer. A window too short for
    /// it gets what there is, and each tab scrolls. Sized for the Appearance tab, the
    /// tallest; the overlay's own 640 cap, less its title row, is just above it.
    /// </summary>
    public const double PageHeight = 580;

    public PreferencesView()
    {
        InitializeComponent();
        Loaded += (_, _) => AutomationNames.NameCardControls(this);
        SettingsTabs.AddHandler(GotFocusEvent, (_, _) => PointStripAtSelectedTab(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Shift+Tab out of a tab's first control goes back to the tab strip (#236). The strip's
    /// panel is one Tab stop (<c>TabNavigation="Once"</c>), and a control on the tab's page,
    /// which is the tab item's logical content, became that stop's remembered element when it
    /// took focus: Shift+Tab from "Add file…" then went back to "Add file…" itself, a trap the
    /// overlays' keyboard walk found. The stop is put back on the selected tab after every
    /// focus change, once Avalonia has recorded its own.
    /// </summary>
    private void PointStripAtSelectedTab() => Dispatcher.UIThread.Post(() =>
    {
        if (SettingsTabs.ContainerFromIndex(SettingsTabs.SelectedIndex) is TabItem selected
            && selected.GetVisualParent() is InputElement strip
            && KeyboardNavigation.GetTabNavigation(strip) == KeyboardNavigationMode.Once
            && !ReferenceEquals(KeyboardNavigation.GetTabOnceActiveElement(strip), selected))
        {
            KeyboardNavigation.SetTabOnceActiveElement(strip, selected);
        }
    }, DispatcherPriority.Background);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = Math.Min(PageHeight, availableSize.Height);
        var desired = base.MeasureOverride(availableSize.WithHeight(height));
        return desired.WithHeight(height);
    }
}
