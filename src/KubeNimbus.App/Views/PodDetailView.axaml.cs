using System.Collections.Specialized;
using Avalonia.Controls;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// The "Following" toggle only controls whether the log stream keeps reading —
/// nothing previously scrolled the log ScrollViewer, so a followed stream would
/// silently scroll off past the visible area. This pins it to the newest line
/// as they arrive, same as `tail -f` / `kubectl logs -f` in a terminal.
/// </summary>
public partial class PodDetailView : UserControl
{
    private PodDetailTabViewModel? _vm;

    public PodDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
        Bind();
    }

    private void Bind()
    {
        if (_vm is not null)
        {
            _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
        }

        _vm = DataContext as PodDetailTabViewModel;
        if (_vm is not null)
        {
            _vm.LogLines.CollectionChanged += OnLogLinesChanged;
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
