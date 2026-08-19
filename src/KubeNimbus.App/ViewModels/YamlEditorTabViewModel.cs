using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// View/edit tab for any resource kind → server-side apply, with delete
/// (two-step confirm, no modal dialog needed) and conflict surfacing. Content
/// is a snapshot taken at open time — live watch updates never overwrite text
/// the user might be mid-edit on; <see cref="ReloadCommand"/> re-fetches explicitly.
/// For a Secret, <c>data</c> stays base64 in the editable text (matching
/// kubectl) — <see cref="IsSecretValuesRevealed"/> only shows a separate,
/// read-only decoded preview panel, masked by default, computed from whatever
/// the editor currently holds (so it reflects in-progress edits too). The same
/// panel covers a ConfigMap's <c>binaryData</c>, which is the only base64 a
/// ConfigMap carries — its <c>data</c> is already plaintext in the editor.
/// </summary>
public sealed partial class YamlEditorTabViewModel : InspectorTabViewModelBase
{
    private const string FieldManager = "kubenimbus";

    /// <summary>
    /// How long to wait after the last keystroke before recomputing the decoded
    /// preview. Every recompute re-parses the whole document through YamlDotNet and
    /// base64-decodes every key, on the UI thread; doing that per keystroke made
    /// typing in a large Secret unusable, and nobody can read a value that is being
    /// edited anyway.
    /// </summary>
    private static readonly TimeSpan RevealDebounceInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>Null on the demo cluster — see <see cref="InspectorTabViewModelBase.IsDemo"/>.</summary>
    private readonly ClusterClient? _client;
    private readonly ResourceDescriptor _descriptor;
    private readonly string? _namespace;
    private readonly string _name;

    private DispatcherTimer? _revealTimer;

    /// <summary>
    /// Cheap "did the encoded block actually change?" fingerprint, so that typing in
    /// <c>spec:</c>/<c>metadata:</c> doesn't rebuild and re-decode the whole panel.
    /// Hash plus total length: the cost of a collision is a panel that is one edit
    /// stale, which is worth the parse it saves on every other edit.
    /// </summary>
    private (int Hash, int Length)? _revealSignature;

    public override string Key { get; }

    [ObservableProperty]
    private string _yamlText;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True while the open-time authoritative read is in flight — see the constructor.</summary>
    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Set when this tab is showing the list's snapshot because the server read failed.
    /// A silently stale editor is how you apply yesterday's object over today's, so the
    /// state gets its own visible line rather than being swallowed (CLAUDE.md UI rule 9).
    /// </summary>
    [ObservableProperty]
    private string? _staleNotice;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private string? _conflictDetails;

    /// <summary>
    /// The server's answer to "what would this apply do?", or null when nothing is
    /// armed. Set by <see cref="ApplyCommand"/> when the preference is on; the panel it
    /// drives carries the confirm, so Apply never mutates on the click that started it —
    /// the same shape as the resource list's row-action strip (CLAUDE.md UI rule 17).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingPreview))]
    private ApplyPreviewViewModel? _pendingPreview;

    public bool HasPendingPreview => PendingPreview is not null;

    /// <summary>The unified line diff — the default, and the way a manifest change is read.</summary>
    public const int PreviewViewModeInline = 0;

    /// <summary>The same diff as two aligned columns.</summary>
    public const int PreviewViewModeSplit = 1;

    /// <summary>The field-path list, which is the semantic summary of the same pair of objects.</summary>
    public const int PreviewViewModeFields = 2;

