using Avalonia.Controls;
using Avalonia.Input;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// The user-preferences page. Opened from the command bar's cog, the app menu or the
/// command palette; every control applies its change immediately, so there is no
/// OK/Cancel and Esc simply closes it.
/// </summary>
public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
        Closed += (_, _) => (DataContext as PreferencesViewModel)?.Detach();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
