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
    public static void Attach(TextBox box, Func<ICommand?> next, Func<ICommand?> previous, Action clear) =>
        box.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(box, e, next, previous, clear), RoutingStrategies.Tunnel);

    private static void OnKeyDown(TextBox box, KeyEventArgs e, Func<ICommand?> next, Func<ICommand?> previous, Action clear)
    {
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
