using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Views;

/// <summary>
/// Esc goes back to the list — from anywhere on the page except a text box, whose Esc is
/// its own (the log filter clears itself). Same carve-out the inspector's Esc makes. On the
/// Cmd scheme ⌘[ goes back too (<see cref="CommandId.BackToApplications"/>), from a text box
/// as well, because it is no text key there.
/// </summary>
public partial class ApplicationPageView : UserControl
{
    public ApplicationPageView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnPagePointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);

        PodList.AddHandler(GotFocusEvent, OnPodListGotFocus, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Tab into the pod list lands on the selected pod, both ways (#236). The list is one Tab
    /// stop and virtualizes, and the selected pod's row is often below the page's fold when the
    /// page opens, so it had no container to be the stop: Tab entered on the first row and
    /// Shift+Tab, after the walk had scrolled, on the selected one. A Tab that arrives on another
    /// row is moved to the selected one, brought into view. The list's own arrow keys are not
    /// Tab focus and are left alone.
    /// </summary>
    private void OnPodListGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod != NavigationMethod.Tab || PodList.SelectedItem is not { } selected
            || e.Source is not ListBoxItem { } arrived || ReferenceEquals(arrived.DataContext, selected))
        {
            return;
        }

        PodList.ScrollIntoView(selected);
        if (PodList.ContainerFromItem(selected) is { } container)
        {
            container.Focus(NavigationMethod.Tab);
        }
    }

    /// <summary>
    /// A click on the page's background (a finding, the timeline, a caption) moves nothing
    /// by itself — none of those is focusable — so focus could stay wherever it was, including
    /// on the list the page hides, and Esc would go there. A control on the page that takes
    /// focus on press (the pod list, the log filter) has done so before this bubbles here, and
    /// is left alone.
    /// </summary>
    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
            && (ReferenceEquals(focused, this) || this.IsVisualAncestorOf(focused)))
        {
            return;
        }

        Focus(NavigationMethod.Pointer);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Take focus so Esc works at once, without a click first.
        Focus();
    }

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not ApplicationPageViewModel vm
            || !CommandBindings.Matches(CommandId.BackToApplications, e)
            || (e.Key == Key.Escape && e.Source is TextBox or TextEditor))
        {
            return;
        }

        vm.BackCommand.Execute(null);
        e.Handled = true;
    }
}
