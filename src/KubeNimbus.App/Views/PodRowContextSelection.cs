using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace KubeNimbus.App.Views;

/// <summary>
/// Select the row under a right click before its DataGrid context menu reads SelectedItem.
/// Resolve from the event source so a click in cell padding works as well as one on text.
/// </summary>
internal static class PodRowContextSelection
{
    public static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed)
            return;

        for (var element = e.Source as Visual; element is not null; element = element.GetVisualParent())
        {
            if (element is DataGridRow row)
            {
                grid.SelectedItem = row.DataContext;
                return;
            }

            if (ReferenceEquals(element, grid)) return;
        }
    }
}
