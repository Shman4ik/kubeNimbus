using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// Esc goes back to the list — from anywhere on the page except a text box, whose Esc is
/// its own (the log filter clears itself). Same carve-out the inspector's Esc makes.
/// </summary>
public partial class ApplicationPageView : UserControl
{
    public ApplicationPageView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnPagePointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
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
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None
            || e.Source is TextBox or TextEditor || DataContext is not ApplicationPageViewModel vm)
        {
            return;
        }

        vm.BackCommand.Execute(null);
        e.Handled = true;
    }
}
