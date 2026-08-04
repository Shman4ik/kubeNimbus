using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One entry from a container's <c>env:</c> list. A literal <see cref="DirectValue"/>
/// is shown as-is; a reference resolves according to what it points at, and the two
/// kinds are deliberately not treated the same:
/// <list type="bullet">
/// <item>A <c>configMapKeyRef</c> is <b>resolved on open</b> and shown like any other
/// value. A ConfigMap is not secret — it is ordinary configuration, and making
/// someone click "Reveal" to read `LOG_LEVEL=info` was a click charged for nothing.
/// It costs one GET per distinct ConfigMap, deduplicated by the same cache the
/// on-demand path uses.</item>
/// <item>A <c>secretKeyRef</c> stays <b>masked</b> (<see cref="MaskedValue"/>) behind
/// an eye toggle. Nothing is fetched until it is asked for: the mask is a placeholder,
/// not a hidden copy of the value, so a secret never enters this process — or a
/// screen-share — because a pane happened to be open. The identity reading the
/// kubeconfig may also have no RBAC to read Secrets while it can read Pods, and that
/// error belongs on the row someone asked about, not on four rows nobody did.</item>
/// </list>
/// </summary>
public sealed partial class EnvVarViewModel(
    string name,
    string? directValue,
    string? sourceDescription,
    string? secretOrConfigMapKind,
    string? secretOrConfigMapName,
    string? key) : ObservableObject
{
    public string Name { get; } = name;

    public string? DirectValue { get; } = directValue;

    /// <summary>e.g. "Secret/db-creds · key=password" — null for a literal value.</summary>
    public string? SourceDescription { get; } = sourceDescription;

    /// <summary>
    /// True when <see cref="DirectValue"/> came from resolving the pod object rather
    /// than from a literal in the spec — a Downward-API <c>fieldRef</c>. The value is
    /// real and shown, and <see cref="SourceDescription"/> rides under it as a caption
    /// so it stays clear where it came from.
    /// </summary>
    public bool IsDerivedValue { get; init; }

    /// <summary>
    /// True when the reference is declared <c>optional: true</c>. It matters: an
    /// optional ref whose key is missing is normal — the container simply starts
    /// without the variable — and reporting that as an error, which is what a failed
    /// reveal used to do, sends people hunting a bug that isn't there.
    /// </summary>
    public bool IsOptionalReference { get; init; }

    /// <summary>A reference: the value line is whatever resolving it produced, not a literal.</summary>
    public bool IsReference => SourceDescription is not null && DirectValue is null;

    /// <summary>The value line: a literal, or a resolved fieldRef.</summary>
    public bool HasDirectValue => DirectValue is not null;

    public bool CanReveal => SecretOrConfigMapKind is not null && SecretOrConfigMapName is not null;

    /// <summary>A Secret key: masked until asked for, and the only row with an eye toggle.</summary>
    public bool IsSecretReference => SecretOrConfigMapKind == "Secret";

    /// <summary>The stand-in shown for an unrevealed Secret. Fixed width — the length of a
    /// secret is itself worth not leaking, and a mask that tracked it would leak it.</summary>
    public string MaskedValue => "••••••••";

    internal string? SecretOrConfigMapKind { get; } = secretOrConfigMapKind;

    internal string? SecretOrConfigMapName { get; } = secretOrConfigMapName;

    internal string? Key { get; } = key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolving))]
    [NotifyPropertyChangedFor(nameof(IsMasked))]
    private bool _isRevealing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResolvedValue))]
    [NotifyPropertyChangedFor(nameof(IsMasked))]
    [NotifyPropertyChangedFor(nameof(IsResolving))]
    private string? _revealedValue;

    /// <summary>
    /// Whether the resolved value is on screen. Separate from <see cref="RevealedValue"/>
    /// being non-null so the eye can hide a value again without discarding it and
    /// re-fetching — and so that hiding it is what the button does, rather than
    /// something you can only achieve by closing the tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResolvedValue))]
    [NotifyPropertyChangedFor(nameof(IsMasked))]
    private bool _isRevealed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolving))]
    [NotifyPropertyChangedFor(nameof(IsMasked))]
    private string? _revealError;

    public bool HasResolvedValue => IsRevealed && RevealedValue is not null;

    /// <summary>A Secret whose value is not on screen: the dots stand in for it.</summary>
    public bool IsMasked => IsSecretReference && !HasResolvedValue && RevealError is null && !IsRevealing;

    /// <summary>Mid-fetch, with nothing to show yet — a ConfigMap row on open, or a Secret just asked for.</summary>
    public bool IsResolving => IsRevealing && RevealedValue is null && RevealError is null;
}
