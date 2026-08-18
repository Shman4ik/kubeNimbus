using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// The aggregated log pane's auto-scroll, identical in mechanism to
/// <see cref="PodDetailView"/>'s and for the same three reasons: the scroll has to be
/// <em>posted</em> (running inside the collection-changed notification reaches the
/// previous extent and leaves the pane a line behind forever), it only pins while the
/// view is already at the bottom (otherwise the next line yanks you back the moment you
/// scroll up to read something), and the subscription is bound to the visual tree rather
/// than to the DataContext (the view model outlives the view, so an unmatched subscribe
/// keeps this control alive after it leaves the tree).
/// </summary>
/// <remarks>
/// Follow-and-scroll is the specific half of multi-pod logs that the field has had
/// trouble with — Headlamp carries its own bug trail for exactly this — which is why it
/// reuses the single-pod pane's already-corrected behaviour rather than inventing a
/// second one.
/// </remarks>
public partial class WorkloadLogsView : UserControl
{
    private WorkloadLogsTabViewModel? _vm;

    public WorkloadLogsView()
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
        if (_vm is null && DataContext is WorkloadLogsTabViewModel vm)
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

    private const double ScrollLockSlack = 24;

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.IsFollowing != true || !IsScrolledToBottom())
        {
            return;
        }

        Dispatcher.UIThread.Post(LogScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private bool IsScrolledToBottom() =>
        LogScroll.Offset.Y >= LogScroll.Extent.Height - LogScroll.Viewport.Height - ScrollLockSlack;
}
