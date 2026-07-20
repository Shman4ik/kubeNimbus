using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
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

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.IsFollowingLogs == true)
        {
            LogScroll.ScrollToEnd();
        }
    }
}
