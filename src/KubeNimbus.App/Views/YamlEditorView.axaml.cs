using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KubeNimbus.Core.Commands;
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
        EditorDefaults.Apply(Editor);
        YamlSyntaxHighlighting.Attach(Editor);
        Editor.TextChanged += OnEditorTextChanged;
        DataContextChanged += (_, _) => Bind();
        Bind();

        // Ctrl/Cmd+S applies, as the cheat sheet says. Tunnel, because the editor is entitled to
        // the key first and would otherwise never pass it on. The shortcut sheet and the docs
        // page listed it for a long time with nothing bound to it (found by the 0.5.1 pass).
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // The view model can refuse the managed-fields chip (text that is not one YAML
        // document): it sets the flag straight back inside the binding's own write, and a
        // binding does not take a value back while it is writing one. A click happened to
        // come out right; a Toggle through UI Automation left the chip pressed over a refusal
        // (#236, QA check M-8). So once the write is over, the chip is put back to what the
        // view model kept. SetCurrentValue, so the two-way binding stays.
        ManagedFieldsChip.IsCheckedChanged += (_, _) => Dispatcher.UIThread.Post(SyncManagedFieldsChip);
    }

    private void SyncManagedFieldsChip()
    {
        if (_vm is { } vm && ManagedFieldsChip.IsChecked != vm.ShowManagedFields)
        {
            ManagedFieldsChip.SetCurrentValue(ToggleButton.IsCheckedProperty, vm.ShowManagedFields);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _vm is null || !CommandBindings.Matches(CommandId.ApplyYaml, e))
        {
            return;
        }

        if (_vm.ApplyCommand.CanExecute(null))
        {
            _vm.ApplyCommand.Execute(null);
        }

        e.Handled = true;
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
    /// while a diff is open (so the two split whatever height the dock has, each scrolling
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
        // Star only when there is a diff to scroll. A preview that reports no changes is
        // one sentence and two buttons, and a star row would give it a card of blank space.
        //
        // While a diff is open it takes the editor's whole row (#316). Three stars against
        // the editor's one was the previous answer, and the QA VM's run (Y-8) measured what
        // that left at the dock's default ~300px: about two lines of diff in a ~35px area,
        // with the footnote drawn over them. The editor is context here and cannot be
        // anything else, because typing in it discards the preview by design (apply-preview
        // rule 6), so the height belongs to the thing being read; Cancel gives the editor
        // back. A star rather than a pixel height, so maximizing the dock still scales it.
        var hasBody = _vm?.PendingPreview is { HasBody: true };
        EditorRow.IsVisible = !hasBody;
        Rows.RowDefinitions[EditorRowIndex].Height = hasBody ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Rows.RowDefinitions[PreviewRowIndex].Height = hasBody ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    /// <summary>The grid row the apply preview occupies — row 0 is the header, row 1 the editor.</summary>
    private const int PreviewRowIndex = 2;

    /// <summary>The editor's row, which the diff takes whole while one is open.</summary>
    private const int EditorRowIndex = 1;
}
