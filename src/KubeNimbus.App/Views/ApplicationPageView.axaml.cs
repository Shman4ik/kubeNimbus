using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
