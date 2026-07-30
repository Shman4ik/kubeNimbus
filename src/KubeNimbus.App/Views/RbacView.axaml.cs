using Avalonia.Controls;

namespace KubeNimbus.App.Views;

/// <summary>Access review pane — purely declarative, all state lives in <see cref="ViewModels.RbacTabViewModel"/>.</summary>
public partial class RbacView : UserControl
{
    public RbacView() => InitializeComponent();
}
