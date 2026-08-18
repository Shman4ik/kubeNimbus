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

    /// <summary>
    /// Pushes the document into the view model on every keystroke, deliberately: Apply
    /// and the decoded-values panel both read <c>YamlText</c>, and a debounce here would
    /// let Apply send a document one keystroke old. The expensive part — re-parsing the
    /// whole document and base64-decoding every key — is what the view model coalesces
    /// (see <c>RevealDebounceInterval</c>); this is a string copy.
    /// </summary>
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

        // Guarded: seeding the editor raises TextChanged, and letting that echo back into
        // the view model marks a freshly opened tab dirty (and, mid-replace, could push an
        // empty document at it).
        _syncing = true;
        Editor.Text = _vm.YamlText;
        _syncing = false;

        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyPreviewRowHeight();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(YamlEditorTabViewModel.HasPendingPreview))
        {
            ApplyPreviewRowHeight();
            return;
        }

        if (e.PropertyName != nameof(YamlEditorTabViewModel.YamlText) || _vm is null || _syncing || Editor.Text == _vm.YamlText)
        {
            return;
        }

        _syncing = true;
        Editor.Text = _vm.YamlText;
        _syncing = false;
    }

    /// <summary>
    /// The apply preview shares the pane with the editor instead of displacing it. Star
    /// while it is open (so the two split whatever height the dock has, each scrolling
    /// inside its own share) and Auto — i.e. nothing — while it is not.
    ///
    /// <para>
    /// In code-behind because the answer has to be a <see cref="RowDefinition"/> height:
    /// an Auto row big enough to read the diff in left a zero-height editor at the dock's
    /// default ~300px, and giving the editor a MinHeight instead pushed the whole grid
    /// past the dock and overlapped its own rows. Same mechanism, same reason, as
    /// <c>ClusterTabView.ApplyDockState</c>.
    /// </para>
    /// </summary>
    private void ApplyPreviewRowHeight()
    {
        // Star only when there is a list to scroll. A preview that reports no changes is
        // one sentence and two buttons, and a star row would give it a card of blank space.
        var hasRows = _vm?.PendingPreview is { HasRows: true };
        Rows.RowDefinitions[PreviewRowIndex].Height = hasRows ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    /// <summary>The grid row the apply preview occupies — row 0 is the header, row 1 the editor.</summary>
    private const int PreviewRowIndex = 2;
}
