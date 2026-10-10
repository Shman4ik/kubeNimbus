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

        LogSearchGestures.Attach(
            LogSearchBox,
            next: () => _vm?.FindNextLogMatchCommand,
            previous: () => _vm?.FindPreviousLogMatchCommand,
            clear: () =>
            {
                if (_vm is not null)
                {
                    _vm.LogSearchText = "";
                }
            },
            toggleRegex: () =>
            {
                if (_vm is not null)
                {
                    _vm.IsLogRegex = !_vm.IsLogRegex;
                }
            },
            toggleMatchCase: () =>
            {
                if (_vm is not null)
                {
                    _vm.IsLogMatchCase = !_vm.IsLogMatchCase;
                }
            },
            pin: () => _vm?.PinSearchCommand);
        LogSearchGestures.AttachRuler(LogRuler, LogItems, LogScroll);
        LogSearchGestures.AttachProblemKeys(this, () => _vm?.Problems);
        LogSearchGestures.AttachReveal(LogItems, () => _vm?.IsLogFilterMode == true, line => _vm?.RevealLine(line));

        // ENG-51: at a narrow window Range, Levels, the context chip and then Copy move into the ⋯ menu.
        LogBar = new LogBarOverflow(LogBarRow, leading: null, LogSearchBox, LogBarTools,
            (RangeSlot, MenuRange), (LevelsSlot, MenuLevels), (ContextSlot, MenuContext), (CopySlot, MenuCopy));
    }

    /// <summary>The log bar's narrow-window behaviour; internal for the screenshot harness's check.</summary>
    internal LogBarOverflow LogBar { get; }

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
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Problems.PropertyChanged += OnProblemsChanged;
            LogSearchGestures.BringIntoView(LogItems, _vm.CurrentLogMatch);
        }
    }

    private void Unbind()
    {
        if (_vm is not null)
        {
            _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Problems.PropertyChanged -= OnProblemsChanged;
            _vm = null;
        }
    }

    /// <summary>The error/warning jump moved: bring its line into sight, as the search's does.</summary>
    private void OnProblemsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogProblems.Current))
        {
            LogSearchGestures.BringIntoView(LogItems, _vm?.Problems.Current);
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkloadLogsTabViewModel.CurrentLogMatch))
        {
            LogSearchGestures.BringIntoView(LogItems, _vm?.CurrentLogMatch);
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

    // Clear and Save in the log overflow menu run their Command; this closes the menu
    // behind them, which a Flyout does not do on its own for a Button inside it.
    private void OnLogMenuActionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        LogMenuButton.Flyout?.Hide();
}
