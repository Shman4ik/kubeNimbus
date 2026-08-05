using Avalonia.Controls;

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
    public PreferencesView() => InitializeComponent();
}
