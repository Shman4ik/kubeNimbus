using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// The two log panes' search-box keys and their scroll-to-match, decided once so pod
/// detail's pane and the multi-pod pane cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// Enter is the next (later) match and Shift+Enter the previous one — the gesture every
/// browser's and editor's find bar uses. Esc empties a non-empty box and is handled, so
/// it does not also restore a maximized inspector: Esc in a text box belongs to the text
/// (<c>row-logs-and-maximized.md</c>, rule 9). An empty box leaves Esc unhandled.
/// </para>
/// <para>
/// Registered in the Tunnel phase for the reason the lists' key handlers are: a TextBox
/// is entitled to Enter, and a Bubble handler would only ever see what it chose to leave.
/// </para>
/// </remarks>
internal static class LogSearchGestures
{
    public static void Attach(
        TextBox box, Func<ICommand?> next, Func<ICommand?> previous, Action clear,
        Action? toggleRegex = null, Action? toggleMatchCase = null, Func<ICommand?>? pin = null) =>
        box.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => OnKeyDown(box, e, next, previous, clear, toggleRegex, toggleMatchCase, pin),
            RoutingStrategies.Tunnel);

    /// <summary>
    /// A click on the overview ruler brings the line it names into view, and the ruler's
    /// ticks are scaled to how much of the pane the lines fill.
    /// </summary>
    public static void AttachRuler(Controls.LogOverviewRuler ruler, ItemsControl items, ScrollViewer scroll)
    {
        ruler.LineRequested += (_, line) => BringIntoView(items, line);
        // A row's own top, over whichever is taller of the content and the pane: exact with
        // wrapping on, and a short log's ticks stay beside its lines at the top.
        ruler.PositionOf = index =>
        {
            var total = Math.Max(scroll.Extent.Height, scroll.Viewport.Height);
            return total > 0 && items.ContainerFromIndex(index) is { } row && row.Bounds.Height > 0
                ? (row.Bounds.Y + row.Bounds.Height / 2) / total
                : null;
        };

        // Rows only have their final positions after layout, so the ruler redraws when the
        // content's size does. ScrollChanged is not enough: a list shorter than its pane
        // never raises it.
        scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
            {
                ruler.InvalidateVisual();
            }
        };
    }

    /// <summary>
    /// Alt+↑ / Alt+↓ anywhere in a log pane: the error before / after the problem cursor
    /// (<see cref="LogProblems"/>). Tunnel, so the search box and the lines both pass it on;
    /// an Alt+arrow means nothing to either.
    /// </summary>
    public static void AttachProblemKeys(Control pane, Func<LogProblems?> problems) =>
        pane.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Alt || problems() is not { } p)
            {
                return;
            }

            var command = e.Key switch
            {
                Key.Up => p.PreviousErrorCommand,
                Key.Down => p.NextErrorCommand,
                _ => null,
            };
            if (command is not null)
            {
                if (command.CanExecute(null))
                {
                    command.Execute(null);
                }

                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

    /// <summary>
    /// A double-click on a line while the search filters shows that line in the full log
    /// (<c>RevealLine</c>). Resolved from the event source rather than handled on the row
    /// template (UI rule 8), and with handled events too, because the line's text block
    /// takes the double-click for its own word selection first.
    /// </summary>
    public static void AttachReveal(ItemsControl items, Func<bool> filtering, Action<LogLineViewModel> reveal) =>
        items.AddHandler(InputElement.DoubleTappedEvent, (_, e) =>
        {
            if (filtering() && (e.Source as Avalonia.StyledElement)?.DataContext is LogLineViewModel line)
            {
                reveal(line);
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

    private static void OnKeyDown(
        TextBox box, KeyEventArgs e, Func<ICommand?> next, Func<ICommand?> previous, Action clear,
        Action? toggleRegex, Action? toggleMatchCase, Func<ICommand?>? pin)
    {
        // VS Code's find-widget keys for its two toggles, and Alt+P to pin the search.
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.R or Key.C or Key.P)
        {
            if (e.Key == Key.P)
            {
                if (pin?.Invoke() is { } command && command.CanExecute(null))
                {
                    command.Execute(null);
                }
            }
            else
            {
                (e.Key == Key.R ? toggleRegex : toggleMatchCase)?.Invoke();
            }

            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter or Key.Return:
            {
                var command = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? previous() : next();
                if (command?.CanExecute(null) == true)
                {
                    command.Execute(null);
                }

                e.Handled = true;
                break;
            }

            case Key.Escape when !string.IsNullOrEmpty(box.Text):
                clear();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Brings the search's current match into sight. Posted at Background priority: the
    /// match can move in the same tick that lines were added, and a container that has not
    /// been measured yet reports the old extent — the same reason the follow's own
    /// scroll-to-end is posted.
    /// </summary>
    public static void BringIntoView(ItemsControl items, LogLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => items.ContainerFromItem(line)?.BringIntoView(),
            DispatcherPriority.Background);
    }
}
