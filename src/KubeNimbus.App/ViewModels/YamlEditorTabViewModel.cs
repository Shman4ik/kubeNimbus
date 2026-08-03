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

    private readonly ClusterClient _client;
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

    [ObservableProperty]
    private bool _isDeleted;

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

    public YamlEditorTabViewModel(
        ClusterClient client, ResourceDescriptor descriptor, string? @namespace, string name, string initialYaml,
        string clusterName = "")
        : base(clusterName.Length == 0 ? $"{descriptor.Kind}/{name}" : $"{descriptor.Kind}/{name} · {clusterName}")
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
        if (!IsSelfDescribing(initialYaml))
        {
            _ = RefreshFromServerAsync();
        }
    }

    partial void OnYamlTextChanged(string value)
    {
        IsDirty = true;
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

    [RelayCommand]
    private async Task ApplyAsync() => await ApplyCoreAsync(force: false);

    [RelayCommand]
    private async Task ForceApplyAsync() => await ApplyCoreAsync(force: true);

    private async Task ApplyCoreAsync(bool force)
    {
        // Caught locally because the server's own complaint about a body with no
        // apiVersion/kind ("Object 'Kind' is missing") reads like a cluster problem
        // rather than something the editor can fix.
        if (!IsSelfDescribing(YamlText))
        {
            StatusMessage = $"Nothing was applied: this document has no apiVersion:/kind:. Reload from the server, "
                + $"or add apiVersion: {_descriptor.ApiVersion} and kind: {_descriptor.Kind}.";
            return;
        }

        IsBusy = true;
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

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        StatusMessage = null;
        ConflictDetails = null;
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

    [RelayCommand]
    private void RequestDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
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
