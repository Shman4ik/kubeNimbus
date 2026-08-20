using Avalonia.Controls;

namespace KubeNimbus.App.Views;

/// <summary>
/// One Argo CD Application's detail pane: what Git says it should be, what Argo made of
/// that, the objects it manages, its conditions and its deployment history. Pure XAML —
/// unlike the Helm pane there is no AvaloniaEdit here to push text into, so there is nothing
/// for code-behind to do beyond loading the markup.
/// </summary>
public partial class ArgoApplicationView : UserControl
{
    public ArgoApplicationView() => InitializeComponent();
}
