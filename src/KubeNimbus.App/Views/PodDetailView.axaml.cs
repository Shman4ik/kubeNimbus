using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// The "Following" toggle only controls whether the log stream keeps reading —
/// nothing previously scrolled the log ScrollViewer, so a followed stream would
/// silently scroll off past the visible area. This pins it to the newest line
/// as they arrive, same as `tail -f` / `kubectl logs -f` in a terminal.
///
/// Subscription is bound to the visual tree, not just DataContext: LogLines is
/// owned by the (potentially longer-lived) view model, so subscribing without
/// unsubscribing on detach would let the view model's collection keep this view
/// alive after it leaves the tree.
/// </summary>
public partial class PodDetailView : UserControl
{
    private PodDetailTabViewModel? _vm;

    public PodDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Unbind();
            Bind();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Bind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unbind();
        base.OnDetachedFromVisualTree(e);
    }

    private void Bind()
    {
        if (_vm is null && DataContext is PodDetailTabViewModel vm)
        {
            _vm = vm;
            _vm.LogLines.CollectionChanged += OnLogLinesChanged;
        }
    }

    private void Unbind()
    {
        if (_vm is not null)
        {
            _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
            _vm = null;
        }
    }

    /// <summary>
    /// How close to the bottom still counts as "at the bottom". A couple of lines of
    /// slack, so a stray wheel notch doesn't silently detach the follow.
    /// </summary>
    private const double ScrollLockSlack = 24;

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.IsFollowingLogs != true || !IsScrolledToBottom())
        {
            return;
        }

        // Posted rather than called inline: this runs *inside* the collection-changed
        // notification, before the new item has been measured, so scrolling here
        // reached the previous extent and left the pane one line behind forever.
        Dispatcher.UIThread.Post(LogScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    /// <summary>
    /// The scroll lock. Auto-scrolling unconditionally means the moment you scroll up
    /// to read something on a chatty pod, the next line yanks you back to the bottom —
    /// so following only pins the view while the view is already at the bottom, which
    /// is what every terminal pager and log viewer does.
    /// </summary>
    private bool IsScrolledToBottom() =>
        LogScroll.Offset.Y >= LogScroll.Extent.Height - LogScroll.Viewport.Height - ScrollLockSlack;
}
