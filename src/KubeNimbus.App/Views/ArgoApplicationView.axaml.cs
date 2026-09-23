using Avalonia.Controls;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// One Argo CD Application's detail pane: what Git says it should be, what Argo made of
/// that, the objects it manages, its conditions and its deployment history. Declarative
/// apart from the resource rows' logs icon, whose Shift+click needs the modifiers a
/// Button's Click does not carry (<see cref="RowLogsGesture"/>).
/// </summary>
public partial class ArgoApplicationView : UserControl
{
    private readonly RowLogsGesture _rowLogs;

    public ArgoApplicationView()
    {
        InitializeComponent();
        _rowLogs = RowLogsGesture.Track(this);
    }

    private void OnResourceLogsClick(object? sender, RoutedEventArgs e)
    {
        var shift = _rowLogs.TakeShift();
        e.Handled = true;
        if (sender is Button { DataContext: ArgoResourceRowViewModel row })
        {
            _ = row.OpenLogsAsync(shift);
        }
    }
}
