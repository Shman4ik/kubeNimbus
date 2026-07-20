using System.ComponentModel;
using Avalonia.Controls;
using KubeNimbus.App.Editing;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// AvaloniaEdit's TextEditor.Text isn't a well-behaved binding target (rebinding
/// on every keystroke resets the caret), so — same approach as pgNimbus's SQL
/// editor — this syncs manually in code-behind with a re-entrancy guard.
/// </summary>
public partial class YamlEditorView : UserControl
{
    private bool _syncing;
    private YamlEditorTabViewModel? _vm;

    public YamlEditorView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = YamlSyntaxHighlighting.Instance;
        Editor.TextChanged += OnEditorTextChanged;
        DataContextChanged += (_, _) => Bind();
        Bind();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing || _vm is null)
        {
            return;
        }

        _syncing = true;
        _vm.YamlText = Editor.Text ?? "";
        _syncing = false;
    }

    private void Bind()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as YamlEditorTabViewModel;
        if (_vm is null)
        {
            return;
        }

        Editor.Text = _vm.YamlText;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(YamlEditorTabViewModel.YamlText) || _vm is null || _syncing || Editor.Text == _vm.YamlText)
        {
            return;
        }

        _syncing = true;
        Editor.Text = _vm.YamlText;
        _syncing = false;
    }
}
