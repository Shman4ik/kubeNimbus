using Avalonia.Controls;
using Avalonia.Input;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

public partial class ExecView : UserControl
{
    public ExecView()
    {
        InitializeComponent();
        InputBox.KeyDown += OnInputKeyDown;
        OutputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0;
            }
        };
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ExecTabViewModel vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
