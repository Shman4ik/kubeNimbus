using Avalonia.Controls;

namespace KubeNimbus.App.Views;

/// <summary>
/// Ingress detail pane — fully declarative. Every action is a command on the row it was
/// pressed on (Open, Copy, the backend chevron) or on the selected row (the context menu).
/// </summary>
public partial class IngressDetailView : UserControl
{
    public IngressDetailView()
    {
        InitializeComponent();
    }
}
