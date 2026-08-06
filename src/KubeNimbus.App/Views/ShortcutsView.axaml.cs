using Avalonia.Controls;

namespace KubeNimbus.App.Views;

/// <summary>
/// The F1 cheat sheet's body, hosted in the shell's shortcuts <c>OverlayPanel</c>.
/// A view rather than markup inside <see cref="MainWindow"/> so the sheet can be
/// rendered on its own by the screenshot harness, and so MainWindow.axaml stays
/// about the shell.
/// </summary>
public partial class ShortcutsView : UserControl
{
    public ShortcutsView() => InitializeComponent();
}
