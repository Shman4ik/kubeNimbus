using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// New YAML… (FEAT-12, the paste-or-type slice): the same editor, apply preview, conflict and
// validation panels as an object opened from the list, against a buffer that starts empty.
// What it is applied to is read from the text at the press (NewManifest.Read), resolved
// against this cluster's discovery, and the namespace defaults to the tab's current one.
public sealed partial class YamlEditorTabViewModel
{
    /// <summary>
    /// Resolves a group and kind to this cluster's descriptor, or null when discovery has no such
    /// kind. It takes <see cref="_closed"/>'s token, so closing the editor while the catalog is
    /// read stops the read (#342, hard rule 2).
    /// </summary>
    private readonly Func<string, string, CancellationToken, Task<ResourceDescriptor?>>? _resolveKind;

    /// <summary>Cancelled when the editor closes: what a press started reading is no longer wanted.</summary>
    private readonly CancellationTokenSource _closed = new();

    /// <summary>The namespace a namespaced object without one is applied to: the tab's, when it was opened.</summary>
    private readonly string _defaultNamespace = "";

    private bool _hasAppliedTarget;

    /// <summary>
    /// A new, empty document. Its key is unique per editor (<paramref name="number"/>), so
    /// opening another never reuses or replaces an editor already open — UI rule 5 — and
    /// nothing about it is known until its text is applied.
    /// </summary>
    public YamlEditorTabViewModel(
        ClusterClient? client, Func<string, string, CancellationToken, Task<ResourceDescriptor?>> resolveKind, string defaultNamespace, int number)
        : base(NewDocumentTitle(number), isDemo: client is null)
    {
        _client = client;
        _resolveKind = resolveKind;
        _defaultNamespace = defaultNamespace;
        _descriptor = NewDocumentPlaceholder;
        _namespace = null;
        _name = "";
        _yamlText = "";
        ClusterName = "";
        _key = $"yaml:new:{number}";
        IsNewDocument = true;

        _previewsApplies = App.LoadSettings().PreviewApplies;
        PreviewPreferenceChanged += OnPreviewPreferenceChanged;
    }

    /// <summary>"New YAML", then "New YAML 2", … — the dock tab's title until the first apply names the object.</summary>
    internal static string NewDocumentTitle(int number) => number <= 1 ? "New YAML" : $"New YAML {number}";

    /// <summary>
    /// Stands in for the descriptor until the text says what it is. No kind, so no kind-specific
    /// panel (a Secret's reveal, a ConfigMap's binaryData) can mistake an empty buffer for one.
    /// </summary>
    private static readonly ResourceDescriptor NewDocumentPlaceholder = new("", "v1", "", "", "", false, [], []);

    /// <summary>True for an editor opened by New YAML…, which applies whatever its text names.</summary>
    public bool IsNewDocument { get; }

    /// <summary>The namespace a namespaced manifest without one goes to.</summary>
    public string DefaultNamespace => _defaultNamespace;

    /// <summary>
    /// The InfoBar over a new document (UI rules 9 and 11): an empty editor otherwise looks
    /// like a blank rectangle, and the namespace default is a fact nobody could guess.
    /// Gone once the document has been applied — from then on it is that object's editor.
    /// </summary>
    public string? NewDocumentNotice => IsNewDocument && !_hasAppliedTarget
        ? OpenedFrom
          + $"{(_sourceFileName is null ? "Paste or type one manifest, then " : "")}{ApplyButtonLabel} applies it to {(ContextName.Length > 0 ? ContextName : "this cluster")}"
          + (_defaultNamespace.Length > 0 ? $". A namespaced object without metadata.namespace goes to {_defaultNamespace}." : ".")
        : null;

    /// <summary>The local file this document was read from (#301, FEAT-12), or null for one typed or pasted.</summary>
    private string? _sourceFileName;

    /// <summary>How many documents that file held; the editor has only the first.</summary>
    private int _sourceDocumentCount;

    /// <summary>
    /// The notice's first sentence for a document read from a file. A multi-document file is
    /// opened on its first document only, and the notice says so before anything is applied:
    /// the editor applies one object (rule 3 above), and the rest were neither opened nor sent.
    /// </summary>
    private string OpenedFrom => _sourceFileName switch
    {
        null => "",
        var file when _sourceDocumentCount > 1 =>
            $"{file} holds {_sourceDocumentCount} documents. Only the first is open here, because an editor applies one "
            + $"object; the other {_sourceDocumentCount - 1} were not opened and nothing was applied. ",
        var file => $"Opened from {file}. ",
    };

    /// <summary>
    /// Fills a new document with a local file's text — its first document when it holds
    /// several — and names the file as the tab's title until the first apply names the object.
    /// The file is read once, here; nothing about it is kept beyond this editor's text.
    /// </summary>
    internal void LoadFromFile(string fileName, string text)
    {
        var (first, count) = YamlJson.FirstDocument(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        _sourceFileName = fileName;
        _sourceDocumentCount = count;
        Title = fileName;
        YamlText = first;
        OnPropertyChanged(nameof(NewDocumentNotice));
    }

    /// <summary>
    /// Reads what a new document is and points the editor at it, or says why it cannot.
    /// True for an editor opened on an object, which already knows. Run at every press,
    /// because the text may name a different object than it did at the last one.
    /// </summary>
    private async Task<bool> ResolveNewDocumentTargetAsync()
    {
        if (!IsNewDocument)
        {
            return true;
        }

        if (NewManifest.Read(YamlText, out var problem) is not { } identity)
        {
            StatusMessage = problem;
            return false;
        }

        ResourceDescriptor? known;
        try
        {
            known = _resolveKind is null ? null : await _resolveKind(identity.Group, identity.Kind, _closed.Token);
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            // The editor was closed mid-read: nobody is left to tell, and nothing was sent.
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Nothing was applied: this cluster's kinds could not be read — {ex.Message}";
            return false;
        }

        if (known is null)
        {
            StatusMessage = $"Nothing was applied: this cluster serves no {identity.Kind} in "
                + (identity.Group.Length == 0 ? "the core API group" : $"API group {identity.Group}")
                + ". Check apiVersion: and kind:, or install the CRD that defines it.";
            return false;
        }

        // The manifest's own version, as kubectl sends it: discovery names the preferred one,
        // and a server serving several versions of a kind accepts any of them.
        _descriptor = known with { Version = identity.Version };
        _resolvedDescriptor = known;
        _name = identity.Name;
        _namespace = known.Namespaced
            ? identity.Namespace ?? (_defaultNamespace.Length > 0 ? _defaultNamespace : "default")
            : null;
        return true;
    }

    /// <summary>
    /// The descriptor discovery returned, at its preferred version: the one a list row and E
    /// build their key from, where <see cref="_descriptor"/> carries the manifest's own version.
    /// </summary>
    private ResourceDescriptor? _resolvedDescriptor;

    private void MarkNewDocumentApplied()
    {
        _hasAppliedTarget = true;

        // From here on this is that object's editor, so it answers to that object's key: E on
        // the row focuses it rather than opening a second editor beside it (#300, UI rule 5).
        // Re-keyed at every apply, because the text may since name a different object.
        _key = KeyFor(ClusterName, _resolvedDescriptor ?? _descriptor, _namespace, _name);
        Title = $"{_descriptor.Kind}/{_name}";
        OnPropertyChanged(nameof(NewDocumentNotice));
        ReloadCommand.NotifyCanExecuteChanged();
        RequestDeleteCommand.NotifyCanExecuteChanged();
        ConfirmDeleteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DeleteTargetDescription));
    }
}