    /// <summary>
    /// Which view of the preview is on screen. It lives on the tab rather than on the
    /// preview so that choosing side-by-side once survives the next apply, and it is
    /// deliberately not a preference: it is a view toggle inside a pane, the same kind of
    /// thing as the log pane's timestamps and wrap toggles.
    /// </summary>
    [ObservableProperty]
    private int _previewViewMode = PreviewViewModeInline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorReadOnly))]
    private bool _isDeleted;

    /// <summary>
    /// The editor is read-only for a deleted object and for the demo cluster. Locking
    /// the text rather than only disabling Apply matters: an editable buffer that can
    /// never be applied invites typing into a void.
    /// </summary>
    public bool IsEditorReadOnly => IsDeleted || IsDemo;

    /// <summary>Core/v1 Secret — the kind whose <c>data</c> is base64 in the editor.</summary>
    public bool IsSecret => _descriptor is { Kind: "Secret", Group: "" };

    /// <summary>Core/v1 ConfigMap — plaintext <c>data</c>, but optionally base64 <c>binaryData</c>.</summary>
    public bool IsConfigMap => _descriptor is { Kind: "ConfigMap", Group: "" };

    /// <summary>
    /// Whether this kind has an encoded block worth decoding. ConfigMaps are included
    /// deliberately: <c>binaryData</c> is base64 exactly like a Secret's <c>data</c>,
    /// and gating the toggle on Secrets alone left it with no decode surface at all.
    /// </summary>
    public bool CanRevealValues => IsSecret || IsConfigMap;

    /// <summary>The block the reveal panel decodes: <c>data</c> on a Secret, <c>binaryData</c> on a ConfigMap.</summary>
    private string EncodedBlockName => IsConfigMap ? "binaryData" : "data";

    /// <summary>Caption above the decoded rows — says which block this is and that nothing leaves the machine.</summary>
    public string RevealCaption => IsConfigMap
        ? "binaryData: decoded locally from the editor's current text — data: values are already plaintext above."
        : "data: decoded locally from the editor's current text — nothing is sent anywhere.";

    [ObservableProperty]
    private bool _isSecretValuesRevealed;

    /// <summary>
    /// Why the panel has no rows (unparsable document, no encoded block, …). Every
    /// one of those states used to render as an empty card, which reads as a bug.
    /// </summary>
    [ObservableProperty]
    private string? _revealNotice;

    [ObservableProperty]
    private bool _isRevealNoticeProblem;

    public ObservableCollection<RevealedValueViewModel> RevealedValues { get; } = [];

    /// <summary>Cluster this object came from in an aggregated fleet list; empty otherwise.</summary>
    public string ClusterName { get; }

    /// <summary>
    /// Tab identity, qualified by cluster when the object came from an aggregated fleet
    /// list — the same namespace/name exists on every cluster in a fleet, and this key
    /// is what decides whether an open tab gets reused.
    /// </summary>
    public static string KeyFor(string clusterName, ResourceDescriptor descriptor, string? @namespace, string name) =>
        clusterName.Length == 0
            ? $"yaml:{descriptor.ApiVersion}/{descriptor.Kind}:{@namespace}/{name}"
            : $"yaml@{clusterName}:{descriptor.ApiVersion}/{descriptor.Kind}:{@namespace}/{name}";

    /// <summary>
    /// Why the demo pane's write half is inert. Viewing is the genuinely useful part
    /// and stays — the object renders with full syntax highlighting — but there is no
    /// API server to apply to or delete from, and a button that appears to change a
    /// cluster and doesn't is worse than no button.
    /// </summary>
    public const string DemoNotice =
        "This object is sample data, so it can be read but not changed — there is no API server to apply to or delete from. "
        + "Open a kubeconfig file to edit objects on one of your own clusters.";

    /// <summary>
    /// False on the demo cluster. Gates every command that talks to the API server,
    /// so the toolbar disables rather than silently no-ops (UI rule 9's last clause).
    /// </summary>
    private bool IsLive => _client is not null;

    public YamlEditorTabViewModel(
        ClusterClient? client, ResourceDescriptor descriptor, string? @namespace, string name, string initialYaml,
        string clusterName = "")
        : base(
            clusterName.Length == 0 ? $"{descriptor.Kind}/{name}" : $"{descriptor.Kind}/{name} · {clusterName}",
            isDemo: client is null)
    {
        _client = client;
        _descriptor = descriptor;
        _namespace = @namespace;
        _name = name;
        _yamlText = initialYaml;
        ClusterName = clusterName;
        Key = KeyFor(clusterName, descriptor, @namespace, name);

        // Why this is conditional rather than an unconditional read-on-open: the row's
        // object comes from a live watch, so it is as current as a GET would be and a
        // second fetch costs a round trip per opened tab (and can even lose a race with
        // the watch). What the snapshot is *not* guaranteed to be is self-describing —
        // list responses omit apiVersion/kind on their items, and an apply body without
        // them is rejected by the API server with a message that reads like a server
        // fault. So the read fires exactly when the snapshot can't identify itself,
        // which self-heals that class of bug wherever it comes from.
        // Not on the demo cluster: there is nothing to read from, and the dataset's
        // objects carry apiVersion/kind anyway.
        if (client is not null && !IsSelfDescribing(initialYaml))
        {
            _ = RefreshFromServerAsync();
        }
    }

    partial void OnYamlTextChanged(string value)
    {
        IsDirty = true;
        // The preview answers a question about the exact text that produced it. Editing
        // makes it a description of something that is no longer on screen, and a stale
        // diff above a live editor is worse than no diff at all.
        PendingPreview = null;
        if (!IsSecretValuesRevealed)
        {
            return;
        }

        _revealTimer ??= CreateRevealTimer();
        _revealTimer.Stop();
        _revealTimer.Start();
    }

    private DispatcherTimer CreateRevealTimer()
    {
        var timer = new DispatcherTimer { Interval = RevealDebounceInterval };
        timer.Tick += (_, _) => RefreshRevealedValues();
        return timer;
    }

    partial void OnIsSecretValuesRevealedChanged(bool value)
    {
        _revealTimer?.Stop();
        _revealSignature = null;
        if (value)
        {
            // Toggling on recomputes immediately — a debounce here would show an empty
            // panel for a third of a second, which is the state that means "nothing to show".
            RefreshRevealedValues();
        }
        else
        {
            RevealedValues.Clear();
            RevealNotice = null;
        }
    }

    /// <summary>
    /// Programmatic toggle (the screenshot harness, and anything that later wires this
    /// to the command palette). The view does NOT invoke this: <c>ToggleButton.IsChecked</c>
    /// binds two-way and <c>ToggleButton.OnClick</c> flips it *before* the command runs, so
    /// a button with both a two-way binding and a toggle command toggles twice per click
    /// and never appears to do anything. A ToggleButton gets one or the other, never both.
    /// </summary>
    [RelayCommand]
    private void ToggleSecretValuesRevealed() => IsSecretValuesRevealed = !IsSecretValuesRevealed;

    /// <summary>
    /// Recomputes the decoded preview from the editor's current text. Never throws and
    /// never abandons the loop: one key that can't be read must not take the rest of the
    /// panel with it (which is exactly what a document-wide try/catch did before —
    /// every key after the first bad one silently vanished).
    /// </summary>
    private void RefreshRevealedValues()
    {
        _revealTimer?.Stop();
        if (!IsSecretValuesRevealed)
        {
            return;
        }

        if (!CanRevealValues)
        {
            SetRevealNotice($"A {_descriptor.Kind} has no base64-encoded block to decode.", problem: false);
            return;
        }

        JsonNode? root;
        try
        {
            root = YamlJson.ParseYamlToJson(YamlText);
        }
        catch (Exception ex)
        {
            // Mid-edit text is routinely not valid YAML. That is a state, not a failure.
            SetRevealNotice($"This document isn't valid YAML right now, so there is nothing to decode — {FirstLine(ex.Message)}", problem: false);
            return;
        }

        if (root is not JsonObject obj)
        {
            SetRevealNotice("This document isn't a single Kubernetes object.", problem: true);
            return;
        }

        if (obj[EncodedBlockName] is not JsonObject block || block.Count == 0)
        {
            SetRevealNotice(EmptyBlockExplanation(obj), problem: false);
            return;
        }

        var signature = SignatureOf(block);
        if (_revealSignature == signature)
        {
            return;
        }

        _revealSignature = signature;
        RevealNotice = null;
        IsRevealNoticeProblem = false;
        RevealedValues.Clear();
        foreach (var (key, node) in block)
        {
            RevealedValues.Add(Decode(key, node));
        }
    }

    private void SetRevealNotice(string notice, bool problem)
    {
        RevealedValues.Clear();
        _revealSignature = null;
        RevealNotice = notice;
        IsRevealNoticeProblem = problem;
    }

    private string EmptyBlockExplanation(JsonObject obj) => IsConfigMap
        ? "This ConfigMap has no binaryData: entries — its data: values are already plaintext in the editor above."
        : obj["stringData"] is JsonObject { Count: > 0 }
            ? "This Secret only carries stringData:, which is plaintext already — nothing here is base64-encoded."
            : "This Secret has no data: entries.";

    /// <summary>
    /// Decodes one entry. Every failure mode becomes a row that says what happened,
    /// because a key that quietly disappears from this panel reads as "this Secret
    /// doesn't have that key".
    /// </summary>
    private static RevealedValueViewModel Decode(string key, JsonNode? node)
    {
        // Read the node defensively instead of GetValue<string>(): a base64 value that
        // looks like a number ("1E50" is valid base64, so is "8080") can come back as a
        // JSON number from a YAML round-trip, and GetValue<string> throws on those.
        var encoded = Stringify(node);
        if (encoded is null)
        {
            return RevealedValueViewModel.Problem(key, "not a scalar value — nothing to decode");
        }

        if (encoded.Length == 0)
        {
            return RevealedValueViewModel.Problem(key, "empty value");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return RevealedValueViewModel.Problem(key, "not valid base64", clipboardText: encoded);
        }

        // Convert.FromBase64String is happy with any bytes, so the FormatException guard
        // above never fires for binary payloads — and they are routine here: a
        // helm.sh/release.v1 Secret's `release` key is base64(gzip(json)) (see
        // ClusterClient.Helm.cs), as are keystores, .p12 bundles and DER keys. Rendering
        // those as UTF-8 produces a card full of U+FFFD.
        return TryDecodeUtf8(bytes, out var text)
            ? RevealedValueViewModel.Text(key, text)
            : RevealedValueViewModel.Binary(key, bytes, encoded);
    }

    private static string? Stringify(JsonNode? node) => node switch
    {
        null => null,
        JsonObject or JsonArray => null,
        // JsonNode.ToString() yields the unquoted scalar for strings and the literal
        // text for numbers/bools, so a re-inferred value still renders as itself.
        _ => node.ToString(),
    };

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }

        // Well-formed UTF-8 still isn't necessarily text — a NUL or a stray control byte
        // means a blob, and it should read as one rather than as mojibake.
        foreach (var c in text)
        {
            if (c == '\0' || (char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            {
                text = "";
                return false;
            }
        }

        return true;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static (int Hash, int Length) SignatureOf(JsonObject block)
    {
        var hash = new HashCode();
        var length = 0;
        foreach (var (key, node) in block)
        {
            var value = Stringify(node);
            hash.Add(key);
            hash.Add(value);
            length += key.Length + (value?.Length ?? 0);
        }

        return (hash.ToHashCode(), length);
    }

    [RelayCommand]
    private async Task CopyRevealedValueAsync(RevealedValueViewModel? row)
    {
        if (row?.ClipboardText is not { } text
            || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
        StatusMessage = row.CopiesBase64
            ? $"Copied {row.Key} (base64) to the clipboard."
            : $"Copied {row.Key} to the clipboard.";
    }

    /// <summary>
    /// Apply. With "Preview before applying" on — the default — this asks the server what
    /// the apply would do and shows it; the panel's own button is what changes anything.
    ///
    /// <para>
    /// The setting is read here, at the moment the button is pressed, rather than cached
    /// on the tab: someone who turns the preview back on after an apply surprised them
    /// expects the very next apply to show one, not the next tab they open. Same rule,
    /// same reason, as the delete confirm.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsLive))]
    private async Task ApplyAsync()
    {
        if (App.LoadSettings().PreviewApplies)
        {
            await PreviewCoreAsync(force: false);
            return;
        }

        await ApplyCoreAsync(force: false);
    }

    /// <summary>
    /// Take the conflicted fields from their current owner. Previewed like any other
    /// apply when the preference is on, and that is the case where a preview earns the
    /// most: what force-apply changes is precisely the fields somebody else is managing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsLive))]
    private async Task ForceApplyAsync()
    {
        if (App.LoadSettings().PreviewApplies)
        {
            await PreviewCoreAsync(force: true);
            return;
        }

        await ApplyCoreAsync(force: true);
    }

    /// <summary>Applies what the open preview was computed from.</summary>
    [RelayCommand(CanExecute = nameof(IsLive))]
    private async Task ConfirmPreviewAsync()
    {
        if (PendingPreview is not { } preview)
        {
            return;
        }

        await ApplyCoreAsync(preview.IsForce);
    }

    [RelayCommand]
    private void CancelPreview()
    {
        PendingPreview = null;
        StatusMessage = "Nothing was applied.";
    }

    private async Task PreviewCoreAsync(bool force)
    {
        if (_client is null)
        {
            return;
        }

        if (!IsSelfDescribing(YamlText))
        {
            StatusMessage = MissingTypeMessage;
            return;
        }

        IsBusy = true;
        PendingPreview = null;
        ConflictDetails = null;
        StatusMessage = null;
        try
        {
            var preview = await _client.PreviewApplyAsync(_descriptor, _namespace, _name, YamlText, FieldManager, force);
            PendingPreview = new ApplyPreviewViewModel(preview, force);
        }
        catch (ServerSideApplyConflictException ex)
        {
            // A conflict is an answer, not a failure of the preview — and it is the one
            // this feature most wants to deliver before the object moves rather than after.
            ConflictDetails = ex.Message;
        }
        catch (Exception ex)
        {
            // Everything the server refuses — a validating webhook, a schema violation, an
            // RBAC 403 — lands here having changed nothing, which is the point.
            StatusMessage = $"Nothing was applied. The server refused the dry run: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyCoreAsync(bool force)
    {
        if (_client is null)
        {
            return;
        }

        if (!IsSelfDescribing(YamlText))
        {
            StatusMessage = MissingTypeMessage;
            return;
        }

        IsBusy = true;
        PendingPreview = null;
        ConflictDetails = null;
        StatusMessage = null;
        try
        {
            var applied = await _client.ApplyYamlAsync(_descriptor, _namespace, _name, YamlText, FieldManager, force);
            YamlText = applied.ToYaml();
            IsDirty = false;
            StaleNotice = null;
            StatusMessage = $"Applied at {DateTimeOffset.Now:T}.";
        }
        catch (ServerSideApplyConflictException ex)
        {
            ConflictDetails = ex.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Apply failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsLive))]
    private async Task ReloadAsync()
    {
        if (_client is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        ConflictDetails = null;
        // A preview describes the text that produced it; reloading replaces that text.
        PendingPreview = null;
        try
        {
            var current = await _client.ReadResourceAsync(_descriptor, _namespace, _name);
            if (current is null)
            {
                IsDeleted = true;
                StatusMessage = "This resource no longer exists on the server.";
                return;
            }

            YamlText = current.ToYaml();
            IsDirty = false;
            IsDeleted = false;
            StaleNotice = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Reload failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Open-time authoritative read (see the constructor for when it fires). Failure is
    /// non-fatal — the snapshot stays on screen and <see cref="StaleNotice"/> says where
    /// it came from, because refusing to show the object at all would be worse.
    /// </summary>
    private async Task RefreshFromServerAsync()
    {
        if (_client is null)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var current = await _client.ReadResourceAsync(_descriptor, _namespace, _name);
            if (current is null)
            {
                IsDeleted = true;
                StatusMessage = "This resource no longer exists on the server.";
                return;
            }

            // An edit that started while the read was in flight outranks the server copy —
            // this tab exists so that live updates never clobber text someone is typing.
            if (IsDirty)
            {
                StaleNotice = "Edited before the server copy arrived — showing your text. Reload to discard it.";
                return;
            }

            YamlText = current.ToYaml();
            IsDirty = false;
        }
        catch (Exception ex)
        {
            StaleNotice = $"Showing the copy from the list — this object could not be read from the server: {FirstLine(ex.Message)}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// Asks for the delete. Normally that means arming the confirm step; with
    /// "Confirm before deleting" turned off it deletes outright.
    ///
    /// <para>
    /// The setting is read here, at the moment the button is pressed, rather than
    /// cached on the tab: someone who turns the confirm back on after a near-miss
    /// expects the very next delete to ask, not the next tab they open.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsLive))]
    private async Task RequestDeleteAsync()
    {
        if (!App.LoadSettings().ConfirmDeletes)
        {
            await ConfirmDeleteAsync();
            return;
        }

        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand(CanExecute = nameof(IsLive))]
    private async Task ConfirmDeleteAsync()
    {
        if (_client is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _client.DeleteResourceAsync(_descriptor, _namespace, _name);
            IsDeleted = true;
            StatusMessage = "Deleted.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsConfirmingDelete = false;
        }
    }

    /// <summary>
    /// Caught locally because the server's own complaint about a body with no
    /// apiVersion/kind ("Object 'Kind' is missing") reads like a cluster problem rather
    /// than something the editor can fix.
    /// </summary>
    private string MissingTypeMessage =>
        "Nothing was applied: this document has no apiVersion:/kind:. Reload from the server, "
        + $"or add apiVersion: {_descriptor.ApiVersion} and kind: {_descriptor.Kind}.";

    /// <summary>What the delete confirmation names, so "Confirm delete" can't be ambiguous about its target.</summary>
    public string DeleteTargetDescription => _namespace is null
        ? $"{_descriptor.Kind}/{_name}"
        : $"{_descriptor.Kind}/{_name} in namespace {_namespace}";

    public override Task OnClosingAsync()
    {
        _revealTimer?.Stop();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether the document carries top-level apiVersion:/kind:. Deliberately a scan and
    /// not a YAML parse — it runs while a tab opens and again on every apply, and the
    /// answer is "yes" for anything the API server itself serialized.
    /// </summary>
    private static bool IsSelfDescribing(string yaml) =>
        HasTopLevelKey(yaml, "apiVersion") && HasTopLevelKey(yaml, "kind");

    private static bool HasTopLevelKey(string yaml, string key) =>
        yaml.StartsWith($"{key}:", StringComparison.Ordinal)
        || yaml.Contains($"\n{key}:", StringComparison.Ordinal);

    /// <summary>Parser/HTTP messages can run to several lines; inline notices get the first one.</summary>
    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}

/// <summary>
/// The server's dry-run answer, rendered for the panel above the editor: what would
/// change, how many of those changes there were, and which button commits them.
/// Built once from a <see cref="ResourceDiff"/> and never mutated — a new preview is a
/// new instance, so nothing here needs change notification.
/// </summary>
public sealed class ApplyPreviewViewModel
{
    public ApplyPreviewViewModel(ApplyPreview preview, bool isForce)
    {
        var diff = preview.Diff;
        IsForce = isForce;
        IsCreate = diff.IsCreate;
        Rows = [.. diff.Changes.Select(c => new DiffRowViewModel(c))];

        // The line diff is over the two documents the server itself produced, with the
        // bookkeeping fields stripped from both — never over the editor's text, which
        // knows nothing about defaulting or admission (Core's ApplyPreview holds both).
        var text = TextDiff.Between(
            ResourceDiff.ToDiffableYaml(preview.Live?.Raw),
            ResourceDiff.ToDiffableYaml(preview.Previewed.Raw));

        // One collapsed row list, and the side-by-side view is derived from it rather
        // than computed a second way: two layouts built independently can disagree about
        // what changed, which is the one thing a diff may not do.
        // Nothing changed means no body at all, not one row reading "56 unchanged lines":
        // the whole document collapses to a single gap, and a gap under "this apply would
        // change nothing" is a row of dock height spent restating it.
        IReadOnlyList<TextDiffLine> collapsed = text.IsEmpty ? [] : text.Collapse();
        Lines = [.. collapsed.Select(line => new DiffLineViewModel(line))];
        SplitLines = [.. TextDiff.SideBySide(collapsed).Select(pair => new DiffPairViewModel(pair))];
        LineSummary = text.IsEmpty ? "" : $"+{text.AddedCount} −{text.RemovedCount}";
        IsEmpty = diff.IsEmpty && text.IsEmpty;

        Headline = diff switch
        {
            { IsCreate: true } => "This object is not on the server. Applying would create it:",
            // A field diff that is empty while the text one is not means the server would
            // write the same values in a different order. Saying "nothing would change"
            // over a panel showing moved lines would read as a bug in the panel.
            { IsEmpty: true } when !text.IsEmpty =>
                "The server would change no field values — the lines below differ only in how the document is ordered.",
            { IsEmpty: true } => "The server reports this apply would change nothing.",
            { TotalChanges: 1 } => "The server would make 1 change:",
            _ => $"The server would make {diff.TotalChanges} changes:",
        };

        // Both notes are about what is *not* on screen, which is exactly the kind of
        // thing a diff must say out loud rather than leave the reader to notice.
        var notes = new List<string>();
        if (diff.IsTruncated)
        {
            notes.Add($"showing the first {diff.Changes.Count} of {diff.TotalChanges}");
        }

        if (diff.HiddenBookkeepingCount > 0)
        {
            notes.Add($"{diff.HiddenBookkeepingCount} server bookkeeping "
                + $"{(diff.HiddenBookkeepingCount == 1 ? "field" : "fields")} hidden "
                + "(managedFields, resourceVersion, generation)");
        }

        // A diff that stopped aligning has to say so. The alternative — quietly showing a
        // whole section as replaced — is a wrong answer wearing the server's authority.
        if (text.IsApproximate)
        {
            notes.Add("too large to align line by line, so the changed section is shown as a replacement");
        }

        Footnote = notes.Count == 0 ? null : string.Join(" · ", notes);
    }

    /// <summary>The field-level changes — the Fields view mode, and the semantic summary
    /// the headline counts. Kept beside the text diff, not replaced by it: its
    /// list-matching by <c>name</c> is what stops an inserted container reading as six
    /// changes, and a line count could never say that.</summary>
    public IReadOnlyList<DiffRowViewModel> Rows { get; }

    /// <summary>The manifest as a unified line diff, unchanged runs already collapsed.</summary>
    public IReadOnlyList<DiffLineViewModel> Lines { get; }

    /// <summary>The same rows as two aligned columns, fillers included.</summary>
    public IReadOnlyList<DiffPairViewModel> SplitLines { get; }

    /// <summary>How many lines the diff adds and removes, e.g. <c>+7 −4</c>.</summary>
    public string LineSummary { get; }

    public string Headline { get; }

    public string? Footnote { get; }

    public bool HasFootnote => Footnote is not null;

    /// <summary>True when the server says nothing would change — the panel then has no rows to show.</summary>
    public bool IsEmpty { get; }

    /// <summary>True when the Fields view has something to list.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>
    /// True when there is a diff to show at all — which is what decides whether the panel
    /// gets a star-sized row and a view-mode strip, or is one sentence and two buttons.
    /// </summary>
    public bool HasBody => Lines.Count > 0;

    public bool IsCreate { get; }

    /// <summary>Whether the apply this previews takes conflicted fields from their current owner.</summary>
    public bool IsForce { get; }

    /// <summary>
    /// The confirm button says which apply it is. A force-apply reached through a
    /// conflict must not confirm under the same word as an ordinary one — taking fields
    /// away from another manager is the more consequential of the two.
    /// </summary>
    public string ConfirmLabel => IsForce ? "Force apply" : "Apply changes";
}

/// <summary>One line of the preview panel.</summary>
public sealed class DiffRowViewModel
{
    public DiffRowViewModel(ResourceChange change)
    {
        Path = change.Path;
        Kind = change.Kind;
        Before = change.Before;
        After = change.After;
    }

    public string Path { get; }

    public ResourceChangeKind Kind { get; }

    public string? Before { get; }

    public string? After { get; }

    /// <summary>
    /// The sign column. A glyph as well as a colour, because colour alone carries
    /// nothing for a reader who cannot tell red from green, and a diff is exactly the
    /// place where the two directions must not be guessed.
    /// </summary>
    public string Marker => Kind switch
    {
        ResourceChangeKind.Added => "+",
        ResourceChangeKind.Removed => "−",
        _ => "~",
    };

    public bool IsAdded => Kind == ResourceChangeKind.Added;

    public bool IsRemoved => Kind == ResourceChangeKind.Removed;

    public bool IsChanged => Kind == ResourceChangeKind.Changed;

    /// <summary>Only a changed value has two sides to separate.</summary>
    public bool HasBothSides => IsChanged;
}

/// <summary>
/// One row of the unified line diff: the two line numbers, the sign, and the text —
/// or, for a collapsed run, how many unchanged lines are standing behind it.
/// </summary>
public sealed class DiffLineViewModel
{
    public DiffLineViewModel(TextDiffLine line)
    {
        Kind = line.Kind;
        LeftNumber = line.LeftNumber?.ToString(CultureInfo.InvariantCulture) ?? "";
        RightNumber = line.RightNumber?.ToString(CultureInfo.InvariantCulture) ?? "";
        Text = line.Text;
        SkippedCount = line.SkippedCount;
    }

    public TextDiffKind Kind { get; }

    public string LeftNumber { get; }

    public string RightNumber { get; }

    public string Text { get; }

    public int SkippedCount { get; }

    /// <summary>
    /// The sign column, as a glyph and not only a colour — the two directions of a diff
    /// are the last thing that should depend on telling red from green.
    /// </summary>
    public string Marker => Kind switch
    {
        TextDiffKind.Added => "+",
        TextDiffKind.Removed => "−",
        _ => " ",
    };

    public bool IsAdded => Kind == TextDiffKind.Added;

    public bool IsRemoved => Kind == TextDiffKind.Removed;

    public bool IsSkipped => Kind == TextDiffKind.Skipped;

    public bool IsLine => Kind != TextDiffKind.Skipped;

    /// <summary>What a collapsed run says about itself. A gap that does not state its
    /// size is a diff quietly withholding part of the document.</summary>
    public string SkippedText => $"{SkippedCount} unchanged {(SkippedCount == 1 ? "line" : "lines")}";
}

/// <summary>
/// One row of the side-by-side diff. Either half may be absent, which is the alignment
/// filler: a deleted line on the left has to face a blank on the right.
/// </summary>
public sealed class DiffPairViewModel
{
    public DiffPairViewModel(TextDiffPair pair)
    {
        Left = pair.Left is null ? null : new DiffLineViewModel(pair.Left);
        Right = pair.Right is null ? null : new DiffLineViewModel(pair.Right);
        SkippedCount = pair.SkippedCount;
    }

    public DiffLineViewModel? Left { get; }

    public DiffLineViewModel? Right { get; }

    public int SkippedCount { get; }

    public bool HasLeft => Left is not null;

    public bool HasRight => Right is not null;

    public bool IsSkipped => SkippedCount > 0;

    public string SkippedText => $"{SkippedCount} unchanged {(SkippedCount == 1 ? "line" : "lines")}";
}

/// <summary>
/// One row of the YAML editor's decoded-values panel. Rebuilt wholesale on every
/// refresh, so it needs no change notification — and it carries its own failure text
/// rather than throwing, because one unreadable key must not blank the others.
/// </summary>
public sealed class RevealedValueViewModel
{
    /// <summary>
    /// How much of one decoded value gets rendered. The panel is a ~160px card of
    /// wrapping monospace text inside a ~300px dock, so laying out a 1 MiB value in
    /// full costs a full text layout to show a screenful; the rest is one click away.
    /// </summary>
    private const int MaxRenderedChars = 512;

    /// <summary>Enough bytes to recognise a magic number (1f 8b = gzip, 30 82 = DER, …).</summary>
    private const int BinaryPreviewBytes = 12;

    private RevealedValueViewModel(
        string key, string value, string? detail, bool isProblem, string? clipboardText, bool isPartial, bool copiesBase64)
    {
        Key = key;
        Value = value;
        Detail = detail;
        IsProblem = isProblem;
        ClipboardText = clipboardText;
        IsPartial = isPartial;
        CopiesBase64 = copiesBase64;
    }

    public string Key { get; }

    /// <summary>What renders — already capped at <see cref="MaxRenderedChars"/>.</summary>
    public string Value { get; }

    /// <summary>Why this row looks the way it does (truncated / binary / unreadable), or null.</summary>
    public string? Detail { get; }

    public bool HasDetail => Detail is not null;

    /// <summary>True when the value could not be decoded at all — colours <see cref="Detail"/> as a warning.</summary>
    public bool IsProblem { get; }

    /// <summary>The full text the copy button puts on the clipboard, or null when there's nothing worth copying.</summary>
    public string? ClipboardText { get; }

    public bool CanCopy => ClipboardText is not null;

    /// <summary>True when <see cref="Value"/> is only part of the payload, so the copy label must say "full".</summary>
    public bool IsPartial { get; }

    /// <summary>True when copying yields base64 rather than the decoded value (binary or undecodable).</summary>
    public bool CopiesBase64 { get; }

    public string CopyTooltip => CopiesBase64
        ? "Copy the base64 value (its decoded bytes aren't text)"
        : IsPartial
            ? "Copy the full value"
            : "Copy the value";

    public static RevealedValueViewModel Text(string key, string text)
    {
        var truncated = text.Length > MaxRenderedChars;
        return new RevealedValueViewModel(
            key,
            truncated ? text[..MaxRenderedChars] : text,
            truncated ? $"showing the first {MaxRenderedChars:N0} of {text.Length:N0} characters" : null,
            isProblem: false,
            clipboardText: text,
            isPartial: truncated,
            copiesBase64: false);
    }

    public static RevealedValueViewModel Binary(string key, byte[] bytes, string encoded) =>
        new(key,
            HexPreview(bytes),
            $"binary, {FormatSize(bytes.Length)} — not text",
            isProblem: false,
            clipboardText: encoded,
            isPartial: true,
            copiesBase64: true);

    public static RevealedValueViewModel Problem(string key, string detail, string? clipboardText = null) =>
        new(key, "—", detail,
            isProblem: true,
            clipboardText: clipboardText,
            isPartial: false,
            copiesBase64: clipboardText is not null);

    private static string HexPreview(byte[] bytes)
    {
        var take = Math.Min(bytes.Length, BinaryPreviewBytes);
        var builder = new StringBuilder(take * 3 + 2);
        for (var i = 0; i < take; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        if (bytes.Length > take)
        {
            builder.Append(" …");
        }

        return builder.ToString();
    }

    private static string FormatSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MiB",
    };
}
