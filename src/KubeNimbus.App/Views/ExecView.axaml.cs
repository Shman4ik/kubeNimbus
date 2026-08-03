using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// Keyboard handling for the exec pane, plus the PTY geometry report.
/// </summary>
/// <remarks>
/// The control keys have to be intercepted here rather than bound as gestures: a
/// focused <c>TextBox</c> handles Ctrl+C as Copy and swallows Tab as focus
/// navigation, so both would reach the remote shell never. They are taken in the
/// Tunnel phase for exactly that reason.
/// </remarks>
public partial class ExecView : UserControl
{
    /// <summary>
    /// Nominal cell size for the monospace font at 12pt, used to convert the output
    /// box's pixel size into the columns/rows the remote PTY is told about. An
    /// approximation is fine and a measurement would be overkill: being within a
    /// column of the truth is the difference between `top` wrapping and not.
    /// </summary>
    private const double CellWidth = 7.2;
    private const double CellHeight = 16.0;

    public ExecView()
    {
        InitializeComponent();

        // Tunnel: TextBox's own handlers would otherwise consume Ctrl+C and Tab first.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        OutputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0;
            }
        };

        OutputBox.SizeChanged += (_, _) => ReportSize();
        DataContextChanged += (_, _) => ReportSize();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // A terminal you have to click before typing into is a terminal that looks
        // broken for the first second of every session.
        Dispatcher.UIThread.Post(() => InputBox.Focus(), DispatcherPriority.Input);
        ReportSize();
    }

    private void ReportSize()
    {
        if (DataContext is not ExecTabViewModel vm || OutputBox.Bounds.Width <= 0)
        {
            return;
        }

        var columns = (int)(OutputBox.Bounds.Width / CellWidth);
        var rows = (int)(OutputBox.Bounds.Height / CellHeight);
        _ = vm.ResizeAsync(columns, rows);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ExecTabViewModel vm)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Ctrl+C with a selection is Copy, which is what anyone would expect from a
        // text box; with nothing selected it is the interrupt, which is what anyone
        // would expect from a terminal. Both readings are right in their own context.
        if (ctrl && e.Key == Key.C && InputBox.SelectionStart == InputBox.SelectionEnd)
        {
            Send(vm, "C", e);
            return;
        }

        if (ctrl && e.Key is Key.D or Key.Z)
        {
            Send(vm, e.Key == Key.D ? "D" : "Z", e);
            return;
        }

        if (e.Key == Key.Tab && !ctrl)
        {
            Send(vm, "Tab", e);
            return;
        }

        if (e.Key == Key.Enter && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static void Send(ExecTabViewModel vm, string key, KeyEventArgs e)
    {
        if (vm.SendControlCommand.CanExecute(key))
        {
            vm.SendControlCommand.Execute(key);
        }

        e.Handled = true;
    }
}
