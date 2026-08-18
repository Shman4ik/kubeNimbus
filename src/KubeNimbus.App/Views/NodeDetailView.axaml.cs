using Avalonia.Controls;

namespace KubeNimbus.App.Views;

/// <summary>
/// Node detail pane — purely declarative, all state lives in
/// <see cref="ViewModels.NodeDetailTabViewModel"/>. The mutating actions
/// (cordon / uncordon / drain) are deliberately not here: they land on the cluster tab's
/// shared confirm strip, as every other mutating action does (UI rule 17).
/// </summary>
public partial class NodeDetailView : UserControl
{
    public NodeDetailView() => InitializeComponent();
}
