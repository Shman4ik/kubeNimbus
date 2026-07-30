using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using KubeNimbus.App.Editing;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// Values/manifest/notes/history for one Helm release. Both editors are
/// read-only, but AvaloniaEdit's <c>Text</c> still isn't a well-behaved binding
/// target (same reason as <see cref="YamlEditorView"/>), so the view-model text
/// is pushed in from code-behind.
/// </summary>
public partial class HelmReleaseView : UserControl
{
    private HelmReleaseTabViewModel? _vm;

    public HelmReleaseView()
    {
        InitializeComponent();
        ValuesEditor.SyntaxHighlighting = YamlSyntaxHighlighting.Instance;
        ManifestEditor.SyntaxHighlighting = YamlSyntaxHighlighting.Instance;
        DataContextChanged += (_, _) => Bind();
        Bind();
    }

    private void Bind()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as HelmReleaseTabViewModel;
        if (_vm is null)
        {
            return;
        }

        PushText();
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HelmReleaseTabViewModel.ValuesYaml)
            or nameof(HelmReleaseTabViewModel.Manifest)
            or null)
        {
            PushText();
        }
    }

    private void PushText()
    {
        if (_vm is null)
        {
            return;
        }

        ValuesEditor.Text = _vm.ValuesYaml;
        ManifestEditor.Text = _vm.Manifest;
    }

    private void OnHistoryRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is { SelectedRevision: { } revision })
        {
            _vm.ShowRevisionCommand.Execute(revision);
        }
    }
}
